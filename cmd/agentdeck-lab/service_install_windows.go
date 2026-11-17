package main

import (
	"errors"
	"fmt"
	"path/filepath"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

// installService registers the Agent as an auto-start service. The command line
// is the one the tray would build, so the service runs the same Agent with the
// same identity and the same pairing folder — the only difference is who starts
// it. Registration needs an elevated process; the tray asks for that itself.
func installService(name, executable string, args []string) error {
	if name == "" || !filepath.IsAbs(executable) {
		return errors.New("service install requires a name and an absolute executable path")
	}
	manager, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("connect to the service manager (run elevated): %w", err)
	}
	defer manager.Disconnect()
	service, err := manager.CreateService(name, executable, mgr.Config{
		DisplayName: "AgentDeck 本机代理",
		Description: "让已配对的手机在 Windows 登录前就能连上这台电脑。",
		StartType:   mgr.StartAutomatic,
	}, args...)
	if errors.Is(err, windows.ERROR_SERVICE_EXISTS) {
		return fmt.Errorf("service %s is already installed", name)
	}
	if err != nil {
		return fmt.Errorf("create service: %w", err)
	}
	defer service.Close()
	if err := service.Start(); err != nil {
		return fmt.Errorf("service %s was registered but did not start: %w", name, err)
	}
	return nil
}

// uninstallService stops the service and removes it from the SCM. Stopping
// first matters: deleting a running service leaves the process behind until the
// next boot, still holding the relay connection and the device identity.
func uninstallService(name string) error {
	if name == "" {
		return errors.New("service uninstall requires a name")
	}
	manager, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("connect to the service manager (run elevated): %w", err)
	}
	defer manager.Disconnect()
	service, err := manager.OpenService(name)
	if errors.Is(err, windows.ERROR_SERVICE_DOES_NOT_EXIST) {
		return fmt.Errorf("service %s is not installed", name)
	}
	if err != nil {
		return fmt.Errorf("open service: %w", err)
	}
	defer service.Close()
	if status, err := service.Query(); err == nil && status.State != svc.Stopped {
		if _, err := service.Control(svc.Stop); err != nil {
			return fmt.Errorf("stop service: %w", err)
		}
		// Wait for the stop the same way the SCM does, then delete. Deleting a
		// service that is still stopping fails with ERROR_SERVICE_MARKED_FOR_DELETE
		// on the next install.
		deadline := time.Now().Add(15 * time.Second)
		for time.Now().Before(deadline) {
			status, err := service.Query()
			if err == nil && status.State == svc.Stopped {
				break
			}
			time.Sleep(200 * time.Millisecond)
		}
	}
	if err := service.Delete(); err != nil {
		return fmt.Errorf("delete service: %w", err)
	}
	return nil
}
