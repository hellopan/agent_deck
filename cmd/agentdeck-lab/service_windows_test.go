package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"log"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
	"github.com/hellopan/agent_deck/internal/transport"
	"golang.org/x/sys/windows/svc"
)

func TestServiceStopsItsWorker(t *testing.T) {
	ctx, cancel := context.WithTimeout(t.Context(), 3*time.Second)
	defer cancel()
	requests := make(chan svc.ChangeRequest, 1)
	states := make(chan svc.Status, 4)
	workerStopped := make(chan struct{})
	result := make(chan uint32, 1)
	handler := serviceHandler{ctx: ctx, worker: func(ctx context.Context) error {
		<-ctx.Done()
		close(workerStopped)
		return nil
	}}
	go func() {
		_, code := handler.Execute(nil, requests, states)
		result <- code
	}()
	defer close(requests)
	expect := func(state svc.State) {
		t.Helper()
		select {
		case got := <-states:
			if got.State != state {
				t.Fatalf("service state = %v, want %v", got.State, state)
			}
		case <-ctx.Done():
			t.Fatalf("timed out waiting for service state %v", state)
		}
	}
	expect(svc.StartPending)
	expect(svc.Running)
	requests <- svc.ChangeRequest{Cmd: svc.Interrogate}
	expect(svc.Running)
	requests <- svc.ChangeRequest{Cmd: svc.Stop}
	expect(svc.StopPending)
	select {
	case code := <-result:
		if code != 0 {
			t.Fatalf("service exit code = %d", code)
		}
	case <-ctx.Done():
		t.Fatal("service did not stop")
	}
	select {
	case <-workerStopped:
	default:
		t.Fatal("service returned before its worker stopped")
	}
}

func TestServiceAgentReportsCodexAndTransport(t *testing.T) {
	executable := os.Getenv("AGENTDECK_TEST_CODEX")
	if executable == "" {
		t.Skip("set AGENTDECK_TEST_CODEX to an installed Codex CLI")
	}
	t.Setenv("OPENAI_API_KEY", "")
	t.Setenv("CODEX_API_KEY", "")
	ctx, cancel := context.WithTimeout(t.Context(), 12*time.Second)
	defer cancel()
	directory := t.TempDir()
	configDir := filepath.Join(directory, "config")
	server := httptest.NewUnstartedServer(nil)
	defer server.Close()
	if err := createLab(configDir, "wss://"+server.Listener.Addr().String()); err != nil {
		t.Fatal(err)
	}
	var config relayConfig
	if err := loadConfig(filepath.Join(configDir, "relay.json"), &config); err != nil {
		t.Fatal(err)
	}
	certificate, err := config.Identity.keyPair()
	if err != nil {
		t.Fatal(err)
	}
	server.Config.Handler = transport.NewRelay(ctx, config.Routes)
	server.TLS = &tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS13}
	server.StartTLS()
	logPath := filepath.Join(directory, "service.log")
	project, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	policy := filepath.Join(directory, "projects.json")
	data, err := json.Marshal(map[string][]projects.Entry{
		"projects": {{ID: "alpha", Directory: project, Enabled: true}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(policy, data, 0o600); err != nil {
		t.Fatal(err)
	}
	codex := codexOptions{executable: executable, home: t.TempDir(), policy: policy, projectID: "alpha"}
	taskLogs := t.TempDir()
	inbox := t.TempDir()
	promptFile := filepath.Join(directory, "prompt.txt")
	if err := os.WriteFile(promptFile, []byte("Test submission"), 0o600); err != nil {
		t.Fatal(err)
	}
	journal, err := codexprobe.NewJournal(taskLogs, "alpha", project)
	if err != nil {
		t.Fatal(err)
	}
	err = journal.Finish("completed")
	if closeErr := journal.Close(); err != nil || closeErr != nil {
		t.Fatalf("save fixture log: %v, %v", err, closeErr)
	}
	done := make(chan error, 1)
	go func() {
		done <- runServiceAgent(ctx, filepath.Join(configDir, "agent-a.json"), logPath, "a", codex, taskLogs, inbox)
	}()
	t.Cleanup(func() {
		cancel()
		if err := <-done; err != nil {
			t.Errorf("service worker: %v", err)
		}
	})
	phonePath := filepath.Join(configDir, "phone.json")
	for {
		query := taskLogQuery{ProjectID: "alpha", TaskID: journal.ID}
		err := runPeer(ctx, "remote-cancel", phonePath, "a", log.Default(), peerOptions{query: query})
		if err == nil {
			break
		}
		if ctx.Err() != nil || !strings.Contains(err.Error(), "503") {
			t.Fatal(err)
		}
		time.Sleep(30 * time.Millisecond)
	}
	if requested, err := journal.CancellationRequested(); err != nil || !requested {
		t.Fatalf("cancellation did not reach the Agent: %v %v", requested, err)
	}
	submission := taskSubmission{RequestID: strings.Repeat("a", 32), ProjectID: "alpha"}
	for range 2 {
		for {
			err := runPeer(ctx, "submit", phonePath, "a", log.Default(), peerOptions{submission: submission, promptPath: promptFile})
			if err == nil {
				break
			}
			if ctx.Err() != nil || !strings.Contains(err.Error(), "503") {
				t.Fatal(err)
			}
			time.Sleep(30 * time.Millisecond)
		}
	}
	entries, err := os.ReadDir(inbox)
	if err != nil || len(entries) != 1 || entries[0].Name() != receiptID(submission.RequestID)+".local.json" {
		t.Fatalf("expected one authenticated submission: %v, %v", entries, err)
	}
	for {
		query := taskLogQuery{ProjectID: "alpha"}
		err := runPeer(ctx, "task-receipt", phonePath, "a", log.Default(), peerOptions{query: query, submission: submission})
		if err == nil {
			break
		}
		if ctx.Err() != nil || !strings.Contains(err.Error(), "503") {
			t.Fatal(err)
		}
		time.Sleep(30 * time.Millisecond)
	}
	for {
		query := taskLogQuery{ProjectID: "alpha", TaskID: journal.ID}
		err := runPeer(ctx, "task-log", phonePath, "a", log.Default(), peerOptions{query: query})
		if err == nil {
			break
		}
		if ctx.Err() != nil || !strings.Contains(err.Error(), "503") {
			t.Fatal(err)
		}
		time.Sleep(30 * time.Millisecond)
	}
	for {
		err := runPeer(ctx, "projects", phonePath, "a", log.Default(), peerOptions{})
		if err == nil {
			break
		}
		if ctx.Err() != nil || !strings.Contains(err.Error(), "503") {
			t.Fatal(err)
		}
		time.Sleep(30 * time.Millisecond)
	}
	for {
		err := runPeer(ctx, "probe", phonePath, "a", log.Default(), peerOptions{})
		if err == nil {
			break
		}
		if ctx.Err() != nil || !strings.Contains(err.Error(), "503") {
			t.Fatal(err)
		}
		time.Sleep(30 * time.Millisecond)
	}
	for {
		contents, err := os.ReadFile(logPath)
		if err != nil {
			t.Fatal(err)
		}
		if strings.Contains(string(contents), "answered an authenticated probe") &&
			strings.Contains(string(contents), "Codex stdio initialized; account_configured=false") {
			break
		}
		if ctx.Err() != nil {
			t.Fatal("service log did not record transport and Codex readiness")
		}
		time.Sleep(30 * time.Millisecond)
	}
}
