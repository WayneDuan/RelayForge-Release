//go:build windows

package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

const windowsServiceName = "RelayForgeAgent"

func restartWithBinary(executable, temporary string) error {
	pid := os.Getpid()
	script := fmt.Sprintf("$pidToWait=%d; while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }; Move-Item -LiteralPath %s -Destination %s -Force; if (Get-Service -Name %s -ErrorAction SilentlyContinue) { Start-Service -Name %s } else { Start-Process -FilePath %s -WorkingDirectory %s }",
		pid, powerShellQuote(temporary), powerShellQuote(executable), powerShellQuote(windowsServiceName), powerShellQuote(windowsServiceName), powerShellQuote(executable), powerShellQuote(filepath.Dir(executable)))
	command := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script)
	if err := command.Start(); err != nil {
		return err
	}

	// The detached updater waits until this process releases the executable,
	// replaces it, then restarts the Windows service (or a manual Agent run).
	os.Exit(0)
	return nil
}

func powerShellQuote(value string) string {
	return "'" + strings.ReplaceAll(value, "'", "''") + "'"
}
