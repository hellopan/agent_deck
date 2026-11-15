package codexprobe

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"slices"
	"strings"
	"testing"
)

func TestTurnFiltersOutput(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"local-thread"}}}
{"method":"turn/started","params":{"threadId":"local-thread","turn":{"id":"active-turn"}}}
{"id":3,"result":{"turn":{"id":"active-turn"}}}
{"method":"item/agentMessage/delta","params":{"threadId":"other-thread","turnId":"active-turn","delta":"unrelated"}}
{"method":"item/agentMessage/delta","params":{"threadId":"local-thread","turnId":"old-turn","delta":"stale"}}
{"method":"turn/completed","params":{"threadId":"local-thread","turn":{"id":"old-turn","status":"failed"}}}
{"method":"item/agentMessage/delta","params":{"threadId":"local-thread","turnId":"active-turn","delta":"hello"}}
{"method":"item/commandExecution/outputDelta","params":{"threadId":"local-thread","turnId":"active-turn","delta":"log"}}
{"method":"turn/completed","params":{"threadId":"local-thread","turn":{"id":"active-turn","status":"completed"}}}
`
	var wire bytes.Buffer
	var events []TaskEvent
	status, err := runTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		events = append(events, event)
		return nil
	}, denyAll)
	want := []TaskEvent{
		{Type: "item/agentMessage/delta", ThreadID: "local-thread", TurnID: "active-turn", Text: "hello"},
		{Type: "item/commandExecution/outputDelta", ThreadID: "local-thread", TurnID: "active-turn", Text: "log"},
		{Type: "turn/completed", ThreadID: "local-thread", TurnID: "active-turn", Status: "completed"},
	}
	if err != nil || status != "completed" || !slices.Equal(events, want) {
		t.Fatalf("status=%q events=%+v err=%v", status, events, err)
	}
	if got := wire.String(); !strings.Contains(got, `"approvalPolicy":"on-request"`) ||
		!strings.Contains(got, `"sandbox":"read-only"`) || !strings.Contains(got, `"threadId":"local-thread"`) {
		t.Fatalf("unexpected request parameters: %s", got)
	}
}

func TestTurnStopsOnForeignApprovalRequest(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"local-thread"}}}
{"id":3,"method":"item/commandExecution/requestApproval","params":{"threadId":"other-thread","turnId":"other-turn"}}
`
	var wire bytes.Buffer
	_, err := runTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		t.Fatalf("unexpected event: %+v", event)
		return nil
	}, denyAll)
	if err == nil || !strings.Contains(err.Error(), "interactive input") {
		t.Fatalf("unexpected error: %v", err)
	}
	if strings.Count(wire.String(), "\n") != 2 {
		t.Fatalf("client answered a foreign approval request: %s", wire.String())
	}
}

func TestTurnAppliesPerRequestDecisions(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"thread"}}}
{"id":3,"result":{"turn":{"id":"turn"}}}
{"id":"file-1","method":"item/fileChange/requestApproval","params":{"threadId":"thread","turnId":"turn","itemId":"file","grantRoot":"C:\\work","reason":"write access"}}
{"id":3,"method":"item/commandExecution/requestApproval","params":{"threadId":"thread","turnId":"turn","itemId":"command","command":"npm test -- --runInBand","cwd":"C:\\work"}}
{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"turn","status":"completed"}}}
`
	var wire bytes.Buffer
	var events []TaskEvent
	var asked []Approval
	decide := func(_ context.Context, approval Approval) (bool, error) {
		asked = append(asked, approval)
		return approval.Kind == commandApprovalKind, nil
	}
	status, err := runTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		events = append(events, event)
		return nil
	}, decide)
	if err != nil || status != "completed" {
		t.Fatalf("status=%q error=%v", status, err)
	}
	wantAsked := []Approval{
		{Kind: fileApprovalKind, ThreadID: "thread", TurnID: "turn", ItemID: "file", GrantRoot: `C:\work`, Reason: "write access"},
		{Kind: commandApprovalKind, ThreadID: "thread", TurnID: "turn", ItemID: "command", Command: "npm test -- --runInBand", Cwd: `C:\work`},
	}
	if !slices.Equal(asked, wantAsked) {
		t.Fatalf("asked=%+v", asked)
	}
	lines := strings.Split(strings.TrimSpace(wire.String()), "\n")
	if len(lines) != 4 || lines[2] != `{"id":"file-1","result":{"decision":"decline"}}` ||
		lines[3] != `{"id":3,"result":{"decision":"accept"}}` {
		t.Fatalf("wrong decisions: %q", lines)
	}
	wantEvents := []TaskEvent{
		{Type: "approval/requested", ThreadID: "thread", TurnID: "turn", Text: `C:\work`},
		{Type: "approval/declined", ThreadID: "thread", TurnID: "turn", Text: `C:\work`, Status: "decline"},
		{Type: "approval/requested", ThreadID: "thread", TurnID: "turn", Text: "npm test -- --runInBand"},
		{Type: "approval/approved", ThreadID: "thread", TurnID: "turn", Text: "npm test -- --runInBand", Status: "accept"},
		{Type: "turn/completed", ThreadID: "thread", TurnID: "turn", Status: "completed"},
	}
	if !slices.Equal(events, wantEvents) {
		t.Fatalf("events=%+v", events)
	}
}

func TestTurnRefusesSessionPermissionsWithoutAsking(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"thread"}}}
{"id":3,"result":{"turn":{"id":"turn"}}}
{"id":"perm-1","method":"item/permissions/requestApproval","params":{"threadId":"thread","turnId":"turn","itemId":"perm","cwd":"C:\\work","permissions":{"network":{"enabled":true}}}}
{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"turn","status":"completed"}}}
`
	var wire bytes.Buffer
	asked := 0
	decide := func(context.Context, Approval) (bool, error) {
		asked++
		return true, nil
	}
	status, err := runTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(TaskEvent) error {
		return nil
	}, decide)
	if err != nil || status != "completed" || asked != 0 {
		t.Fatalf("status=%q asked=%d error=%v", status, asked, err)
	}
	if !strings.Contains(wire.String(), `{"id":"perm-1","result":{"permissions":{}}}`) {
		t.Fatalf("permission request was not refused: %s", wire.String())
	}
}

func denyAll(context.Context, Approval) (bool, error) { return false, nil }
