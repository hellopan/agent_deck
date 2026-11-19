package transport

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"os"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// trace writes one line per tunnel step to AGENTDECK_TRACE when that variable
// names a file. Pairing failures on the phone are opaque from the desktop, so
// this records what the Agent actually saw without changing normal behaviour.
var (
	traceOnce sync.Once
	traceFile *os.File
)

func trace(format string, args ...any) {
	path := os.Getenv("AGENTDECK_TRACE")
	if path == "" {
		return
	}
	traceOnce.Do(func() {
		file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
		if err == nil {
			traceFile = file
		}
	})
	if traceFile == nil {
		return
	}
	fmt.Fprintf(traceFile, time.Now().Format("15:04:05.000 ")+format+"\n", args...)
}

func dialTunnel(ctx context.Context, endpoint, routeToken string, client *http.Client) (net.Conn, error) {
	address, err := url.Parse(endpoint)
	if err != nil || address.Scheme != "wss" || address.Host == "" || address.User != nil {
		return nil, errors.New("relay endpoint must be a WSS URL without user credentials")
	}
	if client == nil {
		client = http.DefaultClient
	}
	relayClient := *client
	relayClient.CheckRedirect = func(*http.Request, []*http.Request) error {
		return http.ErrUseLastResponse
	}
	options := &websocket.DialOptions{
		HTTPClient: &relayClient,
		HTTPHeader: http.Header{"Authorization": {"Bearer " + routeToken}},
	}
	dialCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	ws, _, err := websocket.Dial(dialCtx, address.String(), options)
	if err != nil {
		return nil, err
	}
	conn := websocket.NetConn(ctx, ws, websocket.MessageBinary)
	ws.SetReadLimit(maxFrameSize)
	return conn, nil
}

// DialPhone opens an authenticated inner TLS stream to a specific Agent.
func DialPhone(ctx context.Context, endpoint, routeToken string, client *http.Client, identity *tls.Config) (*tls.Conn, error) {
	if identity == nil || identity.InsecureSkipVerify || identity.ServerName == "" || identity.RootCAs == nil {
		return nil, errors.New("phone requires a trusted Agent identity and name")
	}
	config := identity.Clone()
	config.MinVersion = tls.VersionTLS13
	conn, err := dialTunnel(ctx, endpoint, routeToken, client)
	if err != nil {
		return nil, err
	}
	secured := tls.Client(conn, config)
	handshakeCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := secured.HandshakeContext(handshakeCtx); err != nil {
		conn.Close()
		return nil, err
	}
	return secured, nil
}

// RunAgent keeps a device registered and opens a fresh tunnel when a phone leaves.
// The callback runs only after the phone's certificate has been verified.
func RunAgent(ctx context.Context, endpoint, routeToken string, client *http.Client, identity *tls.Config, serve func(*tls.Conn)) error {
	if identity == nil || !((identity.ClientAuth == tls.RequireAndVerifyClientCert && identity.ClientCAs != nil) ||
		(identity.ClientAuth == tls.RequireAnyClientCert && identity.VerifyConnection != nil)) {
		return errors.New("Agent requires a trusted phone identity")
	}
	config := identity.Clone()
	config.MinVersion = tls.VersionTLS13
	for ctx.Err() == nil {
		conn, err := dialTunnel(ctx, endpoint, routeToken, client)
		if err != nil {
			trace("dial failed: %v", err)
		}
		if err == nil {
			trace("tunnel open local=%s", conn.LocalAddr())
			secured := tls.Server(conn, config)
			if handshakeErr := secured.HandshakeContext(ctx); handshakeErr == nil {
				state := secured.ConnectionState()
				subject := ""
				if len(state.PeerCertificates) > 0 {
					subject = state.PeerCertificates[0].Subject.String()
				}
				trace("handshake ok version=%x cipher=%s peer=%q", state.Version,
					tls.CipherSuiteName(state.CipherSuite), subject)
				serve(secured)
				trace("serve returned")
			} else {
				trace("handshake failed: %v", handshakeErr)
			}
			conn.Close()
		}
		retry := time.NewTimer(250 * time.Millisecond)
		select {
		case <-retry.C:
		case <-ctx.Done():
			retry.Stop()
		}
	}
	return ctx.Err()
}
