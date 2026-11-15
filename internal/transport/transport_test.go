package transport

import (
	"bufio"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"fmt"
	"math/big"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestTwoDevicesAndRelayRestart(t *testing.T) {
	ctx, cancel := context.WithTimeout(t.Context(), 15*time.Second)
	defer cancel()
	routes := map[string]Route{
		"a": {AgentToken: "test-agent-a", PhoneToken: "test-phone-a"},
		"b": {AgentToken: "test-agent-b", PhoneToken: "test-phone-b"},
	}
	var activeRelay atomic.Pointer[http.Handler]
	firstCtx, stopFirst := context.WithCancel(ctx)
	defer stopFirst()
	first := NewRelay(firstCtx, routes)
	activeRelay.Store(&first)
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		(*activeRelay.Load()).ServeHTTP(w, req)
	}))
	t.Cleanup(server.Close)
	base := "wss" + strings.TrimPrefix(server.URL, "https") + "/v1/"
	phone, phoneTrust := testIdentity(t, "phone.test", x509.ExtKeyUsageClientAuth)
	otherPhone, _ := testIdentity(t, "unpaired.test", x509.ExtKeyUsageClientAuth)
	phoneConfigs := make(map[string]*tls.Config)
	for device, route := range routes {
		name := device + ".test"
		agent, agentTrust := testIdentity(t, name, x509.ExtKeyUsageServerAuth)
		phoneConfigs[device] = &tls.Config{
			Certificates: []tls.Certificate{phone}, RootCAs: agentTrust, ServerName: name,
		}
		agentConfig := &tls.Config{
			Certificates: []tls.Certificate{agent}, ClientCAs: phoneTrust, ClientAuth: tls.RequireAndVerifyClientCert,
		}
		workerDone := make(chan struct{})
		t.Cleanup(func() { <-workerDone })
		go func() {
			defer close(workerDone)
			RunAgent(ctx, base+"agent/"+device, route.AgentToken, server.Client(), agentConfig, func(conn *tls.Conn) {
				scanner := bufio.NewScanner(conn)
				for scanner.Scan() {
					if _, err := fmt.Fprintln(conn, device+":"+scanner.Text()); err != nil {
						return
					}
					if scanner.Text() == "finish" {
						return
					}
				}
			})
		}()
	}

	openPhone := func(device string) *tls.Conn {
		t.Helper()
		return dialWhenReady(t, ctx, base+"phone/"+device, routes[device].PhoneToken, server.Client(), phoneConfigs[device])
	}

	connA := openPhone("a")
	defer connA.Close()
	connB := openPhone("b")
	defer connB.Close()
	checkEcho(t, connA, "probe", "a:probe")
	checkEcho(t, connB, "probe", "b:probe")

	request, err := http.NewRequestWithContext(ctx, http.MethodGet, server.URL+"/v1/phone/b", nil)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+routes["a"].PhoneToken)
	response, err := server.Client().Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusUnauthorized {
		t.Fatalf("A route credential on B returned %d", response.StatusCode)
	}

	stopFirst()
	second := NewRelay(ctx, routes)
	activeRelay.Store(&second)
	connA.SetReadDeadline(time.Now().Add(3 * time.Second))
	if _, err := connA.Read(make([]byte, 1)); err == nil {
		t.Fatal("first relay left the phone stream open")
	}

	wrongHost := phoneConfigs["b"].Clone()
	if conn, err := dialWhenAvailable(ctx, base+"phone/a", routes["a"].PhoneToken, server.Client(), wrongHost); err == nil {
		conn.Close()
		t.Fatal("phone accepted a different Agent identity")
	}
	unpaired := phoneConfigs["a"].Clone()
	unpaired.Certificates = []tls.Certificate{otherPhone}
	if conn, err := dialWhenAvailable(ctx, base+"phone/a", routes["a"].PhoneToken, server.Client(), unpaired); err == nil {
		defer conn.Close()
		fmt.Fprintln(conn, "probe")
		if _, err := bufio.NewReader(conn).ReadString('\n'); err == nil {
			t.Fatal("Agent served an unpaired phone")
		}
	}
	newA := openPhone("a")
	defer newA.Close()
	newB := openPhone("b")
	defer newB.Close()
	checkEcho(t, newA, "finish", "a:finish")
	checkEcho(t, newB, "probe", "b:probe")
}

func checkEcho(t *testing.T, conn *tls.Conn, message, expected string) {
	t.Helper()
	if conn.ConnectionState().Version != tls.VersionTLS13 {
		t.Fatal("inner stream did not negotiate TLS 1.3")
	}
	if _, err := fmt.Fprintln(conn, message); err != nil {
		t.Fatal(err)
	}
	line, err := bufio.NewReader(conn).ReadString('\n')
	if err != nil || strings.TrimSpace(line) != expected {
		t.Fatalf("probe got %q, %v; want %q", line, err, expected)
	}
}

func dialWhenReady(t *testing.T, ctx context.Context, endpoint, token string, client *http.Client, identity *tls.Config) *tls.Conn {
	t.Helper()
	conn, err := dialWhenAvailable(ctx, endpoint, token, client, identity)
	if err != nil {
		t.Fatal(err)
	}
	return conn
}

func dialWhenAvailable(ctx context.Context, endpoint, token string, client *http.Client, identity *tls.Config) (*tls.Conn, error) {
	for {
		conn, err := DialPhone(ctx, endpoint, token, client, identity)
		if err == nil || !strings.Contains(err.Error(), "503") {
			return conn, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(30 * time.Millisecond):
		}
	}
}

func testIdentity(t *testing.T, name string, usage x509.ExtKeyUsage) (tls.Certificate, *x509.CertPool) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{
		SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: name}, DNSNames: []string{name},
		NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour),
		KeyUsage: x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign, ExtKeyUsage: []x509.ExtKeyUsage{usage},
		BasicConstraintsValid: true, IsCA: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	trusted := x509.NewCertPool()
	trusted.AddCert(certificate)
	return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key, Leaf: certificate}, trusted
}
