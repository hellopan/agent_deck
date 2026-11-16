// Package agentfind locates the desktop tools this project drives. Nothing
// here starts a process: it only answers "where is this tool, and can it
// actually do the job", so both the CLI and the tray can fill in a path the
// user would otherwise have to type from memory.
package agentfind

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
)

// Tool names as they appear in configuration and in the detection report.
const (
	Codex     = "codex"
	Cursor    = "cursor"
	WorkBuddy = "workbuddy"
)

// Tool is one detection result. Usable is false when the executable exists but
// cannot serve this project, which is the difference between "not installed"
// and "installed, but the wrong copy".
type Tool struct {
	Name    string `json:"name"`
	Path    string `json:"path,omitempty"`
	Source  string `json:"source,omitempty"`
	Usable  bool   `json:"usable"`
	Problem string `json:"problem,omitempty"`
}

// candidate is one place worth looking, with the name reported to the user.
type candidate struct {
	path   string
	source string
}

// Detect finds one tool. An explicit hint always wins: the user pointed at it,
// so a problem with that path is reported instead of silently searching on.
func Detect(name, hint string) Tool {
	if hint != "" {
		return inspect(name, hint, "config")
	}
	var rejected Tool
	for _, place := range candidates(name) {
		if place.path == "" {
			continue
		}
		if info, err := os.Stat(place.path); err != nil || info.IsDir() {
			continue
		}
		tool := inspect(name, place.path, place.source)
		if tool.Usable {
			return tool
		}
		if rejected.Path == "" {
			rejected = tool
		}
	}
	if rejected.Path != "" {
		return rejected
	}
	return Tool{Name: name, Problem: "没有找到安装位置，请手动指定可执行文件"}
}

// DetectAll reports on every supported tool in a stable order.
func DetectAll(codexHint string) []Tool {
	tools := []Tool{
		Detect(Codex, codexHint),
		Detect(Cursor, ""),
		Detect(WorkBuddy, ""),
	}
	return tools
}

func inspect(name, path, source string) Tool {
	abs, err := filepath.Abs(path)
	if err != nil {
		return Tool{Name: name, Path: path, Source: source, Problem: "路径无法解析"}
	}
	tool := Tool{Name: name, Path: abs, Source: source}
	info, err := os.Stat(abs)
	if err != nil {
		tool.Problem = "文件不存在"
		return tool
	}
	if info.IsDir() {
		tool.Problem = "这是一个目录，不是可执行文件"
		return tool
	}
	if !strings.EqualFold(filepath.Ext(abs), ".exe") {
		tool.Problem = "不是 Windows 可执行文件"
		return tool
	}
	if name == Codex {
		if err := checkCodexBundle(abs); err != nil {
			tool.Problem = err.Error()
			return tool
		}
	}
	tool.Usable = true
	return tool
}

// checkCodexBundle rejects a Codex that exists but cannot run shell tools.
//
// Codex ships two shapes of the same binary: the installed build, which sits
// next to codex-code-mode-host.exe, codex-command-runner.exe and
// codex-windows-sandbox-setup.exe, and the stripped copy under
// %USERPROFILE%\.codex\.sandbox-bin, which has no host binary. Starting a turn
// with the stripped copy fails every shell tool with
//
//	failed to spawn code-mode host ... (os error 2)
//
// and, worse, the turn still completes normally, so no approval request is ever
// raised and the phone waits forever. A path that looks right is therefore not
// good enough.
func checkCodexBundle(executable string) error {
	host := filepath.Join(filepath.Dir(executable), "codex-code-mode-host.exe")
	if info, err := os.Stat(host); err != nil || info.IsDir() {
		return errors.New("同目录缺少 codex-code-mode-host.exe，任务执行时 shell 工具起不来、审批也不会出现；请指向完整安装（%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\<build>\\codex.exe）")
	}
	return nil
}

// humanName is the word used when talking to the user about a tool.
func humanName(name string) string {
	switch name {
	case Codex:
		return "Codex"
	case Cursor:
		return "Cursor"
	case WorkBuddy:
		return "WorkBuddy"
	}
	return name
}

// executableName is the file name each tool installs, used by every source.
func executableName(name string) string {
	switch name {
	case Codex:
		return "codex.exe"
	case Cursor:
		return "Cursor.exe"
	case WorkBuddy:
		return "WorkBuddy.exe"
	}
	return ""
}
