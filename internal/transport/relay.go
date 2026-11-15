package transport

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/json"
	"io"
	"net/http"
	"regexp"
	"strings"
	"sync"

	"github.com/coder/websocket"
)

const maxFrameSize = 64 * 1024

// Route authorizes one device and one phone for a prototype tunnel.
// The two credentials grant routing only and are unrelated to the inner TLS keys.
type Route struct {
	AgentToken string
	PhoneToken string
}

type routeCredentials struct {
	agent [sha256.Size]byte
	phone [sha256.Size]byte
}

type tunnel struct {
	ctx       context.Context
	cancel    context.CancelFunc
	agent     *websocket.Conn
	frames    chan []byte
	delivered chan struct{}
	claimed   bool
}

type relay struct {
	ctx     context.Context
	routes  map[string]routeCredentials
	mu      sync.Mutex
	tunnels map[string]*tunnel
}

// NewRelay creates an in-memory, one-tunnel-per-device prototype router.
func NewRelay(ctx context.Context, routes map[string]Route) http.Handler {
	return NewEnrollmentRelay(ctx, routes, "", nil)
}

var enrollmentDevice = regexp.MustCompile(`^[a-f0-9]{32}$`)
var enrollmentToken = regexp.MustCompile(`^[a-f0-9]{64}$`)

