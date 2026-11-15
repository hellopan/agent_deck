package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"time"

	"github.com/hellopan/agent_deck/internal/transport"
)

type identity struct {
	Certificate string `json:"certificate"`
	PrivateKey  string `json:"privateKey"`
}

func (id identity) keyPair() (tls.Certificate, error) {
	return tls.X509KeyPair([]byte(id.Certificate), []byte(id.PrivateKey))
}

type relayConfig struct {
	Identity identity                   `json:"identity"`
	Routes   map[string]transport.Route `json:"routes"`
}

type deviceConfig struct {
	Token           string `json:"token"`
	PeerName        string `json:"peerName"`
	PeerCertificate string `json:"peerCertificate"`
}

type peerConfig struct {
	PhoneRouteToken  string                  `json:"phoneRouteToken,omitempty"`
	RelayURL         string                  `json:"relayUrl"`
	RelayCertificate string                  `json:"relayCertificate"`
	Identity         identity                `json:"identity"`
	Devices          map[string]deviceConfig `json:"devices"`
}

func loadConfig(path string, dest any) error {
	file, err := os.Open(path)
	if err != nil {
		return err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() || info.Size() > 64*1024 {
		return errors.New("expected a regular configuration file at most 64 KiB")
	}
	decoder := json.NewDecoder(file)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(dest); err != nil {
		return fmt.Errorf("read configuration: %w", err)
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return errors.New("unexpected data after configuration")
	}
	return nil
}

func relayAddress(raw string) (*url.URL, error) {
	address, err := url.Parse(raw)
	if err != nil || address.Scheme != "wss" || address.Hostname() == "" || address.User != nil ||
		(address.Path != "" && address.Path != "/") || address.RawQuery != "" || address.Fragment != "" {
		return nil, errors.New("relay URL must be a WSS origin, for example wss://localhost:8443")
	}
	return address, nil
}

func certificatePool(certificate string) (*x509.CertPool, error) {
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM([]byte(certificate)) {
		return nil, errors.New("missing or invalid trusted certificate")
	}
	return pool, nil
}

func issueIdentity(name string, usage x509.ExtKeyUsage) (identity, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return identity{}, err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return identity{}, err
	}
	now := time.Now()
	cert := &x509.Certificate{
		SerialNumber: serial.Add(serial, big.NewInt(1)), Subject: pkix.Name{CommonName: name},
		NotBefore: now.Add(-5 * time.Minute), NotAfter: now.Add(7 * 24 * time.Hour),
		KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{usage},
	}
	if ip := net.ParseIP(name); ip != nil {
		cert.IPAddresses = []net.IP{ip}
	} else {
		cert.DNSNames = []string{name}
	}
	der, err := x509.CreateCertificate(rand.Reader, cert, cert, &key.PublicKey, key)
	if err != nil {
		return identity{}, err
	}
	privateKey, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		return identity{}, err
	}
	return identity{
		Certificate: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})),
		PrivateKey:  string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: privateKey})),
	}, nil
}

func routeToken() (string, error) {
	var token [32]byte
	if _, err := rand.Read(token[:]); err != nil {
		return "", err
	}
	return hex.EncodeToString(token[:]), nil
}

func createLab(directory, rawURL string) error {
	address, err := relayAddress(rawURL)
	if err != nil {
		return err
	}
	relayIdentity, err := issueIdentity(address.Hostname(), x509.ExtKeyUsageServerAuth)
	if err != nil {
		return err
	}
	phoneIdentity, err := issueIdentity("phone.agentdeck.test", x509.ExtKeyUsageClientAuth)
	if err != nil {
		return err
	}
	relay := relayConfig{Identity: relayIdentity, Routes: make(map[string]transport.Route)}
	phone := peerConfig{RelayURL: address.String(), RelayCertificate: relayIdentity.Certificate,
		Identity: phoneIdentity, Devices: make(map[string]deviceConfig)}
	configs := make(map[string]any)
	for _, device := range []string{"a", "b"} {
		name := device + ".agentdeck.test"
		agentIdentity, err := issueIdentity(name, x509.ExtKeyUsageServerAuth)
		if err != nil {
			return err
		}
		agentToken, err := routeToken()
		if err != nil {
			return err
		}
		phoneToken, err := routeToken()
		if err != nil {
			return err
		}
		relay.Routes[device] = transport.Route{AgentToken: agentToken, PhoneToken: phoneToken}
		phone.Devices[device] = deviceConfig{Token: phoneToken, PeerName: name, PeerCertificate: agentIdentity.Certificate}
		configs["agent-"+device+".json"] = peerConfig{
			RelayURL: address.String(), RelayCertificate: relayIdentity.Certificate, Identity: agentIdentity,
			Devices: map[string]deviceConfig{device: {Token: agentToken, PeerCertificate: phoneIdentity.Certificate}},
		}
	}
	configs["relay.json"] = relay
	configs["phone.json"] = phone
	if err := os.Mkdir(directory, 0o700); err != nil {
		return fmt.Errorf("create new lab directory: %w", err)
	}
	for name, config := range configs {
		encoded, err := json.MarshalIndent(config, "", "  ")
		if err != nil {
			return err
		}
		if err := os.WriteFile(filepath.Join(directory, name), append(encoded, '\n'), 0o600); err != nil {
			return err
		}
	}
	return nil
}
