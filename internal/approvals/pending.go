// Package approvals tracks one-time decisions for active tool requests.
package approvals

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"sync"
	"time"
)

var ErrUnavailable = errors.New("approval is unavailable")
var ErrDecision = errors.New("decision is not allowed for this approval")

// Scope is assembled by the Agent from its device identity, the authenticated
// peer, and the authorized task. PeerID must not come from a request body.
type Scope struct {
	DeviceID  string
	PeerID    string
	ProjectID string
	AdapterID string
	SessionID string
	RunID     string
}

type Decision string

const (
	AcceptOnce Decision = "accept"
	Decline    Decision = "decline"
)

type pending struct {
	scope     Scope
	expires   time.Time
	allowOnce bool
	result    chan Decision
	timer     *time.Timer
}

// Pending holds approvals only for the lifetime of the Agent process. The
// adapter owns the tool request and treats a closed result with no value as a
// cancellation. The zero value is ready to use.
type Pending struct {
	mu      sync.Mutex
	entries map[string]pending
}

const maxPending = 128

func (p *Pending) Open(scope Scope, expires time.Time, allowOnce bool) (string, <-chan Decision, error) {
	if scope.DeviceID == "" || scope.PeerID == "" || scope.ProjectID == "" ||
		scope.AdapterID == "" || scope.SessionID == "" || scope.RunID == "" {
		return "", nil, errors.New("approval scope is incomplete")
	}
	var randomID [16]byte
	if _, err := rand.Read(randomID[:]); err != nil {
		return "", nil, err
	}
	id := hex.EncodeToString(randomID[:])
	p.mu.Lock()
	defer p.mu.Unlock()
	now := time.Now()
	if !expires.After(now) {
		return "", nil, ErrUnavailable
	}
	p.removeExpired(now)
	if len(p.entries) >= maxPending {
		return "", nil, errors.New("too many pending approvals")
	}
	if _, exists := p.entries[id]; exists {
		return "", nil, errors.New("duplicate approval identifier")
	}
	if p.entries == nil {
		p.entries = make(map[string]pending)
	}
	result := make(chan Decision, 1)
	entry := pending{scope: scope, expires: expires, allowOnce: allowOnce, result: result}
	entry.timer = time.AfterFunc(time.Until(expires), func() {
		p.mu.Lock()
		defer p.mu.Unlock()
		if current, ok := p.entries[id]; ok && current.result == result {
			p.remove(id, current)
		}
	})
	p.entries[id] = entry
	return id, result, nil
}

// Decide consumes an exact match. The Agent must validate the caller's current
// project authorization before passing a decision to this table.
func (p *Pending) Decide(scope Scope, id string, decision Decision) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.removeExpired(time.Now())
	entry, ok := p.entries[id]
	if !ok || entry.scope != scope {
		return ErrUnavailable
	}
	if decision != Decline && (decision != AcceptOnce || !entry.allowOnce) {
		return ErrDecision
	}
	entry.result <- decision
	p.remove(id, entry)
	return nil
}

// CloseRun invalidates unanswered approvals after a tool turn ends.
func (p *Pending) CloseRun(scope Scope) {
	p.mu.Lock()
	defer p.mu.Unlock()
	for id, entry := range p.entries {
		if entry.scope == scope {
			p.remove(id, entry)
		}
	}
}

func (p *Pending) removeExpired(now time.Time) {
	for id, entry := range p.entries {
		if !entry.expires.After(now) {
			p.remove(id, entry)
		}
	}
}

func (p *Pending) remove(id string, entry pending) {
	entry.timer.Stop()
	close(entry.result)
	delete(p.entries, id)
}
