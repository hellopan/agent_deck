package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
)

// fetchQueue reads the stored request list from an Agent. Each call is one
// short request/response exchange on a fresh tunnel.
func fetchQueue(conn io.ReadWriter) error {
	if _, err := fmt.Fprintln(conn, queueMessage); err != nil {
		return err
	}
	var reply queueView
	if err := json.NewDecoder(io.LimitReader(conn, 1024*1024)).Decode(&reply); err != nil {
		return errors.New("remote task queue unavailable")
	}
	if reply.Items == nil {
		reply.Items = []queueItem{}
	}
	return json.NewEncoder(os.Stdout).Encode(reply)
}

func fetchApprovals(conn io.ReadWriter) error {
	if _, err := fmt.Fprintln(conn, approvalsMessage); err != nil {
		return err
	}
	var reply approvalSnapshot
	if err := json.NewDecoder(io.LimitReader(conn, 256*1024)).Decode(&reply); err != nil {
		return errors.New("remote approval list unavailable")
	}
	if reply.Approvals == nil {
		reply.Approvals = []approvalItem{}
	}
	return json.NewEncoder(os.Stdout).Encode(reply)
}

func sendDecision(conn io.ReadWriter, opts peerOptions) error {
	request := decisionRequest{ApprovalID: opts.approvalID, Decision: opts.decision}
	if !validReceiptID(request.ApprovalID) || (request.Decision != "accept" && request.Decision != "decline") {
		return errors.New("approve requires an approval ID and a decision of accept or decline")
	}
	payload, err := json.Marshal(request)
	if err != nil {
		return err
	}
	if _, err := fmt.Fprintf(conn, "%s%s\n", decideMessage, payload); err != nil {
		return err
	}
	var reply decisionReply
	if err := json.NewDecoder(io.LimitReader(conn, 1024)).Decode(&reply); err != nil || !reply.OK {
		if reply.Error != "" {
			return errors.New(reply.Error)
		}
		return errors.New("remote approval decision unavailable")
	}
	return json.NewEncoder(os.Stdout).Encode(reply)
}
