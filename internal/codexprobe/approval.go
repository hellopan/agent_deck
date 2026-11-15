package codexprobe

import (
	"context"
	"encoding/json"
	"errors"
)

const (
	commandApprovalMethod     = "item/commandExecution/requestApproval"
	fileApprovalMethod        = "item/fileChange/requestApproval"
	permissionsApprovalMethod = "item/permissions/requestApproval"
)

const (
	commandApprovalKind     = "command"
	fileApprovalKind        = "file"
	permissionsApprovalKind = "permissions"
)

// Approval describes one tool request the read-only sandbox refused. It is the
// only thing the phone needs in order to decide.
type Approval struct {
	Kind      string `json:"kind"`
	ThreadID  string `json:"threadId"`
	TurnID    string `json:"turnId"`
	ItemID    string `json:"itemId"`
	Command   string `json:"command,omitempty"`
	Cwd       string `json:"cwd,omitempty"`
	Reason    string `json:"reason,omitempty"`
	GrantRoot string `json:"grantRoot,omitempty"`
}

// NeedsDecision reports whether the request can be granted for a single
// operation. A permission profile widens the sandbox for the rest of the
// session, so it is refused without asking.
func (a Approval) NeedsDecision() bool { return a.Kind != permissionsApprovalKind }

// Summary is the one-line form shown in the task timeline.
func (a Approval) Summary() string {
	for _, value := range []string{a.Command, a.GrantRoot, a.Reason} {
		if value != "" {
			return value
		}
	}
	return a.Kind
}

// Decider resolves one approval before the turn continues. Returning false
// denies the request and lets Codex continue without it; an error aborts the
// task.
type Decider func(context.Context, Approval) (bool, error)

// approvalRequest mirrors the app-server request params. The field set differs
// per method, so unknown members are simply ignored.
type approvalRequest struct {
	ThreadID  string `json:"threadId"`
	TurnID    string `json:"turnId"`
	ItemID    string `json:"itemId"`
	Command   string `json:"command"`
	Cwd       string `json:"cwd"`
	Reason    string `json:"reason"`
	GrantRoot string `json:"grantRoot"`
}

type approvalReply struct {
	ID     json.RawMessage `json:"id"`
	Result any             `json:"result"`
}

// describeApproval validates a tool request and reduces it to what the phone
// renders. Anything outside the active turn is treated as a protocol error.
func describeApproval(method string, id json.RawMessage, request approvalRequest, threadID, turnID string) (Approval, error) {
	if turnID == "" || request.ThreadID != threadID || request.TurnID != turnID || request.ItemID == "" {
		return Approval{}, errors.New("approval request does not match the active Codex operation")
	}
	var numeric json.Number
	var textual string
	if len(id) == 0 || string(id) == "null" ||
		(json.Unmarshal(id, &numeric) != nil && json.Unmarshal(id, &textual) != nil) {
		return Approval{}, errors.New("invalid Codex approval request ID")
	}
	approval := Approval{
		ThreadID: request.ThreadID, TurnID: request.TurnID, ItemID: request.ItemID,
		Command: request.Command, Cwd: request.Cwd, Reason: request.Reason, GrantRoot: request.GrantRoot,
	}
	switch method {
	case commandApprovalMethod:
		approval.Kind = commandApprovalKind
	case fileApprovalMethod:
		approval.Kind = fileApprovalKind
	case permissionsApprovalMethod:
		approval.Kind = permissionsApprovalKind
	default:
		return Approval{}, errors.New("unsupported Codex approval request")
	}
	return approval, nil
}

// decisionWord is the Codex vocabulary for a one-time decision.
func decisionWord(approved bool) string {
	if approved {
		return "accept"
	}
	return "decline"
}

// approvalDecision answers a command or file request. Codex accepts accept and
// decline for both, and neither is remembered for later requests.
func approvalDecision(id json.RawMessage, approved bool) approvalReply {
	return approvalReply{ID: id, Result: map[string]string{"decision": decisionWord(approved)}}
}

// permissionsReply grants nothing. The product escalates one command at a
// time, never a session-wide profile.
func permissionsReply(id json.RawMessage) approvalReply {
	return approvalReply{ID: id, Result: map[string]any{"permissions": map[string]any{}}}
}
