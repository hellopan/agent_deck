package main

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
	"golang.org/x/sys/windows"
)

// TestMain also lets the test binary act as a controllable stdio server.
func TestMain(m *testing.M) {
	if len(os.Args) == 4 && os.Args[1] == "app-server" &&
		os.Args[2] == "--listen" && os.Args[3] == "stdio://" &&
		os.Getenv("AGENTDECK_CANCEL_TEST_SERVER") == "1" {
		os.Exit(runCancellationFixture())
	}
	os.Exit(m.Run())
}

func runCancellationFixture() int {
	reader := json.NewDecoder(os.Stdin)
	writer := json.NewEncoder(os.Stdout)
	replies := []struct {
		method string
		result any
	}{
		{"initialize", map[string]any{}},
		{"initialized", nil},
		{"thread/start", map[string]any{"thread": map[string]string{"id": "test-thread"}}},
		{"turn/start", map[string]any{"turn": map[string]string{"id": "test-turn"}}},
	}
	for _, expect := range replies {
		var request struct {
			ID     int
			Method string
		}
		if err := reader.Decode(&request); err != nil || request.Method != expect.method {
			return 1
		}
		if expect.result != nil {
			if err := writer.Encode(map[string]any{"id": request.ID, "result": expect.result}); err != nil {
				return 1
			}
		}
	}
	if err := os.WriteFile("fixture.pid", []byte(strconv.Itoa(os.Getpid())), 0o600); err != nil {
		return 1
	}
	// Ignore stdin closure so the test exercises process cancellation.
	time.Sleep(30 * time.Second)
	return 1
}

func TestLocalCancellationStopsAppServerAndFinishesJournal(t *testing.T) {
	t.Setenv("AGENTDECK_CANCEL_TEST_SERVER", "1")
	project, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	logs := t.TempDir()
	config := codexOptions{projectID: "alpha", policy: filepath.Join(project, "projects.json")}
	data, err := json.Marshal(map[string][]projects.Entry{
		"projects": {{ID: config.projectID, Directory: project, Enabled: true}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(config.policy, data, 0o600); err != nil {
		t.Fatal(err)
	}
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	resolved := codexprobe.Config{Executable: executable, ProjectDir: project, CodexHome: t.TempDir()}
	const id = "abcdef0123456789abcdef0123456789"
	ctx, cancel := context.WithTimeout(t.Context(), 8*time.Second)
	done := make(chan error, 1)
	go func() { done <- runResolvedTask(ctx, resolved, config, "test", logs, id) }()
	defer func() {
		cancel()
		if done != nil {
			<-done
		}
	}()
	var pid uint64
	for pid == 0 {
		if content, err := os.ReadFile(filepath.Join(project, "fixture.pid")); err == nil {
			pid, _ = strconv.ParseUint(string(content), 10, 32)
		}
		select {
		case <-ctx.Done():
			t.Fatal("fixture did not start")
		case <-time.After(20 * time.Millisecond):
		}
	}
	process, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		t.Fatal(err)
	}
	defer windows.CloseHandle(process)
	if err := codexprobe.RequestCancellation(logs, id); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-done:
		done = nil
		if !errors.Is(err, errTaskCanceled) {
			t.Fatalf("unexpected task result: %v", err)
		}
	case <-ctx.Done():
		t.Fatal("fixture did not stop after cancellation")
	}
	if state, err := windows.WaitForSingleObject(process, 0); err != nil || state != windows.WAIT_OBJECT_0 {
		t.Fatalf("fixture process still running: state=%d err=%v", state, err)
	}
	page, err := codexprobe.ReadJournal(logs, id, config.projectID, project, 0)
	if err != nil || len(page.Events) == 0 {
		t.Fatalf("missing journal: %+v %v", page, err)
	}
	last := page.Events[len(page.Events)-1]
	if last.Type != "task/finished" || last.Status != "interrupted" {
		t.Fatalf("unexpected final journal event: %+v", last)
	}
}
