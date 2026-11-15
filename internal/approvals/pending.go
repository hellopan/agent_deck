// Package approvals tracks one-time decisions for active tool requests.
package approvals

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"slices"
	"strings"
	"sync"
	"time"
)

var ErrUnavailable = errors.New("approval is unavailable")
var ErrDecision = errors.New("decision is not allowed for this approval")

// Scope identifies the operation that raised an approval. It is descriptive:
// the Agent authenticates the caller and re-checks its project authorization
// before passing a decision in.
type Scope struct {
	DeviceID  string `json:"deviceId"`
	PeerID    string `json:"peerId"`
	ProjectID string `json:"projectId"`
	AdapterID string `json:"adapterId"`
	SessionID string `json:"sessionId"`
	RunID     string `json:"runId"`
}

type Decision string

const (
	AcceptOnce Decision = "accept"
	Decline    Decision = "decline"
)

// Request is the read-only view a paired phone receives while deciding.
type Request struct {
	ID        string          `json:"id"`
	Scope     Scope           `json:"scope"`
	Detail    json.RawMessage `json:"detail,omitempty"`
	Expires   time.Time       `json:"expires"`
	AllowOnce bool            `json:"allowOnce"`
}

type pending struct {
	request Request
	result  chan Decision
	timer   *time.Timer
}

// Pending holds approvals only for the lifetime of the Agent process. The
// adapter owns the tool request and treats a closed result with no value as a
// cancellation. The zero value is ready to use.
type Pending struct {
	mu      sync.Mutex
	entries map[string]pending
}

const maxPending = 128

// Open registers one tool request. Detail is opaque to this package and is
// returned verbatim by List so the phone can describe what it is approving.
func (p *Pending) Open(scope Scope, detail json.RawMessage, expires time.Time, allowOnce bool) (string, <-chan Decision, error) {
	if scope.DeviceID == "" || scope.ProjectID == "" || scope.AdapterID == "" ||
		scope.SessionID == "" || scope.RunID == "" {
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
	if p.entries == nil {
		p.entries = make(map[string]pending)
	}
	if _, exists := p.entries[id]; exists {
		return "", nil, errors.New("duplicate approval identifier")
	}
	result := make(chan Decision, 1)
	entry := pending{
		request: Request{ID: id, Scope: scope, Detail: detail, Expires: expires, AllowOnce: allowOnce},
		result:  result,
	}
	entry.timer = time.AfterFunc(time.Until(expires), func() { p.expire(id, result) })
	p.entries[id] = entry
	return id, result, nil
}

// Decide consumes an exact approval ID. The Agent must authenticate the caller
// before calling; a scope that names a peer accepts no other caller.
func (p *Pending) Decide(peerID, id string, decision Decision) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.removeExpired(time.Now())
	entry, ok := p.entries[id]
	if !ok || (entry.request.Scope.PeerID != "" && entry.request.Scope.PeerID != peerID) {
		return ErrUnavailable
	}
	if decision != Decline && (decision != AcceptOnce || !entry.request.AllowOnce) {
		return ErrDecision
	}
	entry.result <- decision
	p.remove(id, entry)
	return nil
}

// Close withdraws an approval the caller no longer waits for.
func (p *Pending) Close(id string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if entry, ok := p.entries[id]; ok {
		p.remove(id, entry)
	}
}

// List returns the unanswered approvals ordered by expiry.
func (p *Pending) List() []Request {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.removeExpired(time.Now())
	requests := make([]Request, 0, len(p.entries))
	for _, entry := range p.entries {
		requests = append(requests, entry.request)
	}
	slices.SortFunc(requests, func(a, b Request) int {
		if order := a.Expires.Compare(b.Expires); order != 0 {
			return order
		}
		return strings.Compare(a.ID, b.ID)
	})
	return requests
}

func (p *Pending) expire(id string, result chan Decision) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if entry, ok := p.entries[id]; ok && entry.result == result {
		p.remove(id, entry)
	}
}

func (p *Pending) removeExpired(now time.Time) {
	for id, entry := range p.entries {
		if !entry.request.Expires.After(now) {
			p.remove(id, entry)
		}
	}
}

func (p *Pending) remove(id string, entry pending) {
	entry.timer.Stop()
	close(entry.result)
	delete(p.entries, id)
}
