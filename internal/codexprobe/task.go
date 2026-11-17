package codexprobe

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"unicode/utf8"
)

// TaskEvent contains only fields intended for the task timeline.
type TaskEvent struct {
	Type     string `json:"type"`
	ThreadID string `json:"threadId"`
	TurnID   string `json:"turnId"`
	Text     string `json:"text,omitempty"`
	Status   string `json:"status,omitempty"`
	At       string `json:"at,omitempty"`
}

// TaskRun is one turn and the conversation it ran in. The caller stores ThreadID
// so a later turn can resume the same conversation instead of starting over.
type TaskRun struct {
	Status   string
	ThreadID string
	TurnID   string
}

// ErrThreadUnavailable reports a conversation that cannot be resumed. The turn
// fails rather than silently starting a new thread, because a fresh thread would
// lose the context the follow-up depends on.
var ErrThreadUnavailable = errors.New("the conversation can no longer be resumed")

// RunTask runs one local turn in the sandbox the config names. An empty
// threadID starts a new conversation; a non-empty one resumes it, so the model
// keeps everything said in earlier turns. Anything the sandbox refuses is
// offered to decide before the turn continues, so the caller owns both the
// authorization and the approval surface. This is not a remote execution API.
func RunTask(ctx context.Context, config Config, threadID, prompt string, emit func(TaskEvent) error, decide Decider) (TaskRun, error) {
	if strings.TrimSpace(prompt) == "" || len(prompt) > 16*1024 || !utf8.ValidString(prompt) || emit == nil || decide == nil {
		return TaskRun{}, errors.New("task requires a UTF-8 prompt of at most 16 KiB, an event handler and an approval decider")
	}
	if threadID != "" && !ValidThreadID(threadID) {
		return TaskRun{}, ErrThreadUnavailable
	}
	var run TaskRun
	err := withAppServer(ctx, config, func(writer *json.Encoder, reader *bufio.Scanner) error {
		var err error
		run, err = runTurn(ctx, config, threadID, prompt, writer, reader, emit, decide)
		return err
	})
	return run, err
}

// ValidThreadID accepts the UUID shape Codex returns and nothing else, so a
// malformed value is refused before a process is started.
func ValidThreadID(id string) bool {
	if len(id) != 36 {
		return false
	}
	for i, c := range id {
		if i == 8 || i == 13 || i == 18 || i == 23 {
			if c != '-' {
				return false
			}
			continue
		}
		if !('0' <= c && c <= '9') && !('a' <= c && c <= 'f') && !('A' <= c && c <= 'F') {
			return false
		}
	}
	return true
}

// openSession creates or resumes the conversation this turn runs in. Resuming
// keeps every earlier turn; starting keeps a fresh one. Both pin the sandbox and
// the per-request approval policy, because a resumed thread would otherwise run
// with whatever the stored session was created with.
func openSession(ctx context.Context, directory, sandbox, threadID string, writer *json.Encoder, reader *bufio.Scanner) (string, error) {
	params := map[string]any{"cwd": directory, "sandbox": sandbox, "approvalPolicy": "on-request"}
	method := "thread/start"
	if threadID == "" {
		// A thread that is not stored has no rollout, and a thread without a
		// rollout can never be resumed. Confirmed against Codex 0.167.
		params["ephemeral"] = false
	} else {
		method = "thread/resume"
		params["threadId"] = threadID
	}
	request := message{Method: method, ID: 2, Params: params}
	if err := writer.Encode(request); err != nil {
		return "", err
	}
	raw, err := readResponse(ctx, reader, request)
	if err != nil {
		if threadID != "" {
			return "", fmt.Errorf("%w: %v", ErrThreadUnavailable, err)
		}
		return "", err
	}
	var thread struct {
		Thread struct{ ID string } `json:"thread"`
	}
	if err := json.Unmarshal(raw, &thread); err != nil || thread.Thread.ID == "" {
		return "", errors.New("Codex returned an invalid thread")
	}
	if threadID != "" && thread.Thread.ID != threadID {
		return "", fmt.Errorf("%w: Codex resumed a different thread", ErrThreadUnavailable)
	}
	return thread.Thread.ID, nil
}

