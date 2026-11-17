package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/hellopan/agent_deck/internal/approvals"
	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

// approvalTimeout bounds one tool request. An unanswered request is denied so
// the turn can continue instead of stalling until the task is cancelled.
const approvalTimeout = 5 * time.Minute

// scanInterval is how often an idle Agent looks for queued work.
const scanInterval = time.Second

const codexAdapterID = "codex"

// agentRuntime owns everything that outlives a single phone connection: the
// queue consumer, the pending approvals and the task that is currently running.
type agentRuntime struct {
	peer    peerOptions
	device  string
	logger  *log.Logger
	trust   *x509.CertPool
	pending approvals.Pending

	mu      sync.Mutex
	running *runningTask
}

// runningTask is the live part of the queue: what the phone shows as running and
// what an approval belongs to.
type runningTask struct {
	TaskID     string    `json:"taskId"`
	ProjectID  string    `json:"projectId"`
	Started    time.Time `json:"startedAt"`
	WaitingFor string    `json:"waitingFor,omitempty"`
}

type approvalDetail struct {
	codexprobe.Approval
	TaskID string `json:"taskId"`
}

type approvalItem struct {
	ID          string    `json:"id"`
	TaskID      string    `json:"taskId"`
	ProjectID   string    `json:"projectId"`
	AdapterID   string    `json:"adapterId"`
	Kind        string    `json:"kind"`
	Command     string    `json:"command,omitempty"`
	Cwd         string    `json:"cwd,omitempty"`
	Reason      string    `json:"reason,omitempty"`
	Expires     time.Time `json:"expiresAt"`
	SecondsLeft int       `json:"secondsLeft"`
}

type approvalSnapshot struct {
	Approvals []approvalItem `json:"approvals"`
	Running   *runningTask   `json:"running,omitempty"`
}

type queueView struct {
	Items []queueItem `json:"items"`
}

type decisionRequest struct {
	ApprovalID string `json:"approvalId"`
	Decision   string `json:"decision"`
}

type decisionReply struct {
	OK    bool   `json:"ok"`
	Error string `json:"error,omitempty"`
}

// serve answers exactly one request per tunnel. The queue consumer keeps
// running in the background, so a phone reconnects to poll rather than to hold
// a stream open.
func (a *agentRuntime) serve(conn *tls.Conn) {
	conn.SetDeadline(time.Now().Add(5 * time.Second))
	line, err := bufio.NewReaderSize(conn, maxSubmission).ReadSlice('\n')
	if err != nil {
		return
	}
	peerID := ""
	if a.peer.pairing != "" {
		phone := conn.ConnectionState().PeerCertificates[0]
		if code, ok := strings.CutPrefix(string(line), pairingMessage); ok {
			paired := submitPairing(a.peer.pairing, a.device, code, phone) == nil
			json.NewEncoder(conn).Encode(map[string]bool{"pending": paired})
			return
		}
		if !authorizedPhone(a.peer.pairing, a.device, phone, a.trust) {
			fmt.Fprintln(conn, `{"error":"phone not paired"}`)
			return
		}
		peerID = pairingFingerprint(phone)
	}
	switch string(line) {
	case probeMessage + "\n":
		if _, err := fmt.Fprintln(conn, probeMessage+":"+a.device); err == nil {
			a.logger.Printf("device %s answered an authenticated probe", a.device)
		}
		return
	case projectsMessage + "\n":
		if a.peer.policy == "" {
			fmt.Fprintln(conn, `{"error":"project policy unavailable"}`)
			return
		}
		ids, err := projects.List(a.peer.policy)
		if err != nil {
			a.logger.Printf("project listing failed: %v", err)
			fmt.Fprintln(conn, `{"error":"project policy unavailable"}`)
			return
		}
		json.NewEncoder(conn).Encode(ids)
		return
	case queueMessage + "\n":
		json.NewEncoder(conn).Encode(queueView{Items: a.queue()})
		return
	case approvalsMessage + "\n":
		json.NewEncoder(conn).Encode(a.approvals())
		return
	}
	if query, ok := strings.CutPrefix(string(line), taskLogMessage); ok {
		serveTaskLog(conn, a.peer, query)
		return
	}
	if request, ok := strings.CutPrefix(string(line), remoteCancelMessage); ok {
		serveRemoteCancel(conn, a.peer, request)
		return
	}
	if request, ok := strings.CutPrefix(string(line), receiptMessage); ok {
		serveReceipt(conn, a.peer, request)
		return
	}
	if request, ok := strings.CutPrefix(string(line), submitMessage); ok {
		a.submit(conn, peerID, request)
		return
	}
	if request, ok := strings.CutPrefix(string(line), decideMessage); ok {
		a.decide(conn, peerID, request)
	}
}

