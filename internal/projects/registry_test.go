package projects

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

func TestResolveRechecksLocalAuthorization(t *testing.T) {
	directory := t.TempDir()
	policy := filepath.Join(directory, "projects.json")
	project, err := filepath.EvalSymlinks(directory)
	if err != nil {
		t.Fatal(err)
	}
	entry := Entry{ID: "alpha", Directory: project, Enabled: true}
	writePolicy(t, policy, entry)
	if ids, err := List(policy); err != nil || len(ids) != 1 || ids[0] != "alpha" {
		t.Fatalf("authorized project list returned %v, %v", ids, err)
	}
	if got, err := Resolve(policy, "alpha"); err != nil || got != project {
		t.Fatalf("authorized project returned %q, %v", got, err)
	}
	for _, id := range []string{"missing", "../alpha", project, "ALPHA", ""} {
		if got, err := Resolve(policy, id); got != "" || !errors.Is(err, ErrNotAuthorized) {
			t.Errorf("invalid selection %q returned %q, %v", id, got, err)
		}
	}
	entry.Enabled = false
	writePolicy(t, policy, entry)
	if ids, err := List(policy); err != nil || ids == nil || len(ids) != 0 {
		t.Fatalf("disabled project list returned %v, %v", ids, err)
	}
	if _, err := Resolve(policy, "alpha"); !errors.Is(err, ErrNotAuthorized) {
		t.Fatalf("disabled project returned %v", err)
	}
	writePolicy(t, policy)
	if _, err := Resolve(policy, "alpha"); !errors.Is(err, ErrNotAuthorized) {
		t.Fatalf("removed project returned %v", err)
	}
}

func TestResolveRejectsAmbiguousPolicyAndMissingDirectory(t *testing.T) {
	directory := t.TempDir()
	policy := filepath.Join(directory, "projects.json")
	entry := Entry{ID: "alpha", Directory: filepath.Join(directory, "missing"), Enabled: true}
	writePolicy(t, policy, entry, entry)
	if got, err := Resolve(policy, "alpha"); got != "" || err == nil {
		t.Fatalf("duplicate project returned %q, %v", got, err)
	}
	writePolicy(t, policy, entry)
	if _, err := Resolve(policy, "alpha"); !errors.Is(err, ErrDirectory) {
		t.Fatalf("missing project directory returned %v", err)
	}
}

func TestModeDefaultsToReadOnlyAndRejectsUnknownScopes(t *testing.T) {
	directory := t.TempDir()
	policy := filepath.Join(directory, "projects.json")
	project, err := filepath.EvalSymlinks(directory)
	if err != nil {
		t.Fatal(err)
	}
	// A list written before modes existed has no mode field at all; it must keep
	// resolving read-only instead of failing to load.
	writePolicy(t, policy, Entry{ID: "alpha", Directory: project, Enabled: true})
	if _, mode, err := ResolveMode(policy, "alpha"); err != nil || mode != ModeReadOnly {
		t.Fatalf("legacy entry resolved %q, %v", mode, err)
	}
	writePolicy(t, policy, Entry{ID: "alpha", Directory: project, Enabled: true, Mode: "workspace-write"})
	if _, mode, err := ResolveMode(policy, "alpha"); err != nil || mode != ModeWorkspaceWrite {
		t.Fatalf("writable entry resolved %q, %v", mode, err)
	}
	// danger-full-access would let a phone request leave the project directory.
	for _, bad := range []string{"danger-full-access", "write", "READ-ONLY"} {
		writePolicy(t, policy, Entry{ID: "alpha", Directory: project, Enabled: true, Mode: bad})
		if got, err := Resolve(policy, "alpha"); got != "" || err == nil {
			t.Fatalf("mode %q resolved %q, %v", bad, got, err)
		}
	}
}

func writePolicy(t *testing.T, path string, entries ...Entry) {
	t.Helper()
	data, err := json.Marshal(struct {
		Projects []Entry `json:"projects"`
	}{Projects: entries})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatal(err)
	}
}
