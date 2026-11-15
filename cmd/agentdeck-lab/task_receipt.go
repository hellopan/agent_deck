package main

import (
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"

	"github.com/hellopan/agent_deck/internal/projects"
)

type receiptQuery struct {
	ProjectID string `json:"projectId"`
	TaskID    string `json:"taskId"`
}

func validReceiptID(id string) bool {
	_, err := hex.DecodeString(id)
	return err == nil && len(id) == 32 && strings.ToLower(id) == id
}

func loadReceipt(opts peerOptions, query receiptQuery) (taskReceipt, error) {
	if opts.inbox == "" || !validReceiptID(query.TaskID) {
		return taskReceipt{}, errors.New("invalid task receipt request")
	}
	dir, err := projects.Resolve(opts.policy, query.ProjectID)
	if err != nil {
		return taskReceipt{}, err
	}
	root, err := os.OpenRoot(opts.inbox)
	if err != nil {
		return taskReceipt{}, err
	}
	defer root.Close()
	record, err := readInboxRecord(root, query.TaskID)
	if err != nil {
		return taskReceipt{}, err
	}
	if record.ProjectID != query.ProjectID || record.Directory != dir {
		return taskReceipt{}, errors.New("task receipt outside the authorized project")
	}
	status, err := taskState(root, opts.logDir, record.ProjectID, dir, query.TaskID)
	if err != nil {
		return taskReceipt{}, err
	}
	return taskReceipt{TaskID: query.TaskID, Status: status}, nil
}

func serveReceipt(conn io.Writer, opts peerOptions, raw string) {
	var query receiptQuery
	reply := taskReceipt{}
	if len(raw) <= 1024 && json.Unmarshal([]byte(raw), &query) == nil {
		reply, _ = loadReceipt(opts, query)
	}
	json.NewEncoder(conn).Encode(reply)
}

func queryReceipt(conn io.ReadWriter, opts peerOptions) error {
	query := receiptQuery{ProjectID: opts.query.ProjectID, TaskID: opts.query.TaskID}
	if opts.submission.RequestID != "" {
		if query.TaskID != "" || !validReceiptID(opts.submission.RequestID) {
			return errors.New("supply either a valid request ID or a task ID")
		}
		query.TaskID = receiptID(opts.submission.RequestID)
	}
	if query.ProjectID == "" || !validReceiptID(query.TaskID) {
		return errors.New("task-receipt requires a project ID and task or request ID")
	}
	raw, err := json.Marshal(query)
	if err != nil || len(raw) > 1023 {
		return errors.New("encoded task receipt request is too large")
	}
	if _, err := fmt.Fprintf(conn, "%s%s\n", receiptMessage, raw); err != nil {
		return err
	}
	var reply taskReceipt
	if err := json.NewDecoder(io.LimitReader(conn, 1024)).Decode(&reply); err != nil || reply.TaskID != query.TaskID ||
		!validTaskStatus(reply.Status) {
		return errors.New("remote task receipt unavailable")
	}
	return json.NewEncoder(os.Stdout).Encode(reply)
}