func (a *agentRuntime) submit(conn io.Writer, peerID, raw string) {
	var submission taskSubmission
	receipt := taskReceipt{}
	if err := json.Unmarshal([]byte(raw), &submission); err == nil {
		var err error
		receipt, err = saveSubmission(a.peer, peerID, submission)
		if err != nil {
			a.logger.Printf("task submission refused: %v", err)
		}
	}
	json.NewEncoder(conn).Encode(receipt)
}

// decide applies a phone decision. The caller already proved possession of a
// paired key; the approval table additionally refuses any other peer.
func (a *agentRuntime) decide(conn io.Writer, peerID, raw string) {
	var request decisionRequest
	reply := decisionReply{}
	if err := json.Unmarshal([]byte(raw), &request); err != nil || !validReceiptID(request.ApprovalID) ||
		(request.Decision != string(approvals.AcceptOnce) && request.Decision != string(approvals.Decline)) {
		reply.Error = "invalid approval decision"
		json.NewEncoder(conn).Encode(reply)
		return
	}
	if err := a.pending.Decide(peerID, request.ApprovalID, approvals.Decision(request.Decision)); err != nil {
		reply.Error = err.Error()
	} else {
		reply.OK = true
		a.logger.Printf("approval %s decided %s", shortID(request.ApprovalID), request.Decision)
	}
	json.NewEncoder(conn).Encode(reply)
}

func (a *agentRuntime) queue() []queueItem {
	items, err := listQueue(a.peer)
	if err != nil {
		a.logger.Printf("task queue unavailable: %v", err)
		return []queueItem{}
	}
	return items
}

func (a *agentRuntime) approvals() approvalSnapshot {
	requests := a.pending.List()
	now := time.Now()
	items := make([]approvalItem, 0, len(requests))
	for _, request := range requests {
		var detail approvalDetail
		if len(request.Detail) > 0 {
			_ = json.Unmarshal(request.Detail, &detail)
		}
		seconds := int(request.Expires.Sub(now).Seconds())
		if seconds < 0 {
			seconds = 0
		}
		items = append(items, approvalItem{
			ID: request.ID, TaskID: detail.TaskID, ProjectID: request.Scope.ProjectID,
			AdapterID: request.Scope.AdapterID, Kind: detail.Kind,
			Command: detail.Command, Cwd: detail.Cwd, Reason: detail.Reason,
			Expires: request.Expires, SecondsLeft: seconds,
		})
	}
	return approvalSnapshot{Approvals: items, Running: a.current()}
}

func (a *agentRuntime) current() *runningTask {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.running == nil {
		return nil
	}
	copied := *a.running
	return &copied
}

func (a *agentRuntime) setRunning(task *runningTask) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.running = task
}

func (a *agentRuntime) setWaiting(command string) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.running != nil {
		a.running.WaitingFor = command
	}
}

// run consumes the queue one task at a time. Nothing else in the process may
// start a request, which is what keeps the read-only sandbox trustworthy.
func (a *agentRuntime) run(ctx context.Context) {
	if a.peer.inbox == "" || a.peer.logDir == "" {
		a.logger.Print("task queue disabled: an inbox and task log directory are required")
		return
	}
	for ctx.Err() == nil {
		task, err := nextQueuedTask(a.peer)
		if err != nil {
			a.logger.Printf("task queue unavailable: %v", err)
		}
		if task == nil {
			select {
			case <-ctx.Done():
				return
			case <-time.After(scanInterval):
			}
			continue
		}
		a.execute(ctx, task)
	}
}

