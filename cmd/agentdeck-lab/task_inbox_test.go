package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/hellopan/agent_deck/internal/projects"
)

func TestSubmissionRetryAndReviewClaim(t *testing.T) {
	root := t.TempDir()
	project, err := filepath.EvalSymlinks(root)
	if err != nil {
		t.Fatal(err)
	}
	policy := filepath.Join(root, "projects.json")
	updatePolicy := func(enabled bool) {
		t.Helper()
		data, err := json.Marshal(map[string][]projects.Entry{
			"projects": {{ID: "alpha", Directory: project, Enabled: enabled}},
		})
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(policy, data, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	updatePolicy(true)
	inbox := t.TempDir()
	opts := peerOptions{policy: policy, inbox: inbox}
	task := taskSubmission{RequestID: strings.Repeat("a", 32), ProjectID: "alpha", Prompt: "example"}
	receipt, err := saveSubmission(opts, task)
	if err != nil || receipt.Status != "awaiting_local_review" {
		t.Fatalf("submit: %+v, %v", receipt, err)
	}
	query := receiptQuery{ProjectID: task.ProjectID, TaskID: receipt.TaskID}
	if got, err := loadReceipt(opts, query); err != nil || got != receipt {
		t.Fatalf("query: %+v, %v", got, err)
	}
	if _, err := loadReceipt(opts, receiptQuery{ProjectID: task.ProjectID, TaskID: "../projects"}); err == nil {
		t.Fatal("accepted an invalid task ID")
	}
	if got, err := saveSubmission(opts, task); err != nil || got != receipt {
		t.Fatalf("retry: %+v, %v", got, err)
	}
	other := task
	other.Prompt = "changed"
	if _, err := saveSubmission(opts, other); err == nil {
		t.Fatal("same request ID accepted changed prompt")
	}
	logs := t.TempDir()
	config := codexOptions{policy: policy, projectID: "alpha"}
	updatePolicy(false)
	if _, err := loadReceipt(opts, query); err == nil {
		t.Fatal("revoked project still returned a receipt")
	}
	if err := reviewTask(t.Context(), config, inbox, receipt.TaskID, logs, true, false); err == nil || !strings.Contains(err.Error(), "authorize project") {
		t.Fatalf("review of revoked project returned %v", err)
	}
	updatePolicy(true)
	// With no Codex executable, the first claim records a failed task but
	// cannot run a model. The second claim must be rejected before startup.
	if err := reviewTask(t.Context(), config, inbox, receipt.TaskID, logs, true, false); err == nil || !strings.Contains(err.Error(), "Codex executable") {
		t.Fatalf("first review returned %v", err)
	}
	if err := reviewTask(t.Context(), config, inbox, receipt.TaskID, logs, true, false); err == nil || !strings.Contains(err.Error(), "one-time execution") {
		t.Fatalf("repeat review returned %v", err)
	}
	if got, err := saveSubmission(opts, task); err != nil || got.TaskID != receipt.TaskID || got.Status != "claimed" {
		t.Fatalf("retry after claim: %+v, %v", got, err)
	}
	statusOpts := opts
	statusOpts.logDir = logs
	if got, err := loadReceipt(statusOpts, query); err != nil || got.Status != "failed" {
		t.Fatalf("expected recorded failure: %+v, %v", got, err)
	}
	task.RequestID = strings.Repeat("b", 32)
	receipt, err = saveSubmission(opts, task)
	if err != nil {
		t.Fatal(err)
	}
	items, err := listInbox(inbox)
	if err != nil || len(items) != 1 || items[0].TaskID != receipt.TaskID {
		t.Fatalf("pending inbox: %v %v", items, err)
	}
	if err := reviewTask(t.Context(), config, inbox, receipt.TaskID, "", false, true); err != nil {
		t.Fatal(err)
	}
	if err := reviewTask(t.Context(), config, inbox, receipt.TaskID, logs, true, false); err == nil || !strings.Contains(err.Error(), "one-time execution") {
		t.Fatalf("declined task could be claimed: %v", err)
	}
	if got, err := saveSubmission(opts, task); err != nil || got.Status != "declined" {
		t.Fatalf("retry after denial: %+v, %v", got, err)
	}
	if got, err := loadReceipt(opts, receiptQuery{ProjectID: task.ProjectID, TaskID: receipt.TaskID}); err != nil || got.Status != "declined" {
		t.Fatalf("query after denial: %+v, %v", got, err)
	}
	items, err = listInbox(inbox)
	if err != nil || len(items) != 0 {
		t.Fatalf("reviewed inbox: %v %v", items, err)
	}
}

func TestReviewMarkerHasOneWinner(t *testing.T) {
	root, err := os.OpenRoot(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()
	results := make(chan error, 2)
	for _, status := range []string{"claimed", "declined"} {
		go func() { results <- publishSubmission(root, "task.claimed.log", []byte(status)) }()
	}
	first, second := <-results, <-results
	if first == nil && second == nil || first != nil && second != nil {
		t.Fatalf("expected one winner: %v %v", first, second)
	}
	if got, err := reviewStatus(root, "task"); err != nil || (got != "claimed" && got != "declined") {
		t.Fatalf("invalid final status: %s %v", got, err)
	}
}
