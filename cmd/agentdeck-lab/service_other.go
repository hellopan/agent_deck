//go:build !windows

package main

import (
	"context"
	"errors"
)

func launchService(_ context.Context, _ string, _ func(context.Context) error) error {
	return errors.New("service mode requires the Windows Service Control Manager")
}
