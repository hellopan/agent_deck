package main

import (
	"bytes"
	"context"
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
	"strings"
	"unicode/utf8"

	"github.com/hellopan/agent_deck/internal/projects"
)

const submitMessage = "agentdeck-task-submit-v1 "
const maxSubmission = 24 * 1024

type taskSubmission struct {
	RequestID string `json:"requestId"`
	ProjectID string `json:"projectId"`
	Prompt    string `json:"prompt"`
}

type taskReceipt struct {
	TaskID string `json:"taskId,omitempty"`
	Status string `json:"status,omitempty"`
}

type inboxRecord struct {
	taskSubmission
	Directory string `json:"directory"`
}

func addSubmitOptions(flags *flag.FlagSet, opts *peerOptions) {
	flags.StringVar(&opts.inbox, "task-inbox", "", "absolute local task inbox directory (agent only)")
	flags.StringVar(&opts.submission.RequestID, "request-id", "", "stable 32-character lowercase hex ID")
	flags.StringVar(&opts.promptPath, "prompt-file", "", "local UTF-8 task prompt file (submit only)")
}

func validSubmission(s taskSubmission) bool {
	return validReceiptID(s.RequestID) &&
		strings.TrimSpace(s.Prompt) != "" && len(s.Prompt) <= 4096 && utf8.ValidString(s.Prompt)
}

// saveSubmission leaves a durable, host-local request for review. Retrying an
// identical submission returns its receipt without creating another request.
func saveSubmission(opts peerOptions, submission taskSubmission) (taskReceipt, error) {
	if opts.inbox == "" || !validSubmission(submission) {
		return taskReceipt{}, errors.New("invalid submission or inbox disabled")
	}
	directory, err := projects.Resolve(opts.policy, submission.ProjectID)
	if err != nil {
		return taskReceipt{}, err
	}
	taskID := receiptID(submission.RequestID)
	data, err := json.Marshal(inboxRecord{taskSubmission: submission, Directory: directory})
	if err != nil {
		return taskReceipt{}, err
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		return taskReceipt{}, err
	}
	defer root.Close()
	name := taskID + ".local.json"
	err = publishSubmission(root, name, data)
	if errors.Is(err, os.ErrExist) {
		existing, err := root.Open(name)
		if err != nil {
			return taskReceipt{}, err
		}
		previous, err := io.ReadAll(io.LimitReader(existing, maxSubmission+1))
		closeErr := existing.Close()
		if err != nil || !bytes.Equal(previous, data) {
			return taskReceipt{}, errors.New("task request conflicts with an existing record")
		}
		if closeErr != nil {
			return taskReceipt{}, closeErr
		}
	} else if err != nil {
		return taskReceipt{}, err
	}
	status, err := reviewStatus(root, taskID)
	if err != nil {
		return taskReceipt{}, err
	}
	return taskReceipt{TaskID: taskID, Status: status}, nil
}

func reviewStatus(root *os.Root, id string) (string, error) {
	file, err := root.Open(id + ".claimed.log")
	if errors.Is(err, os.ErrNotExist) {
		return "awaiting_local_review", nil
	}
	if err != nil {
		return "", err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, 32))
	if err != nil {
		return "", err
	}
	switch string(data) {
	case "declined":
		return "declined", nil
	case "", "claimed": // Empty markers were written by earlier versions.
		return "claimed", nil
	default:
		return "", errors.New("invalid task review marker")
	}
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

func reviewTask(ctx context.Context, config codexOptions, inbox, id, logDir string, accept, decline bool) error {
	if !filepath.IsAbs(inbox) || len(id) != 32 || strings.ToLower(id) != id {
		return errors.New("review requires an absolute inbox path and a receipt ID")
	}
	if _, err := hex.DecodeString(id); err != nil {
		return errors.New("invalid receipt ID")
	}
	root, err := os.OpenRoot(inbox)
	if err != nil {
		return err
	}
	defer root.Close()
	record, err := readInboxRecord(root, id)
	if err != nil {
		return err
	}
	if decline {
		if err := publishSubmission(root, id+".claimed.log", []byte("declined")); err != nil {
			return fmt.Errorf("record task rejection: %w", err)
		}
		return json.NewEncoder(os.Stdout).Encode(taskReceipt{TaskID: id, Status: "declined"})
	}
	if !accept {
		return json.NewEncoder(os.Stdout).Encode(record.taskSubmission)
	}
	if config.projectID != record.ProjectID {
		return errors.New("review must specify the request's project ID")
	}
	if !filepath.IsAbs(logDir) {
		return errors.New("review requires an absolute task log directory")
	}
	resolved, err := config.resolve()
	if err != nil {
		return err
	}
	if resolved.ProjectDir != record.Directory {
		return errors.New("project directory changed since submission")
	}
	if err := publishSubmission(root, id+".claimed.log", []byte("claimed")); err != nil {
		return fmt.Errorf("claim task for one-time execution: %w", err)
	}
	return runResolvedTask(ctx, resolved, config, record.Prompt, logDir, id)
}

type inboxItem struct {
	TaskID    string `json:"taskId"`
	ProjectID string `json:"projectId"`
}

// listInbox exposes only pending metadata. Review reveals the prompt on demand.
func listInbox(inbox string) ([]inboxItem, error) {
	if !filepath.IsAbs(inbox) {
		return nil, errors.New("task inbox requires an absolute path")
	}
	root, err := os.OpenRoot(inbox)
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
	items := make([]inboxItem, 0)
	for _, entry := range entries {
		id, ok := strings.CutSuffix(entry.Name(), ".local.json")
		if !ok || len(id) != 32 || entry.Type() != 0 {
			continue
		}
		if _, err := hex.DecodeString(id); err != nil {
			continue
		}
		status, err := reviewStatus(root, id)
		if err != nil {
			return nil, err
		}
		if status != "awaiting_local_review" {
			continue
		}
		record, err := readInboxRecord(root, id)
		if err != nil {
			return nil, err
		}
		items = append(items, inboxItem{TaskID: id, ProjectID: record.ProjectID})
	}
	return items, nil
}

func serveSubmission(conn io.Writer, opts peerOptions, raw string, report func(error)) {
	var submission taskSubmission
	receipt := taskReceipt{}
	if err := json.Unmarshal([]byte(raw), &submission); err == nil {
		receipt, err = saveSubmission(opts, submission)
		if err != nil {
			report(err)
		}
	}
	json.NewEncoder(conn).Encode(receipt)
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
		!validReviewStatus(receipt.Status) {
		return errors.New("task receipt unavailable; retry with the same request ID and prompt")
	}
	return json.NewEncoder(os.Stdout).Encode(receipt)
}
