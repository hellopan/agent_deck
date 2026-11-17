package main

import (
	"context"
	"errors"
	"time"

	"github.com/hellopan/agent_deck/internal/projects"
)

var errProjectRevoked = errors.New("project authorization was removed or changed")
var errTaskCanceled = errors.New("local task cancellation requested")

// runWhileAuthorized rechecks the project every half second. The sandbox is part
// of the check: narrowing a project from writable to read-only has to interrupt
// the turn, because the running thread was started with the wider scope.
func runWhileAuthorized(ctx context.Context, config codexOptions, directory, sandbox string, run func(context.Context, func() error) (string, error), cancellation func() (bool, error)) (string, error) {
	runCtx, cancel := context.WithCancelCause(ctx)
	defer cancel(nil)
	check := func() error {
		if err := context.Cause(runCtx); err != nil {
			return err
		}
		current, mode, err := projects.ResolveMode(config.policy, config.projectID)
		if err != nil || current != directory || string(mode) != sandbox {
			cancel(errProjectRevoked)
			return errProjectRevoked
		}
		if cancellation != nil {
			requested, err := cancellation()
			if err != nil {
				cancel(err)
			} else if requested {
				cancel(errTaskCanceled)
			}
		}
		return context.Cause(runCtx)
	}
	if err := check(); err != nil {
		return "", err
	}

	done := make(chan struct{})
	go func() {
		defer close(done)
		ticker := time.NewTicker(500 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-runCtx.Done():
				return
			case <-ticker.C:
				if check() != nil {
					return
				}
			}
		}
	}()
	status, err := run(runCtx, check)
	cause := context.Cause(runCtx)
	cancel(nil)
	<-done
	if cause != nil {
		return "interrupted", errors.Join(cause, err)
	}
	return status, err
}
