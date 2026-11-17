//go:build !windows

package main

import (
	"context"
	"errors"
)

func launchService(_ context.Context, _ string, _ func(context.Context) error) error {
	return errors.New("service mode requires the Windows Service Control Manager")
}

func installService(_, _ string, _ []string) error {
	return errors.New("service install requires the Windows Service Control Manager")
}

func uninstallService(_ string) error {
	return errors.New("service uninstall requires the Windows Service Control Manager")
}
