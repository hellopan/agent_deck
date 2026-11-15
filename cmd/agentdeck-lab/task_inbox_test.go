package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

func queueFixture(t *testing.T) (peerOptions, string) {
	t.Helper()
	root := t.TempDir()
	project, err := filepath.EvalSymlinks(root)
	if err != nil {
		t.Fatal(err)
	}
	policy := filepath.Join(root, "projects.json")
	data, err := json.Marshal(map[string][]projects.Entry{
		"projects": {{ID: "alpha", Directory: project, Enabled: true}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(policy, data, 0o600); err != nil {
		t.Fatal(err)
	}
	return peerOptions{policy: policy, inbox: t.TempDir(), logDir: t.TempDir()}, project
}

func TestSubmissionIsQueuedOnceAndRunsWithoutLocalReview(t *testing.T) {
	opts, project := queueFixture(t)
	task := taskSubmission{RequestID: strings.Repeat("a", 32), ProjectID: "alpha", Prompt: "补单元测试\n第二行"}
	receipt, err := saveSubmission(opts, "phone-fingerprint", task)
	if err != nil || receipt.Status != statusQueued {
		t.Fatalf("submit: %+v, %v", receipt, err)
	}
	if got, err := saveSubmission(opts, "phone-fingerprint", task); err != nil || got != receipt {
		t.Fatalf("retry: %+v, %v", got, err)
	}
	changed := task
	changed.Prompt = "changed"
	if _, err := saveSubmission(opts, "phone-fingerprint", changed); err == nil {
		t.Fatal("same request ID accepted a changed prompt")
	}
	items, err := listQueue(opts)
	if err != nil || len(items) != 1 || items[0].TaskID != receipt.TaskID || items[0].Status != statusQueued ||
		items[0].Summary != "补单元测试" || items[0].Prompt != task.Prompt {
		t.Fatalf("queue: %+v %v", items, err)
	}
	if got, err := loadReceipt(opts, receiptQuery{ProjectID: "alpha", TaskID: receipt.TaskID}); err != nil || got.Status != statusQueued {
		t.Fatalf("receipt: %+v %v", got, err)
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()
	record, err := readInboxRecord(root, receipt.TaskID)
	if err != nil || record.PeerID != "phone-fingerprint" || record.Directory != project {
		t.Fatalf("record: %+v %v", record, err)
	}

	next, err := nextQueuedTask(opts)
	if err != nil || next == nil || next.TaskID != receipt.TaskID || next.Record.Prompt != task.Prompt {
		t.Fatalf("next: %+v %v", next, err)
	}
	if err := writeMarker(root, receipt.TaskID+".claimed.log"); err != nil {
		t.Fatal(err)
	}
	if next, err := nextQueuedTask(opts); err != nil || next != nil {
		t.Fatalf("claimed request was offered again: %+v %v", next, err)
	}
	if got, err := taskState(root, opts.logDir, "alpha", project, receipt.TaskID); err != nil || got != statusRunning {
		t.Fatalf("claimed state: %s %v", got, err)
	}

	journal, err := codexprobe.OpenNewJournal(opts.logDir, "alpha", project, receipt.TaskID)
	if err != nil {
		t.Fatal(err)
	}
	if err := journal.Finish("completed"); err != nil {
		t.Fatal(err)
	}
	if err := journal.Close(); err != nil {
		t.Fatal(err)
	}
	if got, err := taskState(root, opts.logDir, "alpha", project, receipt.TaskID); err != nil || got != "completed" {
		t.Fatalf("finished state: %s %v", got, err)
	}
	if got, err := loadReceipt(opts, receiptQuery{ProjectID: "alpha", TaskID: receipt.TaskID}); err != nil || got.Status != "completed" {
		t.Fatalf("finished receipt: %+v %v", got, err)
	}
}

func TestQueueSkipsRevokedProjectsAndCanceledRequests(t *testing.T) {
	opts, _ := queueFixture(t)
	first, err := saveSubmission(opts, "", taskSubmission{RequestID: strings.Repeat("b", 32), ProjectID: "alpha", Prompt: "first"})
	if err != nil {
		t.Fatal(err)
	}
	second, err := saveSubmission(opts, "", taskSubmission{RequestID: strings.Repeat("c", 32), ProjectID: "alpha", Prompt: "second"})
	if err != nil {
		t.Fatal(err)
	}
	// Make the second request the older one so ordering is observable.
	old := time.Now().Add(-time.Hour)
	if err := os.Chtimes(filepath.Join(opts.inbox, second.TaskID+".local.json"), old, old); err != nil {
		t.Fatal(err)
	}
	next, err := nextQueuedTask(opts)
	if err != nil || next == nil || next.TaskID != second.TaskID {
		t.Fatalf("expected the oldest request first: %+v %v", next, err)
	}
	if err := requestTaskCancel(opts, taskLogQuery{ProjectID: "beta", TaskID: second.TaskID}); err == nil {
		t.Fatal("canceled a task through the wrong project")
	}
	if err := requestTaskCancel(opts, taskLogQuery{ProjectID: "alpha", TaskID: second.TaskID}); err != nil {
		t.Fatal(err)
	}
	if err := requestTaskCancel(opts, taskLogQuery{ProjectID: "alpha", TaskID: second.TaskID}); err != nil {
		t.Fatalf("cancellation was not idempotent: %v", err)
	}
	next, err = nextQueuedTask(opts)
	if err != nil || next == nil || next.TaskID != first.TaskID {
		t.Fatalf("canceled request was offered: %+v %v", next, err)
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()
	if got, err := taskState(root, opts.logDir, "alpha", "", second.TaskID); err != nil || got != statusCanceled {
		t.Fatalf("canceled state: %s %v", got, err)
	}
	data, err := json.Marshal(map[string][]projects.Entry{"projects": nil})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(opts.policy, data, 0o600); err != nil {
		t.Fatal(err)
	}
	if items, err := listQueue(opts); err != nil || len(items) != 0 {
		t.Fatalf("revoked project stayed in the queue: %+v %v", items, err)
	}
	if next, err := nextQueuedTask(opts); err != nil || next != nil {
		t.Fatalf("revoked project was queued: %+v %v", next, err)
	}
}

func TestClaimWithoutJournalBecomesFailed(t *testing.T) {
	opts, project := queueFixture(t)
	receipt, err := saveSubmission(opts, "", taskSubmission{RequestID: strings.Repeat("d", 32), ProjectID: "alpha", Prompt: "stale"})
	if err != nil {
		t.Fatal(err)
	}
	marker := filepath.Join(opts.inbox, receipt.TaskID+".claimed.log")
	if err := os.WriteFile(marker, []byte(time.Now().UTC().Add(-10*time.Minute).Format(time.RFC3339)), 0o600); err != nil {
		t.Fatal(err)
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()
	if got, err := taskState(root, opts.logDir, "alpha", project, receipt.TaskID); err != nil || got != "failed" {
		t.Fatalf("stale claim state: %s %v", got, err)
	}
}

func TestSubmissionMarkerHasOneWinner(t *testing.T) {
	root, err := os.OpenRoot(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()
	results := make(chan error, 2)
	for range 2 {
		go func() {
			results <- publishSubmission(root, "task.claimed.log", []byte(time.Now().UTC().Format(time.RFC3339)))
		}()
	}
	first, second := <-results, <-results
	if (first == nil) == (second == nil) {
		t.Fatalf("expected one winner: %v %v", first, second)
	}
	if _, claimed, err := readMarker(root, "task.claimed.log"); err != nil || !claimed {
		t.Fatalf("missing marker: %v %v", claimed, err)
	}
}

func TestContinuationRequiresAKnownThread(t *testing.T) {
	opts, _ := queueFixture(t)
	const thread = "01a1e02e-b9a6-74c3-9462-b22f77b5cee0"
	first, err := saveSubmission(opts, "phone", taskSubmission{
		RequestID: strings.Repeat("d", 32), ProjectID: "alpha", Prompt: "把测试跑一遍",
	})
	if err != nil || first.Status != statusQueued {
		t.Fatalf("first turn: %+v %v", first, err)
	}
	follow := taskSubmission{
		RequestID: strings.Repeat("e", 32), ProjectID: "alpha", Prompt: "失败的那两个再看一下", ThreadID: thread,
	}
	if _, err := saveSubmission(opts, "phone", follow); err == nil {
		t.Fatal("accepted a conversation the Agent never started")
	}

	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		t.Fatal(err)
	}
	defer root.Close()
	if err := writeThreadMarker(root, first.TaskID, "alpha", thread); err != nil {
		t.Fatal(err)
	}
	second, err := saveSubmission(opts, "phone", follow)
	if err != nil || second.Status != statusQueued {
		t.Fatalf("follow-up: %+v %v", second, err)
	}

	items, err := listQueue(opts)
	if err != nil {
		t.Fatal(err)
	}
	for _, item := range items {
		if item.ThreadID != thread {
			t.Fatalf("item %s lost its conversation: %+v", item.TaskID, item)
		}
	}
	if len(items) != 2 {
		t.Fatalf("queue: %+v", items)
	}

	// A marker from another project must not authorize the same thread here.
	if err := writeThreadMarker(root, strings.Repeat("f", 32), "beta", thread); err != nil {
		t.Fatal(err)
	}
	other := follow
	other.RequestID = strings.Repeat("a", 31) + "1"
	other.ThreadID = "01a1e031-bf6d-7952-8a66-b90e94af20aa"
	if _, err := saveSubmission(opts, "phone", other); err == nil {
		t.Fatal("accepted a conversation that belongs to no stored turn")
	}
}
