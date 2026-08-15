//go:build !windows

package main

import (
	"context"
	"os"
	"os/signal"
	"syscall"

	"github.com/go-gost/core/logger"
)

func watchConfigReload(ctx context.Context, p *program) {
	signals := make(chan os.Signal, 1)
	signal.Notify(signals, syscall.SIGHUP)
	defer signal.Stop(signals)

	for {
		select {
		case <-signals:
			if err := p.reloadConfig(); err != nil {
				logger.Default().Error(err)
			} else {
				logger.Default().Info("config reloaded")
			}
		case <-ctx.Done():
			return
		}
	}
}
