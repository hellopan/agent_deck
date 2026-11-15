package codexprobe

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
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
	result, err := runTurn(t.Context(), "workspace", "", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		events = append(events, event)
		return nil
	}, denyAll)
	want := []TaskEvent{
		{Type: "item/agentMessage/delta", ThreadID: "local-thread", TurnID: "active-turn", Text: "hello"},
		{Type: "item/commandExecution/outputDelta", ThreadID: "local-thread", TurnID: "active-turn", Text: "log"},
		{Type: "turn/completed", ThreadID: "local-thread", TurnID: "active-turn", Status: "completed"},
	}
	if err != nil || result.Status != "completed" || !slices.Equal(events, want) {
		t.Fatalf("status=%q events=%+v err=%v", result.Status, events, err)
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
	_, err := runTurn(t.Context(), "workspace", "", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
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
	result, err := runTurn(t.Context(), "workspace", "", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		events = append(events, event)
		return nil
	}, decide)
	if err != nil || result.Status != "completed" {
		t.Fatalf("status=%q error=%v", result.Status, err)
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
	result, err := runTurn(t.Context(), "workspace", "", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(TaskEvent) error {
		return nil
	}, decide)
	if err != nil || result.Status != "completed" || asked != 0 {
		t.Fatalf("status=%q asked=%d error=%v", result.Status, asked, err)
	}
	if !strings.Contains(wire.String(), `{"id":"perm-1","result":{"permissions":{}}}`) {
		t.Fatalf("permission request was not refused: %s", wire.String())
	}
}

func denyAll(context.Context, Approval) (bool, error) { return false, nil }

func TestTurnStartsStoredThread(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"01a1e02e-b9a6-74c3-9462-b22f77b5cee0"}}}
{"id":3,"result":{"turn":{"id":"turn"}}}
{"method":"turn/completed","params":{"threadId":"01a1e02e-b9a6-74c3-9462-b22f77b5cee0","turn":{"id":"turn","status":"completed"}}}
`
	var wire bytes.Buffer
	result, err := runTurn(t.Context(), "workspace", "", "第一句", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(TaskEvent) error {
		return nil
	}, denyAll)
	if err != nil || result.Status != "completed" || result.ThreadID == "" {
		t.Fatalf("result=%+v err=%v", result, err)
	}
	got := wire.String()
	if !strings.Contains(got, `"method":"thread/start"`) || !strings.Contains(got, `"ephemeral":false`) {
		t.Fatalf("a first turn must store its thread: %s", got)
	}
}

func TestTurnResumesKnownThread(t *testing.T) {
	const thread = "01a1e02e-b9a6-74c3-9462-b22f77b5cee0"
	const input = `{"id":2,"result":{"thread":{"id":"01a1e02e-b9a6-74c3-9462-b22f77b5cee0"}}}
{"id":3,"result":{"turn":{"id":"turn"}}}
{"method":"turn/completed","params":{"threadId":"01a1e02e-b9a6-74c3-9462-b22f77b5cee0","turn":{"id":"turn","status":"completed"}}}
`
	var wire bytes.Buffer
	result, err := runTurn(t.Context(), "workspace", thread, "接着改", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(TaskEvent) error {
		return nil
	}, denyAll)
	if err != nil || result.Status != "completed" || result.ThreadID != thread {
		t.Fatalf("result=%+v err=%v", result, err)
	}
	got := wire.String()
	if strings.Contains(got, `"method":"thread/start"`) || !strings.Contains(got, `"method":"thread/resume"`) ||
		!strings.Contains(got, `"threadId":"`+thread+`"`) || !strings.Contains(got, `"sandbox":"read-only"`) ||
		!strings.Contains(got, `"approvalPolicy":"on-request"`) {
		t.Fatalf("a resumed turn must keep the sandbox and ask again: %s", got)
	}
}

func TestTurnRefusesUnresumableThread(t *testing.T) {
	const input = `{"id":2,"error":{"code":-32600,"message":"no rollout found for thread id 01a1e02e-b9a6-74c3-9462-b22f77b5cee0"}}
`
	var wire bytes.Buffer
	_, err := runTurn(t.Context(), "workspace", "01a1e02e-b9a6-74c3-9462-b22f77b5cee0", "接着改", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(TaskEvent) error {
		t.Fatalf("unexpected event")
		return nil
	}, denyAll)
	if !errors.Is(err, ErrThreadUnavailable) || !strings.Contains(err.Error(), "no rollout found") {
		t.Fatalf("unexpected error: %v", err)
	}
	if strings.Contains(wire.String(), `"method":"thread/start"`) {
		t.Fatal("silently started a new thread instead of reporting the lost conversation")
	}
}

func TestValidThreadID(t *testing.T) {
	cases := map[string]bool{
		"01a1e02e-b9a6-74c3-9462-b22f77b5cee0": true,
		"01A1E02E-B9A6-74C3-9462-B22F77B5CEE0": true,
		"":                                     false,
		"local-thread":                         false,
		"01a1e02eb9a674c39462b22f77b5cee0":     false,
		"01a1e02e-b9a6-74c3-9462-b22f77b5cee":  false,
		"01a1e02e-b9a6-74c3-9462-b22f77b5cegg": false,
	}
	for id, want := range cases {
		if got := ValidThreadID(id); got != want {
			t.Fatalf("ValidThreadID(%q)=%v want %v", id, got, want)
		}
	}
}
