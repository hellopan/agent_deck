package projects

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestResolveRejectsWindowsDirectoryJunction(t *testing.T) {
	directory, err := filepath.EvalSymlinks(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	project := filepath.Join(directory, "alpha")
	outside := filepath.Join(directory, "outside")
	for _, path := range []string{project, outside} {
		if err := os.Mkdir(path, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	policy := filepath.Join(directory, "projects.json")
	writePolicy(t, policy, Entry{ID: "alpha", Directory: project, Enabled: true})
	if _, err := Resolve(policy, "alpha"); err != nil {
		t.Fatal(err)
	}
	if err := os.Rename(project, filepath.Join(directory, "original")); err != nil {
		t.Fatal(err)
	}
	command := exec.CommandContext(t.Context(), "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
		`$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:AGENTDECK_TEST_JUNCTION -Target $env:AGENTDECK_TEST_TARGET | Out-Null`)
	command.Env = append(command.Environ(), "AGENTDECK_TEST_JUNCTION="+project, "AGENTDECK_TEST_TARGET="+outside)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("create test junction: %v: %s", err, output)
	}
	info, err := os.Lstat(project)
	if err != nil || !redirectedDirectory(info) {
		t.Fatalf("test junction was not created as a reparse point: info=%v err=%v", info, err)
	}
	if got, err := Resolve(policy, "alpha"); got != "" || !errors.Is(err, ErrDirectory) {
		t.Fatalf("redirected project returned %q, %v", got, err)
	}
	if err := os.Mkdir(filepath.Join(outside, "child"), 0o700); err != nil {
		t.Fatal(err)
	}
	writePolicy(t, policy, Entry{ID: "alpha", Directory: filepath.Join(project, "child"), Enabled: true})
	if got, err := Resolve(policy, "alpha"); got != "" || !errors.Is(err, ErrDirectory) {
		t.Fatalf("project under a junction returned %q, %v", got, err)
	}
}
