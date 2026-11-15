package main

import (
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

const pairingMessage = "agentdeck-pair-v1 "

type pairingOffer struct {
	Code    string    `json:"code"`
	Device  string    `json:"device"`
	Expires time.Time `json:"expires"`
}

// pairingInvite transfers only the route credential and public trust material.
func pairingInvite(agent, folder, device string, output io.Writer) error {
	var host peerConfig
	if err := loadConfig(agent, &host); err != nil {
		return err
	}
	if !filepath.IsAbs(folder) || !validDevice(device) {
		return errors.New("pairing requires an absolute private folder and device ID")
	}
	if _, ok := host.Devices[device]; !ok || host.PhoneRouteToken == "" {
		return errors.New("Agent has no phone route. Enroll this PC in the Relay first.")
	}
	entry := deviceConfig{Token: host.PhoneRouteToken, PeerName: device + ".agentdeck.test", PeerCertificate: host.Identity.Certificate}
	code, err := routeToken()
	if err != nil {
		return err
	}
	offer := pairingOffer{Code: code, Device: device, Expires: time.Now().Add(2 * time.Minute)}
	data, err := json.Marshal(offer)
	if err != nil {
		return err
	}
	root, err := os.OpenRoot(folder)
	if err != nil {
		return err
	}
	defer root.Close()
	if err := publishSubmission(root, "offer-"+code+".local.json", data); err != nil {
		return err
	}
	return json.NewEncoder(output).Encode(struct {
		Version          int          `json:"version"`
		Code             string       `json:"code"`
		Device           string       `json:"device"`
		Host             string       `json:"host,omitempty"`
		RelayURL         string       `json:"relayUrl"`
		RelayCertificate string       `json:"relayCertificate"`
		Entry            deviceConfig `json:"entry"`
	}{1, code, device, hostName(), host.RelayURL, host.RelayCertificate, entry})
}

// hostName labels a pairing invite so the phone can name the machine it drives.
// The value is display-only; authorization never depends on it.
func hostName() string {
	name, err := os.Hostname()
	if err != nil {
		return ""
	}
	return strings.TrimSpace(name)
}

func pairingFingerprint(cert *x509.Certificate) string {
	sum := sha256.Sum256(cert.Raw)
	return hex.EncodeToString(sum[:])
}

func submitPairing(folder, device, line string, cert *x509.Certificate) error {
	code := strings.TrimSpace(line)
	if folder == "" || !validPairingFingerprint(code) || cert == nil ||
		cert.CheckSignature(cert.SignatureAlgorithm, cert.RawTBSCertificate, cert.Signature) != nil ||
		time.Now().Before(cert.NotBefore) || time.Now().After(cert.NotAfter) {
		return errors.New("invalid pairing request")
	}
	root, err := os.OpenRoot(folder)
	if err != nil {
		return err
	}
	defer root.Close()
	offerFile, err := root.Open("offer-" + code + ".local.json")
	if err != nil {
		return err
	}
	var offer pairingOffer
	err = json.NewDecoder(io.LimitReader(offerFile, 1024)).Decode(&offer)
	offerFile.Close()
	if err != nil || offer.Code != code || offer.Device != device || !time.Now().Before(offer.Expires) {
		return errors.New("pairing offer expired or invalid")
	}
	data := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: cert.Raw})
	return publishSubmission(root, "candidate-"+code+".local.json", data)
}

func confirmPairing(folder, device, code, fingerprint string) error {
	root, data, cert, err := pairingCandidate(folder, device, code)
	if err != nil {
		return err
	}
	defer root.Close()
	if !validPairingFingerprint(fingerprint) || pairingFingerprint(cert) != fingerprint {
		return errors.New("phone fingerprint does not match")
	}
	if err := publishSubmission(root, "trusted-"+device+"-"+fingerprint+".local.json", data); err != nil {
		return err
	}
	_ = root.Remove("offer-" + code + ".local.json")
	return nil
}

func pairingCandidate(folder, device, code string) (*os.Root, []byte, *x509.Certificate, error) {
	if !filepath.IsAbs(folder) || !validDevice(device) || !validPairingFingerprint(code) {
		return nil, nil, nil, errors.New("invalid pairing confirmation")
	}
	root, err := os.OpenRoot(folder)
	if err != nil {
		return nil, nil, nil, err
	}
	success := false
	defer func() {
		if !success {
			root.Close()
		}
	}()
	offerFile, err := root.Open("offer-" + code + ".local.json")
	if err != nil {
		return nil, nil, nil, err
	}
	var offer pairingOffer
	err = json.NewDecoder(io.LimitReader(offerFile, 1024)).Decode(&offer)
	offerFile.Close()
	if err != nil || offer.Code != code || offer.Device != device || !time.Now().Before(offer.Expires) {
		return nil, nil, nil, errors.New("pairing offer expired or invalid")
	}
	candidate, err := root.Open("candidate-" + code + ".local.json")
	if err != nil {
		return nil, nil, nil, err
	}
	defer candidate.Close()
	data, err := io.ReadAll(io.LimitReader(candidate, 8192))
	block, _ := pem.Decode(data)
	if err != nil || block == nil {
		return nil, nil, nil, errors.New("invalid candidate certificate")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return nil, nil, nil, err
	}
	success = true
	return root, cert.Raw, cert, nil
}

func validPairingFingerprint(value string) bool {
	_, err := hex.DecodeString(value)
	return len(value) == 64 && err == nil && strings.ToLower(value) == value
}

func authorizedPhone(folder, device string, cert *x509.Certificate, legacy *x509.CertPool) bool {
	if cert == nil {
		return false
	}
	now := time.Now()
	if now.Before(cert.NotBefore) || now.After(cert.NotAfter) {
		return false
	}
	if _, err := cert.Verify(x509.VerifyOptions{Roots: legacy, KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth}}); err == nil {
		return true
	}
	if folder == "" {
		return false
	}
	root, err := os.OpenRoot(folder)
	if err != nil {
		return false
	}
	defer root.Close()
	file, err := root.Open("trusted-" + device + "-" + pairingFingerprint(cert) + ".local.json")
	if err != nil {
		return false
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, 8192))
	return err == nil && string(data) == string(cert.Raw)
}
