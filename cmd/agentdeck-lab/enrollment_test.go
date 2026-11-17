package main

import (
	"crypto/tls"
	"crypto/x509"
	"encoding/pem"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/hellopan/agent_deck/internal/transport"
)

func TestRelayInitHonoursValidity(t *testing.T) {
	path := filepath.Join(t.TempDir(), "relay.local.json")
	var pin strings.Builder
	if err := initEnrollmentRelay(path, "wss://127.0.0.1:8443", 365*24*time.Hour, &pin); err != nil {
		t.Fatal(err)
	}
	if strings.Count(strings.TrimSpace(pin.String()), "\n") != 0 {
		t.Fatalf("stdout must carry only the fingerprint, got %q", pin.String())
	}
	var relay relayConfig
	if err := loadConfig(path, &relay); err != nil {
		t.Fatal(err)
	}
	block, _ := pem.Decode([]byte(relay.Identity.Certificate))
	if block == nil {
		t.Fatal("configuration carries no certificate")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		t.Fatal(err)
	}
	if left := time.Until(cert.NotAfter); left < 364*24*time.Hour || left > 366*24*time.Hour {
		t.Fatalf("certificate lasts %v, want about 365 days", left)
	}
}

func TestValidityFromDays(t *testing.T) {
	if _, err := validityFromDays(0); err == nil {
		t.Fatal("accepted a zero-day certificate")
	}
	if _, err := validityFromDays(3651); err == nil {
		t.Fatal("accepted an unbounded lifetime")
	}
	if got, err := validityFromDays(365); err != nil || got != 365*24*time.Hour {
		t.Fatalf("365 days became %v (%v)", got, err)
	}
}

func TestEnrollWithoutLegacyFiles(t *testing.T) {
	server := httptest.NewUnstartedServer(nil)
	defer server.Close()
	endpoint := "wss://" + server.Listener.Addr().String()
	path := filepath.Join(t.TempDir(), "relay.local.json")
	var pin strings.Builder
	if err := initEnrollmentRelay(path, endpoint, labValidity, &pin); err != nil {
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
