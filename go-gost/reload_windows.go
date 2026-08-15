//go:build windows

package main

import "context"

// Windows services do not expose SIGHUP. Configuration changes are delivered
// by the panel through the Agent WebSocket commands instead.
func watchConfigReload(ctx context.Context, _ *program) {
	<-ctx.Done()
}
