package main

import (
	"bytes"
	"context"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/hellopan/agent_deck/internal/transport"
)

func initEnrollmentRelay(path, endpoint string, output io.Writer) error {
	address, err := relayAddress(endpoint)
	if err != nil {
		return err
	}
	cert, err := issueIdentity(address.Hostname(), x509.ExtKeyUsageServerAuth)
	if err != nil {
		return err
	}
	data, err := json.Marshal(relayConfig{Identity: cert, Routes: map[string]transport.Route{}})
	if err != nil {
		return err
	}
	root, err := os.OpenRoot(filepath.Dir(path))
	if err != nil {
		return err
	}
	defer root.Close()
	if err := publishSubmission(root, filepath.Base(path), data); err != nil {
		return err
	}
	pair, err := cert.keyPair()
	if err != nil {
		return err
	}
	_, err = fmt.Fprintln(output, pairingFingerprint(pair.Leaf))
	return err
}

func validDevice(id string) bool {
	if id == "a" || id == "b" {
		return true
	}
	_, err := hex.DecodeString(id)
	return len(id) == 32 && err == nil && strings.ToLower(id) == id
}

// certificateNames lists the identities a certificate can vouch for, so a host
// mismatch can be reported with both sides instead of a bare "does not match".
func certificateNames(cert *x509.Certificate) string {
	names := append([]string(nil), cert.DNSNames...)
	for _, ip := range cert.IPAddresses {
		names = append(names, ip.String())
	}
	switch len(names) {
	case 0:
		return "no subject alternative name"
	case 1:
		return names[0]
	default:
		return strings.Join(names, ", ")
	}
}

func enrollmentStorage(directory string, routes map[string]transport.Route) (func(string, transport.Route) error, error) {
	entries, err := os.ReadDir(directory)
	if err != nil {
		return nil, err
	}
	for _, entry := range entries {
		id, ok := strings.CutSuffix(entry.Name(), ".route.local.json")
		if !ok {
			continue
		}
		if len(id) != 32 || !validDevice(id) || !entry.Type().IsRegular() {
			return nil, errors.New("invalid stored route")
		}
		var route transport.Route
		if err := loadConfig(filepath.Join(directory, entry.Name()), &route); err != nil {
			return nil, err
		}
		routes[id] = route
	}
	return func(id string, route transport.Route) error {
		root, err := os.OpenRoot(directory)
		if err != nil {
			return err
		}
		defer root.Close()
		data, err := json.Marshal(route)
		if err != nil {
			return err
		}
		return publishSubmission(root, id+".route.local.json", data)
	}, nil
}

func enrollAgent(ctx context.Context, path, rawURL, token, fingerprint string) (string, error) {
	if !filepath.IsAbs(path) || !validPairingFingerprint(token) || !validPairingFingerprint(fingerprint) {
		return "", errors.New("expected an absolute config path, enrollment token and Relay certificate SHA-256")
	}
	address, err := relayAddress(rawURL)
	if err != nil {
		return "", err
	}
	relayURL := address.String()
	var config peerConfig
	err = loadConfig(path, &config)
	newDevice := errors.Is(err, os.ErrNotExist)
	if newDevice {
		device, err := routeToken()
		if err != nil {
			return "", err
		}
		device = device[:32]
		agent, err := routeToken()
		if err != nil {
			return "", err
		}
		phone, err := routeToken()
		if err != nil {
			return "", err
		}
		host, err := issueIdentity(device+".agentdeck.test", x509.ExtKeyUsageServerAuth)
		if err != nil {
			return "", err
		}
		config = peerConfig{RelayURL: relayURL, PhoneRouteToken: phone, Identity: host,
			Devices: map[string]deviceConfig{device: {Token: agent}}}
	} else if err != nil {
		return "", err
	} else if config.RelayURL != relayURL {
		return "", errors.New("existing device belongs to another Relay")
	} else {
		block, _ := pem.Decode([]byte(config.RelayCertificate))
		if block == nil {
			return "", errors.New("stored Relay certificate is invalid")
		}
		cert, err := x509.ParseCertificate(block.Bytes)
		if err != nil || pairingFingerprint(cert) != fingerprint {
			return "", errors.New("existing device is pinned to another Relay certificate")
		}
	}
	if len(config.Devices) != 1 || !validPairingFingerprint(config.PhoneRouteToken) {
		return "", errors.New("existing configuration is not an enrolled device")
	}
	for id, agent := range config.Devices {
		address.Scheme = "https"
		address.Path = "/v1/enroll/" + id
		body, err := json.Marshal(transport.Route{AgentToken: agent.Token, PhoneToken: config.PhoneRouteToken})
		if err != nil {
			return "", err
		}
		ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
		defer cancel()
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, address.String(), bytes.NewReader(body))
		if err != nil {
			return "", err
		}
		req.Header.Set("Authorization", "Bearer "+token)
		req.Header.Set("Content-Type", "application/json")
		client := &http.Client{Transport: &http.Transport{TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS13, InsecureSkipVerify: true, // Verify against the owner's explicit pin.
			VerifyConnection: func(state tls.ConnectionState) error {
				if len(state.PeerCertificates) == 0 {
					return errors.New("relay certificate unavailable")
				}
				cert := state.PeerCertificates[0]
				// Report the three failure modes separately: they need different
				// fixes, and a single "does not match" leaves the operator guessing.
				if actual := pairingFingerprint(cert); subtle.ConstantTimeCompare([]byte(actual), []byte(fingerprint)) != 1 {
					return fmt.Errorf("relay certificate fingerprint mismatch: server sent %s, configured %s", actual, fingerprint)
				}
				if cert.VerifyHostname(address.Hostname()) != nil {
					return fmt.Errorf("relay certificate names %s, which does not cover %q",
						certificateNames(cert), address.Hostname())
				}
				now := time.Now()
				if now.Before(cert.NotBefore) {
					return fmt.Errorf("relay certificate is not valid until %s, check the server clock",
						cert.NotBefore.UTC().Format(time.RFC3339))
				}
				if now.After(cert.NotAfter) {
					return fmt.Errorf("relay certificate expired at %s, re-run relay-init on the server",
						cert.NotAfter.UTC().Format(time.RFC3339))
				}
				if newDevice {
					config.RelayCertificate = string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: cert.Raw}))
					encoded, err := json.Marshal(config)
					if err != nil {
						return err
					}
					root, err := os.OpenRoot(filepath.Dir(path))
					if err != nil {
						return err
					}
					defer root.Close()
					if err := publishSubmission(root, filepath.Base(path), encoded); err != nil {
						return err
					}
					newDevice = false
				}
				return nil
			},
		}}, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
		defer client.CloseIdleConnections()
		reply, err := client.Do(req)
		if err != nil {
			return "", err
		}
		defer reply.Body.Close()
		if reply.StatusCode != http.StatusNoContent {
			return "", fmt.Errorf("Relay registration returned HTTP %d", reply.StatusCode)
		}
		return id, nil
	}
	return "", errors.New("missing device")
}
