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

func serveRemoteCancel(conn io.Writer, opts peerOptions, raw string) {
	var query taskLogQuery
	requested := false
	if opts.logDir != "" && len(raw) <= 1024 && json.Unmarshal([]byte(raw), &query) == nil && query.After == 0 {
		dir, err := projects.Resolve(opts.policy, query.ProjectID)
		if err == nil {
			requested = codexprobe.RequestProjectCancellation(opts.logDir, query.TaskID, query.ProjectID, dir) == nil
		}
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
