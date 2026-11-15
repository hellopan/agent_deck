package codexprobe

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
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

// RunReadOnlyTask starts one local diagnostic turn. The caller must resolve the
// project on the host before invoking it. This is not a remote execution API.
func RunReadOnlyTask(ctx context.Context, config Config, prompt string, emit func(TaskEvent) error) (string, error) {
	if strings.TrimSpace(prompt) == "" || len(prompt) > 16*1024 || !utf8.ValidString(prompt) || emit == nil {
		return "", errors.New("task requires a UTF-8 prompt of at most 16 KiB and an event handler")
	}
	ctx, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()
	var status string
	err := withAppServer(ctx, config, func(writer *json.Encoder, reader *bufio.Scanner) error {
		var err error
		status, err = runReadOnlyTurn(ctx, config.ProjectDir, prompt, writer, reader, emit)
		return err
	})
	return status, err
}

func runReadOnlyTurn(ctx context.Context, directory, prompt string, writer *json.Encoder, reader *bufio.Scanner, emit func(TaskEvent) error) (string, error) {
	start := message{Method: "thread/start", ID: 2, Params: map[string]any{
		"cwd": directory, "sandbox": "read-only", "approvalPolicy": "never", "ephemeral": true,
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
			reply, err := declineApproval(packet.Method, packet.ID, packet.Params.approvalRequest, threadID, turnID)
			if err != nil {
				return "", fmt.Errorf("Codex requested interactive input during the read-only task: %w", err)
			}
			if err := writer.Encode(reply); err != nil {
				return "", err
			}
			if err := emit(TaskEvent{Type: "approval/declined", ThreadID: threadID, TurnID: turnID, Status: reply.Result.Decision}); err != nil {
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
