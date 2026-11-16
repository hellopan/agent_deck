//go:build windows

package agentfind

import (
	"os/exec"
	"syscall"
)

// hideWindow stops a console child from flashing a window when the tray (a GUI
// process with no console of its own) shells out to ask for a version.
func hideWindow(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
}