func runTurn(ctx context.Context, config Config, threadID, prompt string, writer *json.Encoder, reader *bufio.Scanner, emit func(TaskEvent) error, decide Decider) (TaskRun, error) {
	threadID, err := openSession(ctx, config.ProjectDir, config.SandboxName(), threadID, writer, reader)
	if err != nil {
		return TaskRun{}, err
	}
	if err := writer.Encode(message{Method: "turn/start", ID: 3, Params: map[string]any{
		"threadId": threadID, "input": []map[string]string{{"type": "text", "text": prompt}},
	}}); err != nil {
		return TaskRun{}, err
	}

	var turnID string
	for reader.Scan() {
		var packet struct {
			response
			Params struct {
				approvalRequest
				Delta string                      `json:"delta"`
				Turn  struct{ ID, Status string } `json:"turn"`
			} `json:"params"`
		}
		if err := json.Unmarshal(reader.Bytes(), &packet); err != nil {
			return TaskRun{}, errors.New("invalid Codex task message")
		}
		if packet.Method != "" && len(packet.ID) > 0 {
			if err := serveApproval(ctx, packet.Method, packet.ID, packet.Params.approvalRequest, threadID, turnID, writer, emit, decide); err != nil {
				return TaskRun{}, err
			}
			continue
		}
		if packet.Method == "" && string(packet.ID) == "3" {
			if packet.Error != nil {
				return TaskRun{}, fmt.Errorf("Codex turn/start returned error code %d", packet.Error.Code)
			}
			var started struct {
				Turn struct{ ID string } `json:"turn"`
			}
			if err := json.Unmarshal(packet.Result, &started); err != nil || started.Turn.ID == "" || (turnID != "" && turnID != started.Turn.ID) {
				return TaskRun{}, errors.New("Codex returned an invalid turn")
			}
			turnID = started.Turn.ID
			continue
		}
		p := packet.Params
		if p.ThreadID != threadID {
			continue
		}
		switch packet.Method {
		case "turn/started":
			if turnID == "" {
				turnID = p.Turn.ID
			}
		case "item/agentMessage/delta", "item/commandExecution/outputDelta":
			if turnID != "" && p.TurnID == turnID {
				if err := emit(TaskEvent{Type: packet.Method, ThreadID: threadID, TurnID: turnID, Text: p.Delta}); err != nil {
					return TaskRun{}, err
				}
			}
		case "turn/completed":
			if turnID != "" && p.Turn.ID == turnID {
				switch p.Turn.Status {
				case "completed", "failed", "interrupted":
					run := TaskRun{Status: p.Turn.Status, ThreadID: threadID, TurnID: turnID}
					return run, emit(TaskEvent{Type: packet.Method, ThreadID: threadID, TurnID: turnID, Status: p.Turn.Status})
				default:
					return TaskRun{}, errors.New("Codex returned an invalid completion status")
				}
			}
		}
	}
	if err := ctx.Err(); err != nil {
		return TaskRun{}, err
	}
	if err := reader.Err(); err != nil {
		return TaskRun{}, fmt.Errorf("read Codex task output: %w", err)
	}
	return TaskRun{}, errors.New("Codex disconnected before the task finished")
}

// serveApproval answers one tool request. The decision is single-use and never
// cached, so the next request asks again.
func serveApproval(ctx context.Context, method string, id json.RawMessage, request approvalRequest, threadID, turnID string, writer *json.Encoder, emit func(TaskEvent) error, decide Decider) error {
	approval, err := describeApproval(method, id, request, threadID, turnID)
	if err != nil {
		return fmt.Errorf("Codex requested interactive input during the task: %w", err)
	}
	if !approval.NeedsDecision() {
		if err := writer.Encode(permissionsReply(id)); err != nil {
			return err
		}
		return emit(TaskEvent{Type: "approval/declined", ThreadID: threadID, TurnID: turnID, Text: approval.Summary(), Status: decisionWord(false)})
	}
	if err := emit(TaskEvent{Type: "approval/requested", ThreadID: threadID, TurnID: turnID, Text: approval.Summary()}); err != nil {
		return err
	}
	approved, err := decide(ctx, approval)
	if err != nil {
		return err
	}
	if err := writer.Encode(approvalDecision(id, approved)); err != nil {
		return err
	}
	event := "approval/declined"
	if approved {
		event = "approval/approved"
	}
	return emit(TaskEvent{Type: event, ThreadID: threadID, TurnID: turnID, Text: approval.Summary(), Status: decisionWord(approved)})
}
