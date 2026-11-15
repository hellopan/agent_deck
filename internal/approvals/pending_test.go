package approvals

import (
	"errors"
	"sync"
	"testing"
	"time"
)

func TestDecisionRequiresScopeAndCanBeConsumedOnlyOnce(t *testing.T) {
	var requests Pending
	scope := Scope{DeviceID: "a", PeerID: "phone", ProjectID: "alpha", AdapterID: "codex", SessionID: "thread", RunID: "turn"}
	id, result, err := requests.Open(scope, time.Now().Add(time.Minute), true)
	if err != nil {
		t.Fatal(err)
	}
	defer requests.CloseRun(scope)
	for _, mismatch := range []Scope{
		{"b", "phone", "alpha", "codex", "thread", "turn"},
		{"a", "other", "alpha", "codex", "thread", "turn"},
		{"a", "phone", "beta", "codex", "thread", "turn"},
		{"a", "phone", "alpha", "cursor", "thread", "turn"},
		{"a", "phone", "alpha", "codex", "different", "turn"},
		{"a", "phone", "alpha", "codex", "thread", "old"},
	} {
		if err := requests.Decide(mismatch, id, AcceptOnce); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("scope mismatch returned %v", err)
		}
	}
	if err := requests.Decide(scope, id, Decision("acceptForSession")); !errors.Is(err, ErrDecision) {
		t.Fatalf("session-wide approval returned %v", err)
	}
	outcomes := make(chan error, 2)
	var contenders sync.WaitGroup
	for range 2 {
		contenders.Go(func() { outcomes <- requests.Decide(scope, id, AcceptOnce) })
	}
	contenders.Wait()
	close(outcomes)
	accepted := 0
	for err := range outcomes {
		if err == nil {
			accepted++
		} else if !errors.Is(err, ErrUnavailable) {
			t.Fatal(err)
		}
	}
	if accepted != 1 {
		t.Fatalf("%d decisions accepted, want one", accepted)
	}
	if decision, ok := <-result; !ok || decision != AcceptOnce {
		t.Fatalf("received %q, open=%v", decision, ok)
	}
	if _, ok := <-result; ok {
		t.Fatal("approval delivered twice")
	}
}

func TestExpiryAndClosingRun(t *testing.T) {
	var requests Pending
	scope := Scope{DeviceID: "a", PeerID: "phone", ProjectID: "alpha", AdapterID: "codex", SessionID: "thread", RunID: "turn"}
	expires := time.Now().Add(time.Minute)
	id, result, err := requests.Open(scope, expires, false)
	if err != nil {
		t.Fatal(err)
	}
	if err := requests.Decide(scope, id, AcceptOnce); !errors.Is(err, ErrDecision) {
		t.Fatalf("accepted an unsupported decision: %v", err)
	}
	requests.CloseRun(scope)
	if _, ok := <-result; ok {
		t.Fatal("ended turn received a decision")
	}
	if err := requests.Decide(scope, id, Decline); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("ended turn still accepted a decision: %v", err)
	}
	id, result, err = requests.Open(scope, time.Now().Add(100*time.Millisecond), true)
	if err != nil {
		t.Fatal(err)
	}
	defer requests.CloseRun(scope)
	select {
	case _, ok := <-result:
		if ok {
			t.Fatal("expired approval received a decision")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("expiry did not wake the waiter")
	}
	if err := requests.Decide(scope, id, AcceptOnce); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("expired approval returned %v", err)
	}
}
