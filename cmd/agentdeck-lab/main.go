package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/hellopan/agent_deck/internal/agentfind"
	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
	"github.com/hellopan/agent_deck/internal/transport"
)

const probeMessage = "agentdeck-probe-v1"
const projectsMessage = "agentdeck-projects-v1"
const taskLogMessage = "agentdeck-task-log-v1 "
const remoteCancelMessage = "agentdeck-task-cancel-v1 "
const receiptMessage = "agentdeck-task-receipt-v1 "
const queueMessage = "agentdeck-queue-v1"
const approvalsMessage = "agentdeck-approvals-v1"
const decideMessage = "agentdeck-approve-v1 "

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if err := run(ctx, os.Args[1:]); err != nil {
		log.Print(err)
		os.Exit(1)
	}
}

func run(ctx context.Context, args []string) error {
	const usage = "usage: agentdeck-lab {init|relay|enroll|agent|pair-invite|pair-confirm|pair-revoke|probe|projects|queue|submit|task-receipt|approvals|approve|inbox|cancel-task|remote-cancel|task-log|service|codex-check|detect} [options]"
	if len(args) == 0 {
		return errors.New(usage)
	}
	mode := args[0]
	options := flag.NewFlagSet(mode, flag.ContinueOnError)
	switch mode {
	case "init":
		directory := options.String("out", "", "new directory for generated test credentials")
		relayURL := options.String("relay-url", "wss://127.0.0.1:8443", "relay origin for the generated endpoints")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		if *directory == "" {
			return errors.New("init requires -out")
		}
		return createLab(*directory, *relayURL)
	case "relay":
		path := options.String("config", "", "relay configuration")
		listen := options.String("listen", "127.0.0.1:8443", "relay listen address")
		routes := options.String("enrollment-dir", "", "private directory for enrolled routes")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		return runRelay(ctx, *path, *listen, *routes)
	case "relay-init":
		path := options.String("config", "", "new Relay configuration file")
		endpoint := options.String("relay-url", "", "WSS address phones and PCs can reach")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		return initEnrollmentRelay(*path, *endpoint, os.Stdout)
	case "relay-fingerprint":
		path := options.String("config", "", "Relay configuration")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		var config relayConfig
		if err := loadConfig(*path, &config); err != nil {
			return err
		}
		cert, err := config.Identity.keyPair()
		if err != nil {
			return err
		}
		_, err = fmt.Fprintln(os.Stdout, pairingFingerprint(cert.Leaf))
		return err
	case "enroll":
		path := options.String("config", "", "local Agent configuration destination")
		endpoint := options.String("relay-url", "", "Relay WSS origin")
		fingerprint := options.String("fingerprint", "", "Relay certificate SHA-256")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		token, err := bufio.NewReader(io.LimitReader(os.Stdin, 128)).ReadString('\n')
		if err != nil {
			return err
		}
		id, err := enrollAgent(ctx, *path, *endpoint, strings.TrimSpace(token), *fingerprint)
		if err == nil {
			_, err = fmt.Fprintln(os.Stdout, id)
		}
		return err
	case "agent", "probe", "projects", "task-log", "submit", "remote-cancel", "task-receipt", "queue", "approvals", "approve":
		path := options.String("config", "", "endpoint configuration")
		policy := options.String("projects", "", "absolute local policy path (agent only)")
		logDir := options.String("task-logs", "", "absolute local task log directory (agent only)")
		codexExecutable := options.String("codex", "", "absolute path to Codex CLI (agent only)")
		codexHome := options.String("codex-home", "", "absolute path to the account Codex home (agent only)")
		query := addTaskLogOptions(options)
		var peer peerOptions
		options.StringVar(&peer.pairing, "pairing-dir", "", "absolute private pairing directory (agent only)")
		options.StringVar(&peer.approvalID, "approval-id", "", "pending approval ID (approve only)")
		options.StringVar(&peer.decision, "decision", "", "accept or decline (approve only)")
		addSubmitOptions(options, &peer)
		device := options.String("device", "", "lab device a or b")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		if !validDevice(*device) {
			return errors.New("invalid device ID")
		}
		peer.policy, peer.logDir, peer.query = *policy, *logDir, *query
		peer.codex = codexOptions{policy: *policy, executable: *codexExecutable, home: *codexHome}
		peer.submission.ProjectID = query.ProjectID
		return runPeer(ctx, mode, *path, *device, log.Default(), peer)
	case "pair-invite":
		host := options.String("config", "", "local Agent config")
		folder := options.String("pairing-dir", "", "private pairing directory")
		device := options.String("device", "a", "device ID")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		return pairingInvite(*host, *folder, *device, os.Stdout)
	case "pair-confirm", "pair-candidate", "pair-revoke":
		folder := options.String("pairing-dir", "", "private pairing directory")
		code := options.String("code", "", "offer code")
		fingerprint := options.String("fingerprint", "", "phone certificate SHA-256")
		device := options.String("device", "a", "device ID")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		if mode == "pair-revoke" {
			return revokePairing(*folder, *device, *fingerprint)
		}
		if mode == "pair-candidate" {
			root, _, cert, err := pairingCandidate(*folder, *device, *code)
			if err != nil {
				return err
			}
			defer root.Close()
			_, err = fmt.Fprintln(os.Stdout, pairingFingerprint(cert))
			return err
		}
		return confirmPairing(*folder, *device, *code, *fingerprint)
	case "codex-check":
		codex := addCodexOptions(options)
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		return checkCodex(ctx, *codex, log.Default())
	case "cancel-task":
		policy := options.String("projects", "", "absolute local project policy")
		logDir := options.String("task-logs", "", "absolute local task log directory")
		inbox := options.String("task-inbox", "", "absolute local task inbox directory")
		project := options.String("project-id", "", "authorized project ID")
		id := options.String("task-id", "", "existing task ID")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		return requestTaskCancel(peerOptions{policy: *policy, logDir: *logDir, inbox: *inbox},
			taskLogQuery{ProjectID: *project, TaskID: *id})
	case "inbox":
		policy := options.String("projects", "", "absolute local project policy")
		logDir := options.String("task-logs", "", "absolute local task log directory")
		inbox := options.String("task-inbox", "", "absolute local task inbox directory")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		items, err := listQueue(peerOptions{policy: *policy, logDir: *logDir, inbox: *inbox})
		if err != nil {
			return err
		}
		return json.NewEncoder(os.Stdout).Encode(queueView{Items: items})
	case "detect":
		hint := options.String("codex", "", "absolute path to Codex CLI")
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		return json.NewEncoder(os.Stdout).Encode(agentfind.DetectAll(*hint))
	case "service":
		name := options.String("name", "AgentDeckLab", "Windows service name registered with SCM")
		path := options.String("config", "", "absolute endpoint configuration path")
		logPath := options.String("log", "", "absolute local service log path")
		logDir := options.String("task-logs", "", "absolute local task log directory")
		inbox := options.String("task-inbox", "", "absolute local task inbox directory")
		device := options.String("device", "", "lab device a or b")
		codex := addCodexOptions(options)
		if err := parseFlags(options, args[1:]); err != nil {
			return err
		}
		if *name == "" || !filepath.IsAbs(*path) || !filepath.IsAbs(*logPath) || (*device != "a" && *device != "b") {
			return errors.New("service requires a name, absolute config and log paths, and device a or b")
		}
		return launchService(ctx, *name, func(serviceCtx context.Context) error {
			return runServiceAgent(serviceCtx, *path, *logPath, *device, *codex, *logDir, *inbox)
		})
	default:
		return errors.New(usage)
	}
}

