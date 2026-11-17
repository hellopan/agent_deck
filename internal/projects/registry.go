// Package projects resolves workspaces from the local Agent authorization list.
package projects

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
)

var (
	ErrNotAuthorized = errors.New("project is not authorized")
	ErrDirectory     = errors.New("authorized project directory is unavailable or redirected")
	validID          = regexp.MustCompile(`^[a-z0-9][a-z0-9_-]{0,63}$`)
)

type Entry struct {
	ID        string `json:"id"`
	Directory string `json:"directory"`
	Enabled   bool   `json:"enabled"`
	// Mode is the Codex sandbox this project runs under. An empty value means
	// read-only, so every list written before modes existed keeps the strict
	// setting instead of silently granting writes.
	Mode string `json:"mode,omitempty"`
}

// Mode is the sandbox a task runs under. Only Codex's two local scopes are
// accepted: danger-full-access would let a phone request escape the project
// directory, which is the one thing the authorization list is for.
type Mode string

const (
	ModeReadOnly       Mode = "read-only"
	ModeWorkspaceWrite Mode = "workspace-write"
)

func parseMode(raw string) (Mode, error) {
	switch raw {
	case "":
		return ModeReadOnly, nil
	case string(ModeReadOnly):
		return ModeReadOnly, nil
	case string(ModeWorkspaceWrite):
		return ModeWorkspaceWrite, nil
	default:
		return "", fmt.Errorf("project policy contains an unsupported mode %q", raw)
	}
}

// List returns the IDs of enabled projects with usable local directories.
// The result is safe to show to a paired client because it contains no paths.
func List(policyPath string) ([]string, error) {
	entries, err := readPolicy(policyPath)
	if err != nil {
		return nil, err
	}
	ids := make([]string, 0, len(entries))
	for id, entry := range entries {
		if _, _, err := resolveEntry(entry); err == nil {
			ids = append(ids, id)
		}
	}
	sort.Strings(ids)
	return ids, nil
}

// Resolve re-reads the local policy on each use and returns only enabled projects.
// Its result is a launch directory. Child process access needs a separate sandbox.
func Resolve(policyPath, projectID string) (string, error) {
	directory, _, err := ResolveMode(policyPath, projectID)
	return directory, err
}

// ResolveMode is Resolve plus the sandbox the project is allowed to run under.
// Callers that start a turn must use it: the directory alone says nothing about
// whether the task may write.
func ResolveMode(policyPath, projectID string) (string, Mode, error) {
	if !validID.MatchString(projectID) {
		return "", "", ErrNotAuthorized
	}
	entries, err := readPolicy(policyPath)
	if err != nil {
		return "", "", err
	}
	return resolveEntry(entries[projectID])
}

func resolveEntry(entry Entry) (string, Mode, error) {
	if !entry.Enabled {
		return "", "", ErrNotAuthorized
	}
	mode, err := parseMode(entry.Mode)
	if err != nil {
		return "", "", err
	}
	if !filepath.IsAbs(entry.Directory) {
		return "", "", ErrDirectory
	}
	path := filepath.Clean(entry.Directory)
	if !directDirectory(path) {
		return "", "", ErrDirectory
	}
	return path, mode, nil
}

func directDirectory(path string) bool {
	var components []string
	for current := path; ; current = filepath.Dir(current) {
		components = append(components, current)
		if filepath.Dir(current) == current {
			break
		}
	}
	for index := len(components) - 1; index >= 0; index-- {
		info, err := os.Lstat(components[index])
		if err != nil || !info.IsDir() || redirectedDirectory(info) {
			return false
		}
	}
	return true
}

func readPolicy(path string) (map[string]Entry, error) {
	const maxPolicySize = 64 * 1024
	if !filepath.IsAbs(path) {
		return nil, errors.New("project policy requires an absolute local file path")
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("open project policy: %w", err)
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, fmt.Errorf("inspect project policy: %w", err)
	}
	if !info.Mode().IsRegular() || info.Size() > maxPolicySize {
		return nil, errors.New("expected a regular project policy file at most 64 KiB")
	}
	var policy struct {
		Projects []Entry `json:"projects"`
	}
	decoder := json.NewDecoder(io.LimitReader(file, maxPolicySize+1))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&policy); err != nil {
		return nil, fmt.Errorf("decode project policy: %w", err)
	}
	if err := decoder.Decode(new(any)); err != io.EOF || decoder.InputOffset() > maxPolicySize {
		return nil, errors.New("invalid data after project policy")
	}
	entries := make(map[string]Entry, len(policy.Projects))
	for _, entry := range policy.Projects {
		if !validID.MatchString(entry.ID) {
			return nil, errors.New("project policy contains an invalid project ID")
		}
		if _, exists := entries[entry.ID]; exists {
			return nil, fmt.Errorf("project policy contains duplicate ID %q", entry.ID)
		}
		if _, err := parseMode(entry.Mode); err != nil {
			return nil, err
		}
		entries[entry.ID] = entry
	}
	return entries, nil
}
