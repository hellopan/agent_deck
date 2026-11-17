package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

const submitMessage = "agentdeck-task-submit-v1 "
const maxSubmission = 24 * 1024

// A stored request moves queued -> running -> one terminal status. Nothing
// waits for a human at the keyboard: the queue is consumed by the Agent and
// only tool requests stop for an approval.
const (
	statusQueued   = "queued"
	statusRunning  = "running"
	statusCanceled = "canceled"
)

// staleClaim is how long a claimed request may sit without a journal before it
// is reported as failed. It bounds the damage of a crash between the two.
const staleClaim = 2 * time.Minute

type taskSubmission struct {
	RequestID string `json:"requestId"`
	ProjectID string `json:"projectId"`
	Prompt    string `json:"prompt"`
	// ThreadID continues an existing conversation. An empty value starts a new
	// one, which is why the receipt for a first turn cannot carry it.
	ThreadID string `json:"threadId,omitempty"`
}

type taskReceipt struct {
	TaskID string `json:"taskId,omitempty"`
	Status string `json:"status,omitempty"`
}

type inboxRecord struct {
	taskSubmission
	Directory string `json:"directory"`
	// PeerID is the paired phone that submitted the request. It scopes the
	// approvals raised while the task runs.
	PeerID string `json:"peerId,omitempty"`
}

// queueItem is what the queue view returns to a paired phone and to the tray.
type queueItem struct {
	TaskID    string    `json:"taskId"`
	ProjectID string    `json:"projectId"`
	Directory string    `json:"directory"`
	Status    string    `json:"status"`
	Submitted time.Time `json:"submittedAt"`
	Summary   string    `json:"summary"`
	Prompt    string    `json:"prompt"`
	// ThreadID is the conversation this turn belongs to. It is empty until a
	// first turn has actually run, because only Codex can name the thread.
	ThreadID string `json:"threadId,omitempty"`
	// Sandbox is the project's current mode. It is read from the policy on each
	// listing, so changing a project to read-only shows up in the queue at once.
	Sandbox string `json:"sandbox"`
}

type queuedTask struct {
	TaskID string
	Record inboxRecord
}

func addSubmitOptions(flags *flag.FlagSet, opts *peerOptions) {
	flags.StringVar(&opts.inbox, "task-inbox", "", "absolute local task inbox directory (agent only)")
	flags.StringVar(&opts.submission.RequestID, "request-id", "", "stable 32-character lowercase hex ID")
	flags.StringVar(&opts.promptPath, "prompt-file", "", "local UTF-8 task prompt file (submit only)")
	flags.StringVar(&opts.submission.ThreadID, "thread-id", "", "conversation to continue; omit to start a new one")
}

func validSubmission(s taskSubmission) bool {
	return validReceiptID(s.RequestID) &&
		strings.TrimSpace(s.Prompt) != "" && len(s.Prompt) <= 4096 && utf8.ValidString(s.Prompt) &&
		(s.ThreadID == "" || codexprobe.ValidThreadID(s.ThreadID))
}

func validTaskStatus(status string) bool {
	return status == statusQueued || status == statusRunning || status == statusCanceled ||
		codexprobe.IsFinalTaskStatus(status)
}

// saveSubmission leaves a durable, host-local request for the queue. Retrying an
// identical submission returns its receipt without creating another request.
func saveSubmission(opts peerOptions, peerID string, submission taskSubmission) (taskReceipt, error) {
	if opts.inbox == "" || !validSubmission(submission) {
		return taskReceipt{}, errors.New("invalid submission or inbox disabled")
	}
	directory, err := projects.Resolve(opts.policy, submission.ProjectID)
	if err != nil {
		return taskReceipt{}, err
	}
	taskID := receiptID(submission.RequestID)
	data, err := json.Marshal(inboxRecord{taskSubmission: submission, Directory: directory, PeerID: peerID})
	if err != nil {
		return taskReceipt{}, err
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		return taskReceipt{}, err
	}
	defer root.Close()
	if submission.ThreadID != "" && !knownThread(root, submission.ProjectID, submission.ThreadID) {
		return taskReceipt{}, errors.New("the conversation does not belong to an earlier turn in this project")
	}
	name := taskID + ".local.json"
	err = publishSubmission(root, name, data)
	if errors.Is(err, os.ErrExist) {
		existing, readErr := readInboxRecord(root, taskID)
		if readErr != nil {
			return taskReceipt{}, readErr
		}
		if existing.taskSubmission != submission {
			return taskReceipt{}, errors.New("task request conflicts with an existing record")
		}
	} else if err != nil {
		return taskReceipt{}, err
	}
	status, err := taskState(root, opts.logDir, submission.ProjectID, directory, taskID)
	if err != nil {
		return taskReceipt{}, err
	}
	return taskReceipt{TaskID: taskID, Status: status}, nil
}

