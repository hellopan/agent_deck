package approvals

import (
	"encoding/json"
	"errors"
	"sync"
	"testing"
	"time"
)

func scopeFor(peer string) Scope {
	return Scope{DeviceID: "a", PeerID: peer, ProjectID: "alpha", AdapterID: "codex", SessionID: "thread", RunID: "turn"}
}

func TestDecisionIsBoundToTheSubmittingPeerAndConsumedOnce(t *testing.T) {
	var requests Pending
	scope := scopeFor("phone")
	if _, _, err := requests.Open(Scope{DeviceID: "a"}, nil, time.Now().Add(time.Minute), true); err == nil {
		t.Fatal("accepted an incomplete scope")
	}
	id, result, err := requests.Open(scope, json.RawMessage(`{"taskId":"t"}`), time.Now().Add(time.Minute), true)
	if err != nil {
		t.Fatal(err)
	}
	listed := requests.List()
	if len(listed) != 1 || listed[0].ID != id || string(listed[0].Detail) != `{"taskId":"t"}` || !listed[0].AllowOnce {
		t.Fatalf("listed=%+v", listed)
	}
	if err := requests.Decide("other-phone", id, AcceptOnce); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("another phone decided: %v", err)
	}
	if err := requests.Decide("phone", id, Decision("acceptForSession")); !errors.Is(err, ErrDecision) {
		t.Fatalf("session-wide approval returned %v", err)
	}
	outcomes := make(chan error, 2)
	var contenders sync.WaitGroup
	for range 2 {
		contenders.Go(func() { outcomes <- requests.Decide("phone", id, AcceptOnce) })
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
	if listed := requests.List(); len(listed) != 0 {
		t.Fatalf("decided approval stayed listed: %+v", listed)
	}
}

func TestUnscopedApprovalAcceptsAnyAuthenticatedCaller(t *testing.T) {
	var requests Pending
	id, result, err := requests.Open(scopeFor(""), nil, time.Now().Add(time.Minute), true)
	if err != nil {
		t.Fatal(err)
	}
	if err := requests.Decide("any-paired-phone", id, Decline); err != nil {
		t.Fatal(err)
	}
	if decision, ok := <-result; !ok || decision != Decline {
		t.Fatalf("received %q, open=%v", decision, ok)
	}
}

func TestExpiryAndWithdrawalWakeTheWaiter(t *testing.T) {
	var requests Pending
	scope := scopeFor("phone")
	id, result, err := requests.Open(scope, nil, time.Now().Add(50*time.Millisecond), true)
	if err != nil {
		t.Fatal(err)
	}
	select {
	case _, ok := <-result:
		if ok {
			t.Fatal("expired approval received a decision")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("expiry did not wake the waiter")
	}
	if err := requests.Decide("phone", id, AcceptOnce); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("expired approval returned %v", err)
	}

	id, result, err = requests.Open(scope, nil, time.Now().Add(time.Minute), true)
	if err != nil {
		t.Fatal(err)
	}
	requests.Close(id)
	if _, ok := <-result; ok {
		t.Fatal("withdrawn approval received a decision")
	}
	requests.Close(id)
}
