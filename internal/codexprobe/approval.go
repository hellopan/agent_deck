package codexprobe

import (
	"encoding/json"
	"errors"
	"slices"
)

const commandApprovalMethod = "item/commandExecution/requestApproval"
const fileApprovalMethod = "item/fileChange/requestApproval"

type approvalRequest struct {
	ThreadID           string            `json:"threadId"`
	TurnID             string            `json:"turnId"`
	ItemID             string            `json:"itemId"`
	AvailableDecisions []json.RawMessage `json:"availableDecisions"`
}

type approvalReply struct {
	ID     json.RawMessage `json:"id"`
	Result struct {
		Decision string `json:"decision"`
	} `json:"result"`
}

// declineApproval keeps the read-only runner inside its existing policy.
// The only emitted decisions leave the requested operation unauthorized.
func declineApproval(method string, id json.RawMessage, request approvalRequest, threadID, turnID string) (approvalReply, error) {
	if turnID == "" || request.ThreadID != threadID || request.TurnID != turnID || request.ItemID == "" {
		return approvalReply{}, errors.New("approval request does not match the active Codex operation")
	}
	var numeric json.Number
	var textual string
	if len(id) == 0 || string(id) == "null" ||
		(json.Unmarshal(id, &numeric) != nil && json.Unmarshal(id, &textual) != nil) {
		return approvalReply{}, errors.New("invalid Codex approval request ID")
	}
	reply := approvalReply{ID: id}
	switch method {
	case fileApprovalMethod:
		reply.Result.Decision = "decline"
	case commandApprovalMethod:
		if request.AvailableDecisions == nil || hasDecision(request.AvailableDecisions, "decline") {
			reply.Result.Decision = "decline"
		} else if hasDecision(request.AvailableDecisions, "cancel") {
			reply.Result.Decision = "cancel"
		} else {
			return approvalReply{}, errors.New("Codex approval does not offer a denial decision")
		}
	default:
		return approvalReply{}, errors.New("unsupported Codex approval request")
	}
	return reply, nil
}

func hasDecision(options []json.RawMessage, decision string) bool {
	return slices.ContainsFunc(options, func(option json.RawMessage) bool {
		var value string
		return json.Unmarshal(option, &value) == nil && value == decision
	})
}
