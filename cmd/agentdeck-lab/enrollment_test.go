package main

import (
	"crypto/tls"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

	"github.com/hellopan/agent_deck/internal/transport"
)

func TestEnrollWithoutLegacyFiles(t *testing.T) {
	server := httptest.NewUnstartedServer(nil)
	defer server.Close()
	endpoint := "wss://" + server.Listener.Addr().String()
	path := filepath.Join(t.TempDir(), "relay.local.json")
	var pin strings.Builder
	if err := initEnrollmentRelay(path, endpoint, &pin); err != nil {
		t.Fatal(err)
	}
	var relay relayConfig
	if err := loadConfig(path, &relay); err != nil {
		t.Fatal(err)
	}
	cert, err := relay.Identity.keyPair()
	if err != nil {
		t.Fatal(err)
	}
	routes := t.TempDir()
	publish, err := enrollmentStorage(routes, relay.Routes)
	if err != nil {
		t.Fatal(err)
	}
	token := inviteToken(t)
	server.Config.Handler = transport.NewEnrollmentRelay(t.Context(), relay.Routes, token, publish)
	server.TLS = &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS13}
	server.StartTLS()

	agent := filepath.Join(t.TempDir(), "agent.local.json")
	fingerprint := strings.TrimSpace(pin.String())
	if _, err := enrollAgent(t.Context(), agent, endpoint, token, strings.Repeat("0", 64)); err == nil {
		t.Fatal("accepted incorrect Relay fingerprint")
	}
	if _, err := enrollAgent(t.Context(), agent, endpoint, strings.Repeat("0", 64), fingerprint); err == nil {
		t.Fatal("accepted incorrect enrollment token")
	}
	first, err := enrollAgent(t.Context(), agent, endpoint, token, fingerprint)
	if err != nil {
		t.Fatal(err)
	}
	second, err := enrollAgent(t.Context(), agent, endpoint, token, fingerprint)
	if err != nil || first != second {
		t.Fatalf("retry changed identity: %q %q %v", first, second, err)
	}
	reloaded := make(map[string]transport.Route)
	if _, err := enrollmentStorage(routes, reloaded); err != nil || len(reloaded) != 1 {
		t.Fatalf("routes did not survive reload: count=%d %v", len(reloaded), err)
	}
	var qr strings.Builder
	if err := pairingInvite(agent, t.TempDir(), first, &qr); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(qr.String(), first) || strings.Contains(qr.String(), "PRIVATE KEY") {
		t.Fatal("pairing QR missing device or exposing private key")
	}
}
