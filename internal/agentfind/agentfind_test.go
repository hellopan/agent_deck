package agentfind

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func write(t *testing.T, path string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("stub"), 0o600); err != nil {
		t.Fatal(err)
	}
}

func TestCodexNeedsTheHostBinary(t *testing.T) {
	dir := t.TempDir()
	stripped := filepath.Join(dir, "codex.exe")
	write(t, stripped)

	tool := inspect(Codex, stripped, "folder")
	if tool.Usable {
		t.Fatal("accepted a Codex without codex-code-mode-host.exe")
	}
	if !strings.Contains(tool.Problem, "code-mode-host") {
		t.Fatalf("the problem must name the missing binary: %q", tool.Problem)
	}

	write(t, filepath.Join(dir, "codex-code-mode-host.exe"))
	if tool = inspect(Codex, stripped, "folder"); !tool.Usable || tool.Problem != "" {
		t.Fatalf("a complete bundle must pass: %+v", tool)
	}
}

func TestOtherToolsNeedOnlyAnExecutable(t *testing.T) {
	dir := t.TempDir()
	exe := filepath.Join(dir, "Cursor.exe")
	write(t, exe)
	if tool := inspect(Cursor, exe, "folder"); !tool.Usable {
		t.Fatalf("Cursor should not be held to the Codex bundle rule: %+v", tool)
	}
	if tool := inspect(Cursor, filepath.Join(dir, "missing.exe"), "folder"); tool.Usable || tool.Problem == "" {
		t.Fatalf("a missing path must be reported: %+v", tool)
	}
}

func TestHintWinsOverSearch(t *testing.T) {
	tool := Detect(Codex, filepath.Join(t.TempDir(), "nowhere.exe"))
	if tool.Usable || tool.Source != "config" || tool.Problem != "文件不存在" {
		t.Fatalf("an explicit path must be reported as given: %+v", tool)
	}
}

func TestShortcutTargetReadsBothEncodings(t *testing.T) {
	ascii := []byte("junk\x00C:\\Tools\\Cursor\\Cursor.exe\x00more")
	if got := shortcutTarget(ascii); got != `c:\tools\cursor\cursor.exe` {
		t.Fatalf("ascii target: %q", got)
	}

	target := `\\?\D:\00-Softwares\WorkBuddy\WorkBuddy.exe`
	wide := make([]byte, 0, len(target)*2)
	for _, r := range target {
		wide = append(wide, byte(r), byte(r>>8))
	}
	if got := shortcutTarget(append([]byte("head"), wide...)); got != `d:\00-softwares\workbuddy\workbuddy.exe` {
		t.Fatalf("utf-16 target: %q", got)
	}

	if got := shortcutTarget(nil); got != "" {
		t.Fatalf("empty shortcut: %q", got)
	}
}

func TestFindUnderLooksOneLevelDown(t *testing.T) {
	base := t.TempDir()
	write(t, filepath.Join(base, "1.2.3", "Codex.exe"))
	if got := findUnder(base, "Codex.exe"); got != filepath.Join(base, "1.2.3", "Codex.exe") {
		t.Fatalf("nested install: %q", got)
	}
	if got := findUnder(base, "Missing.exe"); got != "" {
		t.Fatalf("missing exe: %q", got)
	}
}
