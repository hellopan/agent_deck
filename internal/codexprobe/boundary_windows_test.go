package codexprobe

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

const boundaryProfile = `default_permissions = "agentdeck-project"
approval_policy = "never"

[windows]
sandbox = "mxc"

[permissions.agentdeck-project]
extends = ":workspace"

[permissions.agentdeck-project.filesystem]
":root" = "deny"
":minimal" = "read"
":tmpdir" = "deny"
":slash_tmp" = "deny"

[permissions.agentdeck-project.network]
enabled = false
`

const boundaryOutputPrefix = "AGENTDECK_BOUNDARY:"

type boundaryResult struct {
	InsideRead   bool
	InsideWrite  bool
	OutsideRead  bool
	OutsideWrite bool
}

// Run only on an explicit Windows test host. The fixture contains synthetic files.
func TestWindowsCodexProjectBoundary(t *testing.T) {
	executable := os.Getenv("AGENTDECK_TEST_CODEX")
	scratch := os.Getenv("AGENTDECK_TEST_CODEX_SANDBOX_ROOT")
	if executable == "" || scratch == "" {
		t.Skip("set AGENTDECK_TEST_CODEX and AGENTDECK_TEST_CODEX_SANDBOX_ROOT")
	}
	if !filepath.IsAbs(scratch) {
		t.Fatal("sandbox fixture root must be absolute")
	}
	scratch, err := filepath.EvalSymlinks(scratch)
	if err != nil {
		t.Fatal(err)
	}
	root, err := os.MkdirTemp(scratch, "agentdeck-boundary-")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		resolved, err := filepath.EvalSymlinks(root)
		if err != nil || filepath.Dir(resolved) != scratch || !strings.HasPrefix(filepath.Base(resolved), "agentdeck-boundary-") {
			t.Errorf("refusing to remove unverified test directory: %v", err)
			return
		}
		if err := os.RemoveAll(resolved); err != nil {
			t.Errorf("remove test fixture: %v", err)
		}
	})
	workspace := filepath.Join(root, "workspace")
	outside := filepath.Join(root, "outside")
	home := filepath.Join(root, "codex-home")
	for _, directory := range []string{workspace, outside, home} {
		if err := os.Mkdir(directory, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	for path, content := range map[string]string{
		filepath.Join(home, "config.toml"):   boundaryProfile,
		filepath.Join(workspace, "read.txt"): "agentdeck-fixture",
		filepath.Join(outside, "read.txt"):   "agentdeck-fixture",
	} {
		if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	t.Setenv("CODEX_HOME", home)
	t.Setenv("OPENAI_API_KEY", "")
	t.Setenv("CODEX_API_KEY", "")
	testExecutable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	testBinary, err := os.ReadFile(testExecutable)
	if err != nil {
		t.Fatal(err)
	}
	probe := filepath.Join(workspace, "probe.exe")
	if err := os.WriteFile(probe, testBinary, 0o700); err != nil {
		t.Fatal(err)
	}
	args := []string{"-test.run=^TestWindowsBoundaryFixtureHelper$", "--"}
	baseline := exec.CommandContext(t.Context(), probe, append(args, "baseline")...)
	baseline.Dir = workspace
	output, err := baseline.Output()
	if err != nil {
		t.Fatalf("baseline did not run: %v", err)
	}
	assertBoundary(t, output, boundaryResult{true, true, true, true}, workspace, outside, "baseline")
	want := boundaryResult{InsideRead: true, InsideWrite: true}

	t.Run("CLI", func(t *testing.T) {
		arguments := []string{"sandbox", "--include-managed-config", "--permission-profile", "agentdeck-project", "--cd", workspace, "--", probe}
		command := exec.CommandContext(t.Context(), executable, append(append(arguments, args...), "cli")...)
		command.Dir = workspace
		output, err := command.Output()
		if err != nil {
			var exitErr *exec.ExitError
			if errors.As(err, &exitErr) {
				t.Fatalf("Codex CLI sandbox failed: %v: %s", err, exitErr.Stderr)
			}
			t.Fatal(err)
		}
		assertBoundary(t, output, want, workspace, outside, "cli")
	})
	t.Run("AppServer", func(t *testing.T) {
		command := append(append([]string{probe}, args...), "server")
		result, err := exchange(t.Context(), Config{Executable: executable, ProjectDir: workspace, CodexHome: home}, message{
			Method: "command/exec", ID: 2, Params: map[string]any{
				"command": command, "cwd": workspace, "timeoutMs": 10000,
			},
		})
		if err != nil {
			t.Fatal(err)
		}
		var reply struct {
			ExitCode int    `json:"exitCode"`
			Stdout   string `json:"stdout"`
			Stderr   string `json:"stderr"`
		}
		if err := json.Unmarshal(result, &reply); err != nil {
			t.Fatal(err)
		}
		if reply.ExitCode != 0 {
			t.Fatalf("App Server command exited %d: %s", reply.ExitCode, reply.Stderr)
		}
		assertBoundary(t, []byte(reply.Stdout), want, workspace, outside, "server")
	})
}

func TestWindowsBoundaryFixtureHelper(t *testing.T) {
	if len(os.Args) != 4 || os.Args[2] != "--" {
		t.Skip("fixture process only")
	}
	trial := os.Args[3]
	if trial != "baseline" && trial != "cli" && trial != "server" {
		t.Fatal("invalid fixture trial")
	}
	workspace, err := os.Getwd()
	if err != nil || filepath.Base(workspace) != "workspace" || !strings.HasPrefix(filepath.Base(filepath.Dir(workspace)), "agentdeck-boundary-") {
		t.Fatal("invalid fixture workspace")
	}
	readFixture := func(directory string) bool {
		value, err := os.ReadFile(filepath.Join(directory, "read.txt"))
		return err == nil && string(value) == "agentdeck-fixture"
	}
	writeFixture := func(directory string) bool {
		path := filepath.Join(directory, trial+"-write.txt")
		file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
		if err != nil {
			return false
		}
		_, err = file.WriteString("agentdeck-write")
		return file.Close() == nil && err == nil
	}
	if !readFixture(workspace) {
		t.Fatal("inside fixture is unreadable")
	}
	outside := filepath.Join(filepath.Dir(workspace), "outside")
	result, err := json.Marshal(boundaryResult{
		InsideRead: true, InsideWrite: writeFixture(workspace),
		OutsideRead: readFixture(outside), OutsideWrite: writeFixture(outside),
	})
	if err != nil {
		t.Fatal(err)
	}
	fmt.Printf("%s%s\n", boundaryOutputPrefix, result)
}

func assertBoundary(t *testing.T, output []byte, want boundaryResult, workspace, outside, trial string) {
	t.Helper()
	var got boundaryResult
	var found bool
	for line := range bytes.SplitSeq(output, []byte{'\n'}) {
		value, ok := bytes.CutPrefix(line, []byte(boundaryOutputPrefix))
		if !ok {
			continue
		}
		if err := json.Unmarshal(value, &got); err != nil || found {
			t.Fatalf("invalid probe result: %v: %s", err, output)
		}
		found = true
	}
	if !found {
		t.Fatalf("missing probe result: %s", output)
	}
	if got != want {
		t.Fatalf("file access: got %+v, want %+v", got, want)
	}
	if data, err := os.ReadFile(filepath.Join(workspace, trial+"-write.txt")); err != nil || string(data) != "agentdeck-write" {
		t.Fatalf("workspace write was not observed: %v", err)
	}
	_, err := os.Stat(filepath.Join(outside, trial+"-write.txt"))
	if (want.OutsideWrite && err != nil) || (!want.OutsideWrite && !errors.Is(err, os.ErrNotExist)) {
		t.Fatalf("unexpected outside write status: %v", err)
	}
	t.Logf("inside read=%t write=%t, outside read=%t write=%t", got.InsideRead, got.InsideWrite, got.OutsideRead, got.OutsideWrite)
}
