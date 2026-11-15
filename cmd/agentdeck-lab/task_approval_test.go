package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"strings"
	"testing"
	"time"

	"github.com/hellopan/agent_deck/internal/approvals"
	"github.com/hellopan/agent_deck/internal/codexprobe"
)

func approvalRuntime(t *testing.T) (*agentRuntime, *queuedTask, codexprobe.Approval) {
	t.Helper()
	opts, _ := queueFixture(t)
	runtime := &agentRuntime{peer: opts, device: "a", logger: log.New(io.Discard, "", 0)}
	task := &queuedTask{TaskID: strings.Repeat("e", 32), Record: inboxRecord{
		taskSubmission: taskSubmission{RequestID: strings.Repeat("e", 32), ProjectID: "alpha", Prompt: "test"},
		PeerID:         "phone",
	}}
	return runtime, task, codexprobe.Approval{
		Kind: "command", ThreadID: "thread", TurnID: "turn", ItemID: "item", Command: "npm test",
	}
}

func TestAwaitDecisionOnlyAcceptsTheSubmittingPhone(t *testing.T) {
	opts, _ := queueFixture(t)
	runtime := &agentRuntime{peer: opts, device: "a", logger: log.New(io.Discard, "", 0)}
	task := &queuedTask{TaskID: strings.Repeat("e", 32), Record: inboxRecord{
		taskSubmission: taskSubmission{RequestID: strings.Repeat("e", 32), ProjectID: "alpha", Prompt: "test"},
		PeerID:         "phone",
	}}
	approval := codexprobe.Approval{
		Kind: "command", ThreadID: "thread", TurnID: "turn", ItemID: "item",
		Command: "npm test -- --runInBand", Cwd: `C:\work`,
	}
	type outcome struct {
		approved bool
		err      error
	}
	done := make(chan outcome, 1)
	go func() {
		approved, err := runtime.awaitDecision(t.Context(), task, approval)
		done <- outcome{approved, err}
	}()
	var id string
	for id == "" {
		if list := runtime.pending.List(); len(list) == 1 {
			id = list[0].ID
		} else {
			select {
			case <-t.Context().Done():
				t.Fatal("approval never became pending")
			case <-time.After(5 * time.Millisecond):
			}
		}
	}
	snapshot := runtime.approvals()
	if len(snapshot.Approvals) != 1 {
		t.Fatalf("snapshot: %+v", snapshot)
	}
	item := snapshot.Approvals[0]
	if item.Command != approval.Command || item.TaskID != task.TaskID || item.ProjectID != "alpha" ||
		item.AdapterID != codexAdapterID || item.Kind != "command" || item.SecondsLeft <= 0 {
		t.Fatalf("approval view: %+v", item)
	}
	if snapshot.Running != nil {
		t.Fatalf("unexpected running task: %+v", snapshot.Running)
	}
	if err := runtime.pending.Decide("other-phone", id, approvals.AcceptOnce); !errors.Is(err, approvals.ErrUnavailable) {
		t.Fatalf("another phone decided: %v", err)
	}
	var wire bytes.Buffer
	runtime.decide(&wire, "phone", fmt.Sprintf(`{"approvalId":%q,"decision":"accept"}`, id))
	var reply decisionReply
	if err := json.NewDecoder(&wire).Decode(&reply); err != nil || !reply.OK {
		t.Fatalf("decision rejected: %+v %v", reply, err)
	}
	select {
	case got := <-done:
		if got.err != nil || !got.approved {
			t.Fatalf("outcome: %+v", got)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("decision did not reach the runner")
	}
	if list := runtime.pending.List(); len(list) != 0 {
		t.Fatalf("decided approval stayed pending: %+v", list)
	}
}

func TestAwaitDecisionRejectsUnknownDecisions(t *testing.T) {
	runtime, task, approval := approvalRuntime(t)
	done := make(chan error, 1)
	go func() {
		_, err := runtime.awaitDecision(t.Context(), task, approval)
		done <- err
	}()
	for len(runtime.pending.List()) == 0 {
		select {
		case <-t.Context().Done():
			t.Fatal("approval never became pending")
		case <-time.After(5 * time.Millisecond):
		}
	}
	id := runtime.pending.List()[0].ID
	var wire bytes.Buffer
	runtime.decide(&wire, "phone", fmt.Sprintf(`{"approvalId":%q,"decision":"acceptForSession"}`, id))
	var reply decisionReply
	if err := json.NewDecoder(&wire).Decode(&reply); err != nil || reply.OK || reply.Error == "" {
		t.Fatalf("session-wide decision accepted: %+v %v", reply, err)
	}
	runtime.pending.Close(id)
	if err := <-done; err != nil {
		t.Fatalf("closing an approval failed the task: %v", err)
	}
}

func TestAwaitDecisionStopsWithTheTask(t *testing.T) {
	runtime, task, approval := approvalRuntime(t)
	ctx, cancel := context.WithCancel(t.Context())
	go func() {
		time.Sleep(20 * time.Millisecond)
		cancel()
	}()
	if _, err := runtime.awaitDecision(ctx, task, approval); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation returned %v", err)
	}
	if list := runtime.pending.List(); len(list) != 0 {
		t.Fatalf("cancelled approval stayed pending: %+v", list)
	}
}
