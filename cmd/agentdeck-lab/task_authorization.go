package main

import (
	"context"
	"errors"
	"time"

	"github.com/hellopan/agent_deck/internal/projects"
)

var errProjectRevoked = errors.New("project authorization was removed or changed")
var errTaskCanceled = errors.New("local task cancellation requested")

func runWhileAuthorized(ctx context.Context, config codexOptions, directory string, run func(context.Context, func() error) (string, error), cancellation func() (bool, error)) (string, error) {
	runCtx, cancel := context.WithCancelCause(ctx)
	defer cancel(nil)
	check := func() error {
		if err := context.Cause(runCtx); err != nil {
			return err
		}
		current, err := projects.Resolve(config.policy, config.projectID)
		if err != nil || current != directory {
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
