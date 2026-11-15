package main

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

func TestRunningTaskObservesLocalCancellation(t *testing.T) {
	dir, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	policy := filepath.Join(dir, "projects.json")
	data, err := json.Marshal(map[string][]projects.Entry{"projects": {{ID: "alpha", Directory: dir, Enabled: true}}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(policy, data, 0o600); err != nil {
		t.Fatal(err)
	}
	journal, err := codexprobe.NewJournal(dir, "alpha", dir)
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	if err := codexprobe.RequestCancellation(dir, "../projects"); err == nil {
		t.Fatal("accepted invalid task ID")
	}
	ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
	defer cancel()
	status, err := runWhileAuthorized(ctx, codexOptions{policy: policy, projectID: "alpha"}, dir,
		func(runCtx context.Context, _ func() error) (string, error) {
			for range 2 {
				if err := codexprobe.RequestCancellation(dir, journal.ID); err != nil {
					return "", err
				}
			}
			<-runCtx.Done()
			return "", runCtx.Err()
		}, journal.CancellationRequested)
	if status != "interrupted" || !errors.Is(err, errTaskCanceled) {
		t.Fatalf("status=%q error=%v", status, err)
	}
}

func TestRunningTaskStopsOnProjectRevocation(t *testing.T) {
	for _, change := range []string{"disable", "move", "remove"} {
		t.Run(change, func(t *testing.T) {
			dir, err := filepath.EvalSymlinks(t.TempDir())
			if err != nil {
				t.Fatal(err)
			}
			policy := filepath.Join(dir, "projects.json")
			entry := projects.Entry{ID: "alpha", Directory: dir, Enabled: true}
			writePolicy := func(entries ...projects.Entry) {
				t.Helper()
				data, err := json.Marshal(map[string][]projects.Entry{"projects": entries})
				if err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(policy, data, 0o600); err != nil {
					t.Fatal(err)
				}
			}
			writePolicy(entry)
			ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
			defer cancel()
			status, err := runWhileAuthorized(ctx, codexOptions{policy: policy, projectID: "alpha"}, dir,
				func(runCtx context.Context, check func() error) (string, error) {
					switch change {
					case "disable":
						entry.Enabled = false
						writePolicy(entry)
					case "move":
						entry.Directory = t.TempDir()
						writePolicy(entry)
					case "remove":
						writePolicy()
					}
					<-runCtx.Done()
					return "", check()
				}, nil)
			if status != "interrupted" || !errors.Is(err, errProjectRevoked) {
				t.Fatalf("status=%q error=%v", status, err)
			}
		})
	}
}

func TestRevocationStopsNextOutputWithoutPolling(t *testing.T) {
	dir, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	policy := filepath.Join(dir, "projects.json")
	data, err := json.Marshal(map[string][]projects.Entry{"projects": {{ID: "alpha", Directory: dir, Enabled: true}}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(policy, data, 0o600); err != nil {
		t.Fatal(err)
	}
	status, err := runWhileAuthorized(t.Context(), codexOptions{policy: policy, projectID: "alpha"}, dir,
		func(_ context.Context, check func() error) (string, error) {
			if err := os.WriteFile(policy, []byte(`{"projects":[]}`), 0o600); err != nil {
				t.Fatal(err)
			}
			return "completed", check()
		}, nil)
	if status != "interrupted" || !errors.Is(err, errProjectRevoked) {
		t.Fatalf("status=%q error=%v", status, err)
	}
}
