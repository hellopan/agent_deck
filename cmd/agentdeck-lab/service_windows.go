package main

import (
	"context"

	"golang.org/x/sys/windows/svc"
)

type serviceHandler struct {
	ctx    context.Context
	worker func(context.Context) error
}

func launchService(ctx context.Context, name string, worker func(context.Context) error) error {
	return svc.Run(name, serviceHandler{ctx: ctx, worker: worker})
}

func (h serviceHandler) Execute(_ []string, requests <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	status <- svc.Status{State: svc.StartPending}
	ctx, cancel := context.WithCancel(h.ctx)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- h.worker(ctx) }()
	current := svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	status <- current
	for {
		select {
		case err := <-done:
			if err != nil {
				return true, 1
			}
			return false, 0
		case request, ok := <-requests:
			if !ok || request.Cmd == svc.Stop || request.Cmd == svc.Shutdown {
				status <- svc.Status{State: svc.StopPending, WaitHint: 15000}
				cancel()
				if err := <-done; err != nil {
					return true, 1
				}
				return false, 0
			}
			if request.Cmd == svc.Interrogate {
				status <- current
			}
		}
	}
}
