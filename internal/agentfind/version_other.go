//go:build !windows

package agentfind

import "os/exec"

// hideWindow is a no-op outside Windows; detection never finds a tool there.
func hideWindow(cmd *exec.Cmd) {}
