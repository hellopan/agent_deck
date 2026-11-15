package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

func TestTaskLogCursorAndAuthorization(t *testing.T) {
	root := t.TempDir()
	project, err := filepath.EvalSymlinks(root)
	if err != nil {
		t.Fatal(err)
	}
	policy := filepath.Join(root, "projects.json")
	setPolicy := func(enabled bool) {
		t.Helper()
		data, err := json.Marshal(map[string][]projects.Entry{"projects": {
			{ID: "alpha", Directory: project, Enabled: enabled},
			{ID: "beta", Directory: project, Enabled: true},
		}})
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(policy, data, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	setPolicy(true)
	journal, err := codexprobe.NewJournal(root, "alpha", project)
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	for range 65 {
		if err := journal.Append(codexprobe.TaskEvent{Type: "delta", Text: "sample"}); err != nil {
			t.Fatal(err)
		}
	}
	if err := journal.Finish("completed"); err != nil {
		t.Fatal(err)
	}
	request := func(id, task string, after int) *codexprobe.TaskPage {
		t.Helper()
		raw, err := json.Marshal(taskLogQuery{ProjectID: id, TaskID: task, After: after})
		if err != nil {
			t.Fatal(err)
		}
		var wire bytes.Buffer
		serveTaskLog(&wire, peerOptions{policy: policy, logDir: root}, string(raw))
		var reply taskLogReply
		if err := json.NewDecoder(&wire).Decode(&reply); err != nil {
			t.Fatal(err)
		}
		return reply.Page
	}
	cancel := func(id, task string) bool {
		t.Helper()
		raw, err := json.Marshal(taskLogQuery{ProjectID: id, TaskID: task})
		if err != nil {
			t.Fatal(err)
		}
		var wire bytes.Buffer
		serveRemoteCancel(&wire, peerOptions{policy: policy, logDir: root}, string(raw))
		var reply cancelReply
		if err := json.NewDecoder(&wire).Decode(&reply); err != nil {
			t.Fatal(err)
		}
		return reply.Requested
	}
	if cancel("beta", journal.ID) || cancel("alpha", "../projects") {
		t.Fatal("accepted cancellation with wrong project or task ID")
	}
	if got, err := journal.CancellationRequested(); err != nil || got {
		t.Fatalf("invalid request created a marker: %v %v", got, err)
	}
	if !cancel("alpha", journal.ID) || !cancel("alpha", journal.ID) {
		t.Fatal("valid cancellation was not idempotent")
	}
	first := request("alpha", journal.ID, 0)
	if first == nil || len(first.Events) != 64 || first.Next != 64 {
		t.Fatalf("invalid first page: %+v", first)
	}
	last := request("alpha", journal.ID, first.Next)
	if last == nil || len(last.Events) != 2 || last.Next != 66 || last.Events[1].Status != "completed" {
		t.Fatalf("invalid last page: %+v", last)
	}
	if request("beta", journal.ID, 0) != nil || request("alpha", "../projects", 0) != nil || request("alpha", journal.ID, 67) != nil {
		t.Fatal("accepted wrong project, path, or out-of-range cursor")
	}
	empty := request("alpha", journal.ID, last.Next)
	if empty == nil || empty.Events == nil || len(empty.Events) != 0 || empty.Next != last.Next {
		t.Fatalf("invalid end-of-log response: %+v", empty)
	}
	setPolicy(false)
	if cancel("alpha", journal.ID) {
		t.Fatal("disabled project still accepted cancellation")
	}
	if request("alpha", journal.ID, 0) != nil {
		t.Fatal("disabled project still returned logs")
	}
}
