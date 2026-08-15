//go:build !windows

package main

import (
	"fmt"
	"os"
	"syscall"
)

func restartWithBinary(executable, temporary string) error {
	backup := executable + ".previous"
	_ = os.Remove(backup)
	if err := os.Rename(executable, backup); err != nil {
		return fmt.Errorf("备份旧 Agent 失败：%w", err)
	}
	if err := os.Rename(temporary, executable); err != nil {
		_ = os.Rename(backup, executable)
		return fmt.Errorf("安装新 Agent 失败：%w", err)
	}
	if err := syscall.Exec(executable, os.Args, os.Environ()); err != nil {
		_ = os.Remove(executable)
		_ = os.Rename(backup, executable)
		return err
	}
	return nil
}
