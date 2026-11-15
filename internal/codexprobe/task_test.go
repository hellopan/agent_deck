package codexprobe

import (
	"bufio"
	"bytes"
	"encoding/json"
	"slices"
	"strings"
	"testing"
)

func TestReadOnlyTurnFiltersOutput(t *testing.T) {
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
	status, err := runReadOnlyTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		events = append(events, event)
		return nil
	})
	want := []TaskEvent{
		{Type: "item/agentMessage/delta", ThreadID: "local-thread", TurnID: "active-turn", Text: "hello"},
		{Type: "item/commandExecution/outputDelta", ThreadID: "local-thread", TurnID: "active-turn", Text: "log"},
		{Type: "turn/completed", ThreadID: "local-thread", TurnID: "active-turn", Status: "completed"},
	}
	if err != nil || status != "completed" || !slices.Equal(events, want) {
		t.Fatalf("status=%q events=%+v err=%v", status, events, err)
	}
	if got := wire.String(); !strings.Contains(got, `"approvalPolicy":"never"`) ||
		!strings.Contains(got, `"sandbox":"read-only"`) || !strings.Contains(got, `"threadId":"local-thread"`) {
		t.Fatalf("unexpected request parameters: %s", got)
	}
}

func TestReadOnlyTurnStopsOnApprovalRequest(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"local-thread"}}}
{"id":3,"method":"item/commandExecution/requestApproval","params":{"threadId":"other-thread","turnId":"other-turn"}}
`
	var wire bytes.Buffer
	_, err := runReadOnlyTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		t.Fatalf("unexpected event: %+v", event)
		return nil
	})
	if err == nil || !strings.Contains(err.Error(), "interactive input") {
		t.Fatalf("unexpected error: %v", err)
	}
	if strings.Count(wire.String(), "\n") != 2 {
		t.Fatalf("client answered an approval request: %s", wire.String())
	}
}

func TestReadOnlyTurnDeclinesScopedRequests(t *testing.T) {
	const input = `{"id":2,"result":{"thread":{"id":"thread"}}}
{"id":3,"result":{"turn":{"id":"turn"}}}
{"id":"file-1","method":"item/fileChange/requestApproval","params":{"threadId":"thread","turnId":"turn","itemId":"file"}}
{"id":3,"method":"item/commandExecution/requestApproval","params":{"threadId":"thread","turnId":"turn","itemId":"command","availableDecisions":["accept","cancel"]}}
{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"turn","status":"completed"}}}
`
	var wire bytes.Buffer
	var events []TaskEvent
	status, err := runReadOnlyTurn(t.Context(), "workspace", "test", json.NewEncoder(&wire), bufio.NewScanner(strings.NewReader(input)), func(event TaskEvent) error {
		events = append(events, event)
		return nil
	})
	if err != nil || status != "completed" || len(events) != 3 {
		t.Fatalf("status=%q events=%+v error=%v", status, events, err)
	}
	got := strings.Split(strings.TrimSpace(wire.String()), "\n")
	if len(got) != 4 || got[2] != `{"id":"file-1","result":{"decision":"decline"}}` ||
		got[3] != `{"id":3,"result":{"decision":"cancel"}}` {
		t.Fatalf("wrong request IDs or decisions: %q", got)
	}
	if events[0].Type != "approval/declined" || events[0].Status != "decline" ||
		events[1].Type != "approval/declined" || events[1].Status != "cancel" {
		t.Fatalf("wrong approval events: %+v", events)
	}
}
