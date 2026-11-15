package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"

	"github.com/hellopan/agent_deck/internal/codexprobe"
	"github.com/hellopan/agent_deck/internal/projects"
)

type taskLogQuery struct {
	ProjectID string `json:"projectId"`
	TaskID    string `json:"taskId"`
	After     int    `json:"after"`
}

type taskLogReply struct {
	Page *codexprobe.TaskPage `json:"page,omitempty"`
}

func addTaskLogOptions(flags *flag.FlagSet) *taskLogQuery {
	query := &taskLogQuery{}
	flags.StringVar(&query.ProjectID, "project-id", "", "authorized project ID")
	flags.StringVar(&query.TaskID, "task-id", "", "task ID previously returned by codex-task")
	flags.IntVar(&query.After, "after", 0, "last event cursor")
	return query
}

func serveTaskLog(conn io.Writer, opts peerOptions, raw string) {
	reply := taskLogReply{}
	defer func() { json.NewEncoder(conn).Encode(reply) }()
	var query taskLogQuery
	if opts.logDir == "" || json.Unmarshal([]byte(raw), &query) != nil {
		return
	}
	directory, err := projects.Resolve(opts.policy, query.ProjectID)
	if err != nil {
		return
	}
	page, err := codexprobe.ReadJournal(opts.logDir, query.TaskID, query.ProjectID, directory, query.After)
	if err == nil {
		reply.Page = &page
	}
}

func fetchTaskLog(conn io.ReadWriter, query taskLogQuery) (codexprobe.TaskPage, error) {
	payload, err := json.Marshal(query)
	if err != nil {
		return codexprobe.TaskPage{}, err
	}
	if len(payload)+len(taskLogMessage)+1 > 1024 || query.ProjectID == "" || len(query.TaskID) != 32 || query.After < 0 {
		return codexprobe.TaskPage{}, errors.New("task-log requires a project ID, task ID and nonnegative cursor")
	}
	if _, err := fmt.Fprintf(conn, "%s%s\n", taskLogMessage, payload); err != nil {
		return codexprobe.TaskPage{}, err
	}
	var reply taskLogReply
	if err := json.NewDecoder(io.LimitReader(conn, 3*1024*1024)).Decode(&reply); err != nil || reply.Page == nil {
		return codexprobe.TaskPage{}, errors.New("remote task log unavailable")
	}
	return *reply.Page, nil
}