// NewEnrollmentRelay enables enrollment when the owner supplies a credential
// and an exclusive, durable route publisher. The publisher runs under the lock.
func NewEnrollmentRelay(ctx context.Context, routes map[string]Route, enrollment string, publish func(string, Route) error) http.Handler {
	server := &relay{
		ctx: ctx, routes: make(map[string]routeCredentials),
		tunnels: make(map[string]*tunnel),
	}
	for deviceID, route := range routes {
		if route.AgentToken == "" || route.PhoneToken == "" || route.AgentToken == route.PhoneToken {
			continue
		}
		server.routes[deviceID] = routeCredentials{
			agent: sha256.Sum256([]byte(route.AgentToken)),
			phone: sha256.Sum256([]byte(route.PhoneToken)),
		}
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/agent/{device}", server.attachAgent)
	mux.HandleFunc("GET /v1/phone/{device}", server.attachPhone)
	if enrollmentToken.MatchString(enrollment) && publish != nil {
		digest := sha256.Sum256([]byte(enrollment))
		mux.HandleFunc("POST /v1/enroll/{device}", func(w http.ResponseWriter, req *http.Request) {
			token, ok := strings.CutPrefix(req.Header.Get("Authorization"), "Bearer ")
			check := sha256.Sum256([]byte(token))
			if !ok || subtle.ConstantTimeCompare(digest[:], check[:]) != 1 {
				http.Error(w, "enrollment denied", http.StatusUnauthorized)
				return
			}
			id := req.PathValue("device")
			var route Route
			body := json.NewDecoder(http.MaxBytesReader(w, req.Body, 1024))
			if !enrollmentDevice.MatchString(id) || body.Decode(&route) != nil || body.Decode(new(any)) != io.EOF ||
				!enrollmentToken.MatchString(route.AgentToken) || !enrollmentToken.MatchString(route.PhoneToken) ||
				route.AgentToken == route.PhoneToken {
				http.Error(w, "invalid enrollment", http.StatusBadRequest)
				return
			}
			record := routeCredentials{agent: sha256.Sum256([]byte(route.AgentToken)), phone: sha256.Sum256([]byte(route.PhoneToken))}
			server.mu.Lock()
			defer server.mu.Unlock()
			if existing, ok := server.routes[id]; ok {
				if existing != record {
					http.Error(w, "device already registered", http.StatusConflict)
					return
				}
			} else {
				if len(server.routes) >= 256 {
					http.Error(w, "route limit reached", http.StatusServiceUnavailable)
					return
				}
				if err := publish(id, route); err != nil {
					http.Error(w, "enrollment storage unavailable", http.StatusServiceUnavailable)
					return
				}
				server.routes[id] = record
			}
			w.WriteHeader(http.StatusNoContent)
		})
	}
	return mux
}

func (r *relay) authorize(w http.ResponseWriter, req *http.Request, forAgent bool) (string, bool) {
	deviceID := req.PathValue("device")
	r.mu.Lock()
	credentials, exists := r.routes[deviceID]
	r.mu.Unlock()
	token, hasBearer := strings.CutPrefix(req.Header.Get("Authorization"), "Bearer ")
	digest := sha256.Sum256([]byte(token))
	expected := credentials.phone
	if forAgent {
		expected = credentials.agent
	}
	if !exists || !hasBearer || subtle.ConstantTimeCompare(digest[:], expected[:]) != 1 {
		http.Error(w, "routing credentials rejected", http.StatusUnauthorized)
		return "", false
	}
	if r.ctx.Err() != nil {
		http.Error(w, "relay is stopping", http.StatusServiceUnavailable)
		return "", false
	}
	return deviceID, true
}

func (r *relay) attachAgent(w http.ResponseWriter, req *http.Request) {
	deviceID, ok := r.authorize(w, req, true)
	if !ok {
		return
	}
	agent, err := websocket.Accept(w, req, nil)
	if err != nil {
		return
	}
	defer agent.CloseNow()
	agent.SetReadLimit(maxFrameSize)
	ctx, cancel := context.WithCancel(r.ctx)
	defer cancel()
	link := &tunnel{
		ctx: ctx, cancel: cancel, agent: agent,
		frames: make(chan []byte), delivered: make(chan struct{}),
	}

	r.mu.Lock()
	previous := r.tunnels[deviceID]
	r.tunnels[deviceID] = link
	r.mu.Unlock()
	if previous != nil {
		previous.cancel()
	}
	defer func() {
		r.mu.Lock()
		if r.tunnels[deviceID] == link {
			delete(r.tunnels, deviceID)
		}
		r.mu.Unlock()
	}()

	for {
		messageType, frame, err := agent.Read(ctx)
		if err != nil || messageType != websocket.MessageBinary {
			return
		}
		select {
		case link.frames <- frame:
		case <-ctx.Done():
			return
		}
		// Forward the last frame before reacting to the Agent's close frame.
		select {
		case <-link.delivered:
		case <-ctx.Done():
			return
		}
	}
}

func (r *relay) attachPhone(w http.ResponseWriter, req *http.Request) {
	deviceID, ok := r.authorize(w, req, false)
	if !ok {
		return
	}
	r.mu.Lock()
	link := r.tunnels[deviceID]
	if link == nil || link.ctx.Err() != nil || link.claimed {
		r.mu.Unlock()
		http.Error(w, "device tunnel unavailable", http.StatusServiceUnavailable)
		return
	}
	link.claimed = true
	r.mu.Unlock()
	defer link.cancel()

	phone, err := websocket.Accept(w, req, nil)
	if err != nil {
		return
	}
	defer phone.CloseNow()
	phone.SetReadLimit(maxFrameSize)
	forwarded := make(chan struct{})
	go func() {
		defer close(forwarded)
		defer link.cancel()
		for {
			select {
			case frame := <-link.frames:
				if err := phone.Write(link.ctx, websocket.MessageBinary, frame); err != nil {
					return
				}
				select {
				case link.delivered <- struct{}{}:
				case <-link.ctx.Done():
					return
				}
			case <-link.ctx.Done():
				return
			}
		}
	}()
	defer func() {
		link.cancel()
		<-forwarded
	}()

	for {
		messageType, frame, err := phone.Read(link.ctx)
		if err != nil || messageType != websocket.MessageBinary {
			return
		}
		if err := link.agent.Write(link.ctx, websocket.MessageBinary, frame); err != nil {
			return
		}
	}
}