type codexOptions struct {
	executable string
	home       string
	policy     string
	projectID  string
}

func addCodexOptions(options *flag.FlagSet) *codexOptions {
	config := &codexOptions{}
	options.StringVar(&config.executable, "codex", "", "absolute path to Codex CLI")
	options.StringVar(&config.home, "codex-home", "", "absolute path to the account's Codex home")
	options.StringVar(&config.policy, "projects", "", "absolute path to the local project policy")
	options.StringVar(&config.projectID, "project-id", "", "authorized project ID")
	return config
}

func checkCodex(ctx context.Context, config codexOptions, logger *log.Logger) error {
	resolved, source, err := config.resolve()
	if err != nil {
		return err
	}
	status, err := codexprobe.Check(ctx, resolved)
	if err != nil {
		return err
	}
	logger.Printf("Codex stdio initialized; executable=%s source=%s account_configured=%t openai_auth_required=%t project_id=%s",
		resolved.Executable, source, status.AccountConfigured, status.OpenAIAuthRequired, config.projectID)
	return nil
}

// resolve turns flags into one concrete Codex invocation. An empty -codex is
// not a configuration error: the installed build is detected instead, because
// making the user paste a path is the friction this project exists to remove.
func (config codexOptions) resolve() (codexprobe.Config, string, error) {
	directory, mode, err := projects.ResolveMode(config.policy, config.projectID)
	if err != nil {
		return codexprobe.Config{}, "", fmt.Errorf("authorize project: %w", err)
	}
	executable, source, err := config.findCodex()
	if err != nil {
		return codexprobe.Config{}, "", err
	}
	home := config.home
	if home == "" {
		// Account state and session rollouts live here. Every turn of a
		// conversation must use the same home, or "接着说" resumes nothing.
		userHome, homeErr := os.UserHomeDir()
		if homeErr != nil {
			return codexprobe.Config{}, "", fmt.Errorf("locate the Codex home: %w", homeErr)
		}
		home = filepath.Join(userHome, ".codex")
	}
	return codexprobe.Config{Executable: executable, ProjectDir: directory, CodexHome: home, Sandbox: string(mode)}, source, nil
}

