package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

type cancelReply struct {
	Requested bool `json:"requested"`
}

// requestTaskCancel records a stop request. A queued request is dropped before
// it starts; a running one is noticed by the runner on its next check.
func requestTaskCancel(opts peerOptions, query taskLogQuery) error {
	if !validReceiptID(query.TaskID) {
		return errors.New("cancellation requires a task ID")
	}
	directory, err := projects.Resolve(opts.policy, query.ProjectID)
	if err != nil {
		return err
	}
	canceled := false
	if opts.inbox != "" {
		root, err := os.OpenRoot(opts.inbox)
		if err != nil {
			return err
		}
		defer root.Close()
		record, err := readInboxRecord(root, query.TaskID)
		if err != nil {
			return err
		}
		if record.ProjectID != query.ProjectID || record.Directory != directory {
			return errors.New("task is outside the authorized project")
		}
		if err := writeMarker(root, query.TaskID+".cancel.log"); err != nil {
			return err
		}
		canceled = true
	}
	// A running turn only sees its own journal marker, so record both.
	if opts.logDir != "" {
		if err := codexprobe.RequestProjectCancellation(opts.logDir, query.TaskID, query.ProjectID, directory); err == nil {
			canceled = true
		}
	}
	if !canceled {
		return errors.New("task cancellation unavailable")
	}
	return nil
}

func serveRemoteCancel(conn io.Writer, opts peerOptions, raw string) {
	var query taskLogQuery
	requested := false
	if len(raw) <= 1024 && json.Unmarshal([]byte(raw), &query) == nil && query.After == 0 {
		requested = requestTaskCancel(opts, query) == nil
	}
	json.NewEncoder(conn).Encode(cancelReply{Requested: requested})
}

func sendRemoteCancel(conn io.ReadWriter, query taskLogQuery) error {
	if query.ProjectID == "" || len(query.TaskID) != 32 || query.After != 0 {
		return errors.New("remote-cancel requires a project ID and a task ID")
	}
	payload, err := json.Marshal(query)
	if err != nil {
		return err
	}
	if len(payload) > 1023 {
		return errors.New("cancellation request is too large")
	}
	if _, err := fmt.Fprintf(conn, "%s%s\n", remoteCancelMessage, payload); err != nil {
		return err
	}
	var reply cancelReply
	if err := json.NewDecoder(io.LimitReader(conn, 1024)).Decode(&reply); err != nil || !reply.Requested {
		return errors.New("remote task cancellation unavailable")
	}
	return json.NewEncoder(os.Stdout).Encode(reply)
}