// taskState derives the queue state from the markers this package owns and the
// journal the runner writes.
func taskState(root *os.Root, logDir, projectID, directory, id string) (string, error) {
	claimedAt, claimed, err := readMarker(root, id+".claimed.log")
	if err != nil {
		return "", err
	}
	if !claimed {
		if _, canceled, err := readMarker(root, id+".cancel.log"); err != nil {
			return "", err
		} else if canceled {
			return statusCanceled, nil
		}
		return statusQueued, nil
	}
	if logDir == "" {
		return statusRunning, nil
	}
	terminal, err := codexprobe.FinalJournalStatus(logDir, id, projectID, directory)
	if errors.Is(err, os.ErrNotExist) {
		// The claim outlived the process that took it.
		if time.Since(claimedAt) > staleClaim {
			return "failed", nil
		}
		return statusRunning, nil
	}
	if err != nil {
		return "", err
	}
	if terminal != "" {
		return terminal, nil
	}
	return statusRunning, nil
}

func readMarker(root *os.Root, name string) (time.Time, bool, error) {
	file, err := root.Open(name)
	if errors.Is(err, os.ErrNotExist) {
		return time.Time{}, false, nil
	}
	if err != nil {
		return time.Time{}, false, err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, 64))
	if err != nil {
		return time.Time{}, false, err
	}
	at, err := time.Parse(time.RFC3339, strings.TrimSpace(string(data)))
	if err != nil {
		// A marker from an older build carries no timestamp.
		return time.Time{}, true, nil
	}
	return at, true, nil
}

func writeMarker(root *os.Root, name string) error {
	err := publishSubmission(root, name, []byte(time.Now().UTC().Format(time.RFC3339)))
	if errors.Is(err, os.ErrExist) {
		return nil
	}
	return err
}

// threadMarker records the conversation a turn ran in. A submission record is
// published once and never rewritten, and the thread id is only known after
// Codex answers, so the id lives in its own file.
type threadMarker struct {
	ThreadID  string `json:"threadId"`
	ProjectID string `json:"projectId"`
}

func writeThreadMarker(root *os.Root, id, projectID, threadID string) error {
	if !codexprobe.ValidThreadID(threadID) || projectID == "" {
		return errors.New("invalid conversation marker")
	}
	data, err := json.Marshal(threadMarker{ThreadID: threadID, ProjectID: projectID})
	if err != nil {
		return err
	}
	err = publishSubmission(root, id+".thread.json", data)
	if errors.Is(err, os.ErrExist) {
		return nil
	}
	return err
}

func readThreadMarker(root *os.Root, id string) (threadMarker, bool, error) {
	var mark threadMarker
	file, err := root.Open(id + ".thread.json")
	if errors.Is(err, os.ErrNotExist) {
		return mark, false, nil
	}
	if err != nil {
		return mark, false, err
	}
	data, readErr := io.ReadAll(io.LimitReader(file, 1024))
	if err := errors.Join(readErr, file.Close()); err != nil {
		return mark, false, err
	}
	if json.Unmarshal(data, &mark) != nil || !codexprobe.ValidThreadID(mark.ThreadID) || mark.ProjectID == "" {
		return threadMarker{}, false, nil
	}
	return mark, true, nil
}

// knownThread proves a continuation targets a conversation this Agent started in
// the same project, so a phone cannot ask it to resume an arbitrary thread.
func knownThread(root *os.Root, projectID, threadID string) bool {
	dir, err := root.Open(".")
	if err != nil {
		return false
	}
	defer dir.Close()
	entries, err := dir.ReadDir(-1)
	if err != nil {
		return false
	}
	for _, entry := range entries {
		id, ok := strings.CutSuffix(entry.Name(), ".thread.json")
		if !ok || !validReceiptID(id) {
			continue
		}
		if mark, ok, err := readThreadMarker(root, id); err == nil && ok &&
			mark.ProjectID == projectID && mark.ThreadID == threadID {
			return true
		}
	}
	return false
}

func publishSubmission(root *os.Root, name string, data []byte) error {
	var nonce [16]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		return err
	}
	staging := "inbox-" + hex.EncodeToString(nonce[:]) + ".local.json"
	file, err := root.OpenFile(staging, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	defer root.Remove(staging)
	_, writeErr := file.Write(data)
	if writeErr == nil {
		writeErr = file.Sync()
	}
	if err := errors.Join(writeErr, file.Close()); err != nil {
		return err
	}
	// Publish the complete record atomically without replacing a prior submission.
	return root.Link(staging, name)
}

func receiptID(requestID string) string {
	id := sha256.Sum256([]byte(requestID))
	return hex.EncodeToString(id[:16])
}

func readInboxRecord(root *os.Root, id string) (inboxRecord, error) {
	var record inboxRecord
	file, err := root.Open(id + ".local.json")
	if err != nil {
		return record, err
	}
	data, readErr := io.ReadAll(io.LimitReader(file, maxSubmission+1))
	if err := errors.Join(readErr, file.Close()); err != nil {
		return record, err
	}
	if len(data) > maxSubmission || json.Unmarshal(data, &record) != nil || !validSubmission(record.taskSubmission) ||
		receiptID(record.RequestID) != id {
		return inboxRecord{}, errors.New("invalid inbox record")
	}
	return record, nil
}