// findCodex returns the executable and the name of the source it came from.
func (config codexOptions) findCodex() (string, string, error) {
	if config.executable != "" {
		return config.executable, "config", nil
	}
	tool := agentfind.Detect(agentfind.Codex, "")
	if !tool.Usable {
		where := tool.Path
		if where == "" {
			where = "没有找到安装位置"
		}
		return "", "", fmt.Errorf("Codex 不可用：%s（%s）；可用 -codex 手动指定", tool.Problem, where)
	}
	return tool.Path, tool.Source, nil
}

func readPromptFile(taskPath string, maxBytes int64) ([]byte, error) {
	file, err := os.Open(taskPath)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() {
		return nil, errors.New("prompt file must be a regular file")
	}
	return io.ReadAll(io.LimitReader(file, maxBytes+1))
}

func runServiceAgent(ctx context.Context, configPath, logPath, device string, codex codexOptions, logDir, inbox string) error {
	file, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return fmt.Errorf("open service log: %w", err)
	}
	defer file.Close()
	logger := log.New(file, "", log.LstdFlags|log.LUTC)
	logger.Printf("service started for device %s", device)
	ctx, cancel := context.WithCancel(ctx)
	var checks sync.WaitGroup
	if codex.executable != "" || codex.home != "" || codex.projectID != "" {
		checks.Go(func() {
			if err := checkCodex(ctx, codex, logger); err != nil {
				logger.Printf("Codex startup check failed: %v", err)
			}
		})
	}
	err = runPeer(ctx, "agent", configPath, device, logger, peerOptions{policy: codex.policy, logDir: logDir, inbox: inbox, codex: codex})
	cancel()
	checks.Wait()
	if err != nil {
		logger.Printf("service stopped with error: %v", err)
	} else {
		logger.Print("service stopped")
	}
	return err
}

func parseFlags(options *flag.FlagSet, args []string) error {
	if err := options.Parse(args); err != nil {
		return err
	}
	if options.NArg() != 0 {
		return errors.New("unexpected positional arguments")
	}
	return nil
}

func runRelay(ctx context.Context, path, address, enrollmentDir string) error {
	var config relayConfig
	if err := loadConfig(path, &config); err != nil {
		return err
	}
	certificate, err := config.Identity.keyPair()
	if err != nil {
		return fmt.Errorf("read relay identity: %w", err)
	}
	listener, err := net.Listen("tcp", address)
	if err != nil {
		return err
	}
	defer listener.Close()
	handler := transport.NewRelay(ctx, config.Routes)
	if enrollmentDir != "" {
		token := os.Getenv("AGENTDECK_ENROLLMENT_TOKEN")
		if !validPairingFingerprint(token) {
			return errors.New("invalid AGENTDECK_ENROLLMENT_TOKEN")
		}
		publish, err := enrollmentStorage(enrollmentDir, config.Routes)
		if err != nil {
			return err
		}
		handler = transport.NewEnrollmentRelay(ctx, config.Routes, token, publish)
	}
	server := &http.Server{
		Handler:           handler,
		TLSConfig:         &tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS13},
		ReadHeaderTimeout: 5 * time.Second,
	}
	stop := context.AfterFunc(ctx, func() { server.Close() })
	defer stop()
	log.Printf("lab relay listening on %s", listener.Addr())
	if err := server.ServeTLS(listener, "", ""); !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}

type peerOptions struct {
	pairing    string
	policy     string
	logDir     string
	query      taskLogQuery
	inbox      string
	promptPath string
	submission taskSubmission
	codex      codexOptions
	approvalID string
	decision   string
}

