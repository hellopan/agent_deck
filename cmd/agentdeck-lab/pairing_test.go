package main

import (
	"bytes"
	"crypto/x509"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestPairingRequiresMatchingLocalConfirmation(t *testing.T) {
	dir := t.TempDir()
	configs := filepath.Join(dir, "config")
	if err := createLab(configs, "wss://127.0.0.1:8443", labValidity); err != nil {
		t.Fatal(err)
	}
	folder := filepath.Join(dir, "pairing")
	if err := os.Mkdir(folder, 0700); err != nil {
		t.Fatal(err)
	}
	var wire bytes.Buffer
	host := filepath.Join(configs, "agent-a.json")
	var config peerConfig
	if err := loadConfig(host, &config); err != nil {
		t.Fatal(err)
	}
	config.PhoneRouteToken = inviteToken(t)
	data, err := json.Marshal(config)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(host, data, 0600); err != nil {
		t.Fatal(err)
	}
	if err := pairingInvite(host, folder, "a", &wire); err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(wire.Bytes(), []byte("PRIVATE KEY")) {
		t.Fatal("QR includes a private key")
	}
	var invite pairingOffer
	if err := json.Unmarshal(wire.Bytes(), &invite); err != nil {
		t.Fatal(err)
	}
	phone, err := issueIdentity("new-phone", x509.ExtKeyUsageClientAuth, labValidity)
	if err != nil {
		t.Fatal(err)
	}
	keyPair, err := phone.keyPair()
	if err != nil {
		t.Fatal(err)
	}
	cert := keyPair.Leaf
	if cert == nil {
		cert, err = x509.ParseCertificate(keyPair.Certificate[0])
		if err != nil {
			t.Fatal(err)
		}
	}
	legacy := x509.NewCertPool()
	if err := submitPairing(folder, "b", invite.Code, cert); err == nil {
		t.Fatal("offer accepted on wrong device")
	}
	if err := submitPairing(folder, "a", invite.Code, cert); err != nil {
		t.Fatal(err)
	}
	if authorizedPhone(folder, "a", cert, legacy) {
		t.Fatal("candidate trusted before confirmation")
	}
	if err := confirmPairing(folder, "a", invite.Code, invite.Code); err == nil {
		t.Fatal("accepted mismatched fingerprint")
	}
	if err := confirmPairing(folder, "a", invite.Code, pairingFingerprint(cert)); err != nil {
		t.Fatal(err)
	}
	if !authorizedPhone(folder, "a", cert, legacy) || authorizedPhone(folder, "b", cert, legacy) {
		t.Fatal("trust scope incorrect")
	}
	if err := submitPairing(folder, "a", invite.Code, cert); err == nil {
		t.Fatal("offer could be reused")
	}
	// Revocation is the trust file going away. The wrong device must not be
	// able to remove anything, and a second revoke has to report that the
	// phone is gone instead of succeeding quietly.
	if err := revokePairing(folder, "b", pairingFingerprint(cert)); err == nil {
		t.Fatal("revocation applied to the wrong device")
	}
	if err := revokePairing(folder, "a", pairingFingerprint(cert)); err != nil {
		t.Fatal(err)
	}
	if authorizedPhone(folder, "a", cert, legacy) {
		t.Fatal("phone still trusted after revocation")
	}
	if err := revokePairing(folder, "a", pairingFingerprint(cert)); err == nil {
		t.Fatal("revoked an already removed phone")
	}
}

func inviteToken(t *testing.T) string {
	t.Helper()
	token, err := routeToken()
	if err != nil {
		t.Fatal(err)
	}
	return token
}