func promptSummary(prompt string) string {
	line := strings.TrimSpace(prompt)
	if index := strings.IndexByte(line, '\n'); index >= 0 {
		line = strings.TrimSpace(line[:index])
	}
	if runes := []rune(line); len(runes) > 96 {
		return string(runes[:96]) + "…"
	}
	return line
}

// listQueue reports every stored request the caller may still see. Requests for
// projects that are no longer authorized are omitted rather than shown as
// failures of the queue.
func listQueue(opts peerOptions) ([]queueItem, error) {
	if !filepath.IsAbs(opts.inbox) {
		return nil, errors.New("task inbox requires an absolute path")
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		return nil, err
	}
	defer root.Close()
	dir, err := root.Open(".")
	if err != nil {
		return nil, err
	}
	defer dir.Close()
	entries, err := dir.ReadDir(-1)
	if err != nil {
		return nil, err
	}
	items := make([]queueItem, 0)
	for _, entry := range entries {
		id, ok := strings.CutSuffix(entry.Name(), ".local.json")
		if !ok || !validReceiptID(id) || entry.Type() != 0 {
			continue
		}
		record, err := readInboxRecord(root, id)
		if err != nil {
			return nil, err
		}
		_, mode, err := projects.ResolveMode(opts.policy, record.ProjectID)
		if err != nil {
			continue
		}
		status, err := taskState(root, opts.logDir, record.ProjectID, record.Directory, id)
		if err != nil {
			return nil, err
		}
		info, err := entry.Info()
		if err != nil {
			return nil, err
		}
		threadID := record.ThreadID
		if threadID == "" {
			if mark, ok, err := readThreadMarker(root, id); err != nil {
				return nil, err
			} else if ok {
				threadID = mark.ThreadID
			}
		}
		items = append(items, queueItem{
			TaskID: id, ProjectID: record.ProjectID, Directory: record.Directory,
			Status: status, Submitted: info.ModTime().UTC(), Summary: promptSummary(record.Prompt),
			Prompt: record.Prompt, ThreadID: threadID, Sandbox: string(mode),
		})
	}
	sort.Slice(items, func(a, b int) bool {
		if items[a].Submitted.Equal(items[b].Submitted) {
			return items[a].TaskID < items[b].TaskID
		}
		return items[a].Submitted.After(items[b].Submitted)
	})
	return items, nil
}

// nextQueuedTask returns the oldest request nobody has taken yet.
func nextQueuedTask(opts peerOptions) (*queuedTask, error) {
	if opts.inbox == "" || opts.logDir == "" {
		return nil, nil
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		return nil, err
	}
	defer root.Close()
	dir, err := root.Open(".")
	if err != nil {
		return nil, err
	}
	defer dir.Close()
	entries, err := dir.ReadDir(-1)
	if err != nil {
		return nil, err
	}
	var next *queuedTask
	var oldest time.Time
	for _, entry := range entries {
		id, ok := strings.CutSuffix(entry.Name(), ".local.json")
		if !ok || !validReceiptID(id) || entry.Type() != 0 {
			continue
		}
		if _, claimed, err := readMarker(root, id+".claimed.log"); err != nil || claimed {
			continue
		}
		if _, canceled, err := readMarker(root, id+".cancel.log"); err != nil || canceled {
			continue
		}
		info, err := entry.Info()
		if err != nil || (next != nil && !info.ModTime().Before(oldest)) {
			continue
		}
		record, err := readInboxRecord(root, id)
		if err != nil {
			continue
		}
		if directory, err := projects.Resolve(opts.policy, record.ProjectID); err != nil || directory != record.Directory {
			continue
		}
		next = &queuedTask{TaskID: id, Record: record}
		oldest = info.ModTime()
	}
	return next, nil
}

func sendSubmission(conn io.ReadWriter, opts peerOptions) error {
	prompt, err := readPromptFile(opts.promptPath, 4096)
	if err != nil {
		return err
	}
	submission := opts.submission
	submission.Prompt = string(prompt)
	if !validSubmission(submission) || submission.ProjectID == "" {
		return errors.New("submit requires an authorized project, a request ID and a UTF-8 prompt of up to 4096 bytes")
	}
	raw, err := json.Marshal(submission)
	if err != nil || len(raw)+len(submitMessage)+1 > maxSubmission {
		return errors.New("encoded task request is too large")
	}
	if _, err := fmt.Fprintf(conn, "%s%s\n", submitMessage, raw); err != nil {
		return err
	}
	var receipt taskReceipt
	if err := json.NewDecoder(io.LimitReader(conn, 1024)).Decode(&receipt); err != nil || receipt.TaskID != receiptID(submission.RequestID) ||
		!validTaskStatus(receipt.Status) {
		return errors.New("task receipt unavailable; retry with the same request ID and prompt")
	}
	return json.NewEncoder(os.Stdout).Encode(receipt)
}