func runPeer(ctx context.Context, mode, path, device string, logger *log.Logger, opts peerOptions) error {
	if opts.pairing != "" && !filepath.IsAbs(opts.pairing) {
		return errors.New("pairing directory must be absolute")
	}
	if mode == "agent" && opts.logDir != "" && (!filepath.IsAbs(opts.logDir) || !filepath.IsAbs(opts.policy)) {
		return errors.New("task logs require absolute log and project policy paths")
	}
	if mode == "agent" && opts.inbox != "" &&
		(opts.logDir == "" || !filepath.IsAbs(opts.inbox) || !filepath.IsAbs(opts.logDir) || !filepath.IsAbs(opts.policy)) {
		return errors.New("task inbox requires absolute inbox, task log and project policy paths")
	}
	var config peerConfig
	if err := loadConfig(path, &config); err != nil {
		return err
	}
	entry, found := config.Devices[device]
	if !found || entry.Token == "" {
		return errors.New("configuration does not grant access to the selected device")
	}
	address, err := relayAddress(config.RelayURL)
	if err != nil {
		return err
	}
	relayTrust, err := certificatePool(config.RelayCertificate)
	if err != nil {
		return fmt.Errorf("read relay trust: %w", err)
	}
	peerTrust := x509.NewCertPool()
	if entry.PeerCertificate != "" || mode != "agent" || opts.pairing == "" {
		peerTrust, err = certificatePool(entry.PeerCertificate)
		if err != nil {
			return fmt.Errorf("read peer trust: %w", err)
		}
	}
	certificate, err := config.Identity.keyPair()
	if err != nil {
		return fmt.Errorf("read endpoint identity: %w", err)
	}
	httpTransport := http.DefaultTransport.(*http.Transport).Clone()
	httpTransport.TLSClientConfig = &tls.Config{RootCAs: relayTrust, MinVersion: tls.VersionTLS13}
	defer httpTransport.CloseIdleConnections()
	client := &http.Client{Transport: httpTransport}
	inner := &tls.Config{Certificates: []tls.Certificate{certificate}}
	if mode == "agent" {
		address.Path = "/v1/agent/" + device
		inner.ClientAuth = tls.RequireAndVerifyClientCert
		inner.ClientCAs = peerTrust
		if opts.pairing != "" {
			inner.ClientAuth = tls.RequireAnyClientCert
			inner.ClientCAs = nil
			inner.VerifyConnection = func(state tls.ConnectionState) error {
				// The pairing verb is handled separately after proof of key possession.
				if len(state.PeerCertificates) != 1 {
					return errors.New("expected a phone certificate")
				}
				return nil
			}
		}
		runtime := &agentRuntime{peer: opts, device: device, logger: logger, trust: peerTrust}
		// The queue consumer outlives every tunnel: a phone connects to poll,
		// not to keep the task alive.
		runner, stopRunner := context.WithCancel(ctx)
		defer stopRunner()
		go runtime.run(runner)
		logger.Printf("lab agent %s connecting to relay", device)
		err := transport.RunAgent(ctx, address.String(), entry.Token, client, inner, runtime.serve)
		if errors.Is(err, context.Canceled) {
			return nil
		}
		return err
	}
	address.Path = "/v1/phone/" + device
	inner.RootCAs = peerTrust
	inner.ServerName = entry.PeerName
	return probe(ctx, address.String(), entry.Token, client, inner, device, mode, opts)
}

func probe(ctx context.Context, endpoint, token string, client *http.Client, inner *tls.Config, device, mode string, opts peerOptions) error {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	conn, err := transport.DialPhone(ctx, endpoint, token, client, inner)
	if err != nil {
		return err
	}
	defer conn.Close()
	if mode == "task-receipt" {
		return queryReceipt(conn, opts)
	}
	if mode == "queue" {
		return fetchQueue(conn)
	}
	if mode == "approvals" {
		return fetchApprovals(conn)
	}
	if mode == "approve" {
		return sendDecision(conn, opts)
	}
	if mode == "remote-cancel" {
		return sendRemoteCancel(conn, opts.query)
	}
	if mode == "submit" {
		return sendSubmission(conn, opts)
	}
	if mode == "task-log" {
		page, err := fetchTaskLog(conn, opts.query)
		if err != nil {
			return err
		}
		return json.NewEncoder(os.Stdout).Encode(page)
	}
	if mode == "projects" {
		if _, err := fmt.Fprintln(conn, projectsMessage); err != nil {
			return err
		}
		var ids []string
		if err := json.NewDecoder(io.LimitReader(conn, 128*1024)).Decode(&ids); err != nil || ids == nil {
			return errors.New("remote project list unavailable")
		}
		return json.NewEncoder(os.Stdout).Encode(ids)
	}
	if _, err := fmt.Fprintln(conn, probeMessage); err != nil {
		return err
	}
	if err := receiveLine(conn, probeMessage+":"+device); err != nil {
		return err
	}
	fmt.Printf("device %s: authenticated TLS 1.3 probe succeeded\n", device)
	return nil
}

func receiveLine(conn *tls.Conn, expected string) error {
	line, err := bufio.NewReaderSize(conn, 128).ReadSlice('\n')
	if err != nil {
		return err
	}
	if string(line) != expected+"\n" {
		return errors.New("unexpected probe message")
	}
	return nil
}
