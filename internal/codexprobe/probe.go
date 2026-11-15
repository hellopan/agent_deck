// Package codexprobe provides local Codex App Server diagnostics.
package codexprobe

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"time"
)

type Config struct {
	Executable string
	ProjectDir string
	CodexHome  string
}

type Status struct {
	AccountConfigured  bool
	OpenAIAuthRequired bool
}

type message struct {
	Method string `json:"method"`
	ID     int    `json:"id,omitempty"`
	Params any    `json:"params,omitempty"`
}

type response struct {
	ID     json.RawMessage `json:"id"`
	Method string          `json:"method"`
	Result json.RawMessage `json:"result"`
	Error  *struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
}

// Check launches the configured executable locally and reads account state without
// refreshing credentials or submitting a task.
func Check(ctx context.Context, config Config) (Status, error) {
	result, err := exchange(ctx, config, message{
		Method: "account/read", ID: 2, Params: map[string]bool{"refreshToken": false},
	})
	if err != nil {
		return Status{}, err
	}
	var account struct {
		Value              json.RawMessage `json:"account"`
		RequiresOpenAIAuth *bool           `json:"requiresOpenaiAuth"`
	}
	if err := json.Unmarshal(result, &account); err != nil || account.RequiresOpenAIAuth == nil || len(account.Value) == 0 {
		return Status{}, errors.New("Codex returned an invalid account state")
	}
	return Status{AccountConfigured: string(account.Value) != "null", OpenAIAuthRequired: *account.RequiresOpenAIAuth}, nil
}

func exchange(ctx context.Context, config Config, request message) (json.RawMessage, error) {
	ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	var result json.RawMessage
	err := withAppServer(ctx, config, func(writer *json.Encoder, reader *bufio.Scanner) error {
		if err := writer.Encode(request); err != nil {
			return fmt.Errorf("send %s: %w", request.Method, err)
		}
		var err error
		result, err = readResponse(ctx, reader, request)
		return err
	})
	return result, err
}

func withAppServer(ctx context.Context, config Config, use func(*json.Encoder, *bufio.Scanner) error) error {
	if err := validatePaths(config); err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	cmd := exec.CommandContext(ctx, config.Executable, "app-server", "--listen", "stdio://")
	cmd.Dir = config.ProjectDir
	cmd.Env = append(cmd.Environ(), "CODEX_HOME="+config.CodexHome)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	defer stdin.Close()
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	defer stdout.Close()
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start Codex App Server: %w", err)
	}
	defer func() {
		stdin.Close()
		stopGracePeriod := time.AfterFunc(2*time.Second, cancel)
		cmd.Wait()
		stopGracePeriod.Stop()
	}()
	stopClose := context.AfterFunc(ctx, func() {
		stdout.Close()
		stdin.Close()
	})
	defer stopClose()

	writer := json.NewEncoder(stdin)
	reader := bufio.NewScanner(stdout)
	reader.Buffer(make([]byte, 4096), 1024*1024)
	requests := []message{
		{Method: "initialize", ID: 1, Params: map[string]any{
			"clientInfo": map[string]string{"name": "agentdeck_lab", "title": "AgentDeck Lab", "version": "0.1.0"},
		}},
		{Method: "initialized"},
	}
	for _, req := range requests {
		if err := writer.Encode(req); err != nil {
			return fmt.Errorf("send %s: %w", req.Method, err)
		}
		if req.ID != 0 {
			if _, err := readResponse(ctx, reader, req); err != nil {
				return err
			}
		}
	}
	return use(writer, reader)
}

func readResponse(ctx context.Context, reader *bufio.Scanner, request message) (json.RawMessage, error) {
	for reader.Scan() {
		var reply response
		if err := json.Unmarshal(reader.Bytes(), &reply); err != nil {
			return nil, fmt.Errorf("invalid App Server message while awaiting %s", request.Method)
		}
		if reply.Method != "" && len(reply.ID) > 0 {
			return nil, fmt.Errorf("unexpected Codex request while awaiting %s", request.Method)
		}
		if string(reply.ID) != strconv.Itoa(request.ID) {
			continue
		}
		if reply.Error != nil {
			if reply.Error.Message != "" {
				return nil, fmt.Errorf("Codex %s returned error code %d: %s", request.Method, reply.Error.Code, reply.Error.Message)
			}
			return nil, fmt.Errorf("Codex %s returned error code %d", request.Method, reply.Error.Code)
		}
		if len(reply.Result) == 0 || string(reply.Result) == "null" {
			return nil, fmt.Errorf("Codex %s returned an empty response", request.Method)
		}
		return reply.Result, nil
	}
	if err := ctx.Err(); err != nil {
		return nil, fmt.Errorf("waiting for %s: %w", request.Method, err)
	}
	return nil, fmt.Errorf("App Server output ended while awaiting %s", request.Method)
}

func validatePaths(config Config) error {
	for _, entry := range []struct {
		label string
		path  string
		isDir bool
	}{
		{"Codex executable", config.Executable, false},
		{"project directory", config.ProjectDir, true},
		{"Codex home", config.CodexHome, true},
	} {
		if !filepath.IsAbs(entry.path) {
			return fmt.Errorf("%s must be an absolute path", entry.label)
		}
		info, err := os.Stat(entry.path)
		if err != nil {
			return fmt.Errorf("open %s: %w", entry.label, err)
		}
		if (entry.isDir && !info.IsDir()) || (!entry.isDir && !info.Mode().IsRegular()) {
			return fmt.Errorf("invalid %s", entry.label)
		}
	}
	return nil
}
