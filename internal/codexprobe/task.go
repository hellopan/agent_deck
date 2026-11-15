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
}

// RunReadOnlyTask starts one local turn under a read-only sandbox. Anything the
// sandbox refuses is offered to decide before the turn continues, so the caller
// owns both the authorization and the approval surface. This is not a remote
// execution API.
func RunReadOnlyTask(ctx context.Context, config Config, prompt string, emit func(TaskEvent) error, decide Decider) (string, error) {
	if strings.TrimSpace(prompt) == "" || len(prompt) > 16*1024 || !utf8.ValidString(prompt) || emit == nil || decide == nil {
		return "", errors.New("task requires a UTF-8 prompt of at most 16 KiB, an event handler and an approval decider")
	}
	var status string
	err := withAppServer(ctx, config, func(writer *json.Encoder, reader *bufio.Scanner) error {
		var err error
		status, err = runTurn(ctx, config.ProjectDir, prompt, writer, reader, emit, decide)
		return err
	})
	return status, err
}

func runTurn(ctx context.Context, directory, prompt string, writer *json.Encoder, reader *bufio.Scanner, emit func(TaskEvent) error, decide Decider) (string, error) {
	// "on-request" keeps reads inside the sandbox while letting the model ask
	// for a single escalation. "never" would silently refuse every write and
	// leave the phone nothing to answer.
	start := message{Method: "thread/start", ID: 2, Params: map[string]any{
		"cwd": directory, "sandbox": "read-only", "approvalPolicy": "on-request", "ephemeral": true,
	}}
	if err := writer.Encode(start); err != nil {
		return "", err
	}
	raw, err := readResponse(ctx, reader, start)
	if err != nil {
		return "", err
	}
	var thread struct {
		Thread struct{ ID string } `json:"thread"`
	}
	if err := json.Unmarshal(raw, &thread); err != nil || thread.Thread.ID == "" {
		return "", errors.New("Codex returned an invalid thread")
	}
	threadID := thread.Thread.ID
	if err := writer.Encode(message{Method: "turn/start", ID: 3, Params: map[string]any{
		"threadId": threadID, "input": []map[string]string{{"type": "text", "text": prompt}},
	}}); err != nil {
		return "", err
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
			return "", errors.New("invalid Codex task message")
		}
		if packet.Method != "" && len(packet.ID) > 0 {
			if err := serveApproval(ctx, packet.Method, packet.ID, packet.Params.approvalRequest, threadID, turnID, writer, emit, decide); err != nil {
				return "", err
			}
			continue
		}
		if packet.Method == "" && string(packet.ID) == "3" {
			if packet.Error != nil {
				return "", fmt.Errorf("Codex turn/start returned error code %d", packet.Error.Code)
			}
			var started struct {
				Turn struct{ ID string } `json:"turn"`
			}
			if err := json.Unmarshal(packet.Result, &started); err != nil || started.Turn.ID == "" || (turnID != "" && turnID != started.Turn.ID) {
				return "", errors.New("Codex returned an invalid turn")
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
					return "", err
				}
			}
		case "turn/completed":
			if turnID != "" && p.Turn.ID == turnID {
				switch p.Turn.Status {
				case "completed", "failed", "interrupted":
					return p.Turn.Status, emit(TaskEvent{Type: packet.Method, ThreadID: threadID, TurnID: turnID, Status: p.Turn.Status})
				default:
					return "", errors.New("Codex returned an invalid completion status")
				}
			}
		}
	}
	if err := ctx.Err(); err != nil {
		return "", err
	}
	if err := reader.Err(); err != nil {
		return "", fmt.Errorf("read Codex task output: %w", err)
	}
	return "", errors.New("Codex disconnected before the task finished")
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