func (a *agentRuntime) execute(ctx context.Context, task *queuedTask) {
	root, err := os.OpenRoot(a.peer.inbox)
	if err != nil {
		a.logger.Printf("task %s: %v", shortID(task.TaskID), err)
		return
	}
	defer root.Close()
	// Claim first so that a request is never started twice.
	if err := writeMarker(root, task.TaskID+".claimed.log"); err != nil {
		a.logger.Printf("task %s cannot be claimed: %v", shortID(task.TaskID), err)
		return
	}
	journal, err := codexprobe.OpenNewJournal(a.peer.logDir, task.Record.ProjectID, task.Record.Directory, task.TaskID)
	if err != nil {
		a.logger.Printf("task %s journal unavailable: %v", shortID(task.TaskID), err)
		return
	}
	defer journal.Close()
	policy := a.peer.codex
	policy.policy = a.peer.policy
	policy.projectID = task.Record.ProjectID
	resolved, source, err := policy.resolve()
	if err != nil || resolved.ProjectDir != task.Record.Directory {
		a.logger.Printf("task %s can no longer start in its authorized project", shortID(task.TaskID))
		if err := journal.Finish("failed"); err != nil {
			a.logger.Printf("task %s final status: %v", shortID(task.TaskID), err)
		}
		return
	}
	a.logger.Printf("task %s uses Codex %s (source: %s)", shortID(task.TaskID), resolved.Executable, source)
	a.setRunning(&runningTask{TaskID: task.TaskID, ProjectID: task.Record.ProjectID, Started: time.Now()})
	defer a.setRunning(nil)
	a.logger.Printf("task %s started in %s", shortID(task.TaskID), task.Record.ProjectID)

	var threadID string
	status, runErr := runWhileAuthorized(ctx, policy, resolved.ProjectDir, resolved.SandboxName(), func(runCtx context.Context, check func() error) (string, error) {
		run, err := codexprobe.RunTask(runCtx, resolved, task.Record.ThreadID, task.Record.Prompt, func(event codexprobe.TaskEvent) error {
			if err := check(); err != nil {
				return err
			}
			return journal.Append(event)
		}, func(runCtx context.Context, approval codexprobe.Approval) (bool, error) {
			return a.awaitDecision(runCtx, task, approval)
		})
		if run.ThreadID != "" && run.ThreadID != task.Record.ThreadID {
			threadID = run.ThreadID
			// Remember the conversation before the journal closes, so the phone
			// can offer a follow-up even if this turn failed.
			if err := writeThreadMarker(root, task.TaskID, task.Record.ProjectID, run.ThreadID); err != nil {
				a.logger.Printf("task %s conversation marker: %v", shortID(task.TaskID), err)
			}
		}
		return run.Status, err
	}, func() (bool, error) {
		// A queued request is stopped by the inbox marker, a running one by the
		// journal marker; a remote cancellation writes both.
		if requested, err := journal.CancellationRequested(); err != nil || requested {
			return requested, err
		}
		return cancelRequested(a.peer.inbox, task.TaskID)
	})

	switch {
	case errors.Is(runErr, errProjectRevoked), errors.Is(runErr, errTaskCanceled):
		status = "interrupted"
	case status == "":
		status = "failed"
		if ctx.Err() != nil {
			status = "interrupted"
		}
	}
	if errors.Is(runErr, codexprobe.ErrThreadUnavailable) {
		// Tell the phone why the follow-up could not run: a new thread here
		// would answer a question the model never saw.
		if err := journal.Append(codexprobe.TaskEvent{
			Type: "task/notice", ThreadID: threadID,
			Text: "上一轮会话已经无法续接，这一句没有执行。重新提交一个任务会开新会话。",
		}); err != nil {
			a.logger.Printf("task %s notice: %v", shortID(task.TaskID), err)
		}
		a.logger.Printf("task %s conversation unavailable: %v", shortID(task.TaskID), runErr)
	}
	if err := journal.Finish(status); err != nil {
		a.logger.Printf("task %s final status: %v", shortID(task.TaskID), err)
	}
	a.logger.Printf("task %s ended %s", shortID(task.TaskID), status)
}

// awaitDecision publishes one tool request to the paired phone and blocks until
// it answers, expires or the task is stopped.
func (a *agentRuntime) awaitDecision(ctx context.Context, task *queuedTask, approval codexprobe.Approval) (bool, error) {
	scope := approvals.Scope{
		DeviceID:  a.device,
		PeerID:    task.Record.PeerID,
		ProjectID: task.Record.ProjectID,
		AdapterID: codexAdapterID,
		SessionID: approval.ThreadID,
		RunID:     approval.TurnID,
	}
	detail, err := json.Marshal(approvalDetail{Approval: approval, TaskID: task.TaskID})
	if err != nil {
		return false, err
	}
	id, decisions, err := a.pending.Open(scope, detail, time.Now().Add(approvalTimeout), true)
	if err != nil {
		return false, err
	}
	defer a.pending.Close(id)
	a.setWaiting(approval.Summary())
	defer a.setWaiting("")
	a.logger.Printf("task %s waits for approval %s", shortID(task.TaskID), shortID(id))
	select {
	case decision, ok := <-decisions:
		if !ok {
			a.logger.Printf("approval %s expired or withdrawn", shortID(id))
			return false, nil
		}
		return decision == approvals.AcceptOnce, nil
	case <-ctx.Done():
		return false, ctx.Err()
	}
}

func cancelRequested(inbox, id string) (bool, error) {
	root, err := os.OpenRoot(inbox)
	if err != nil {
		return false, err
	}
	defer root.Close()
	_, canceled, err := readMarker(root, id+".cancel.log")
	return canceled, err
}

func shortID(id string) string {
	if len(id) > 8 {
		return id[:8]
	}
	return id
}
