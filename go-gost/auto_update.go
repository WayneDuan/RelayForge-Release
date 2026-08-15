package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

const (
	defaultUpdateManifestURL = "https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/agent-manifest.json"
	updateCheckDelay         = 30 * time.Second
	updateCheckInterval      = 6 * time.Hour
	updateRequestTimeout     = 45 * time.Second
	maxUpdateBinarySize      = 512 * 1024 * 1024
)

type agentManifest struct {
	Version string                 `json:"version"`
	Assets  map[string]agentAsset `json:"assets"`
}

type agentAsset struct {
	URL    string `json:"url"`
	SHA256 string `json:"sha256"`
}

// runAutoUpdater checks the public release manifest periodically. Missing
// fields in older config.json files intentionally keep auto-updates enabled.
func runAutoUpdater(ctx context.Context, config *Config) {
	if config.AutoUpdate != nil && !*config.AutoUpdate {
		log.Printf("Agent 自动升级已关闭")
		return
	}

	manifestURL := strings.TrimSpace(config.UpdateManifestURL)
	if manifestURL == "" {
		manifestURL = defaultUpdateManifestURL
	}
	if !isHTTPSURL(manifestURL) {
		log.Printf("Agent 自动升级已跳过：更新清单必须使用 HTTPS")
		return
	}

	timer := time.NewTimer(updateCheckDelay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return
	case <-timer.C:
	}

	check := func() {
		if err := checkAndApplyUpdate(ctx, manifestURL); err != nil {
			log.Printf("Agent 自动升级检查失败：%v", err)
		}
	}
	check()

	ticker := time.NewTicker(updateCheckInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			check()
		}
	}
}

func checkAndApplyUpdate(parent context.Context, manifestURL string) error {
	ctx, cancel := context.WithTimeout(parent, updateRequestTimeout)
	defer cancel()

	manifest, err := fetchManifest(ctx, manifestURL)
	if err != nil {
		return err
	}
	if compareVersions(manifest.Version, AgentVersion) <= 0 {
		log.Printf("Agent 已是最新版本：%s", AgentVersion)
		return nil
	}

	assetKey := runtime.GOOS + "-" + runtime.GOARCH
	asset, ok := manifest.Assets[assetKey]
	if !ok {
		return fmt.Errorf("更新清单没有当前平台文件：%s", assetKey)
	}
	assetURL, err := resolveAssetURL(manifestURL, asset.URL)
	if err != nil || !isSHA256(asset.SHA256) {
		return errors.New("更新清单中的下载地址或 SHA-256 无效")
	}

	log.Printf("发现 Agent 新版本 %s，开始下载", manifest.Version)
	if err := downloadAndReplace(ctx, assetURL, strings.ToLower(asset.SHA256), manifest.Version); err != nil {
		return err
	}
	return nil
}

func fetchManifest(ctx context.Context, manifestURL string) (agentManifest, error) {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, manifestURL, nil)
	if err != nil {
		return agentManifest{}, err
	}
	request.Header.Set("User-Agent", "RelayForge-Agent-Updater/1")
	response, err := updateHTTPClient().Do(request)
	if err != nil {
		return agentManifest{}, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return agentManifest{}, fmt.Errorf("更新清单返回 HTTP %d", response.StatusCode)
	}

	var manifest agentManifest
	if err := json.NewDecoder(io.LimitReader(response.Body, 64*1024)).Decode(&manifest); err != nil {
		return agentManifest{}, fmt.Errorf("解析更新清单失败：%w", err)
	}
	if manifest.Version == "" || len(manifest.Assets) == 0 {
		return agentManifest{}, errors.New("更新清单缺少版本或文件")
	}
	return manifest, nil
}

func downloadAndReplace(ctx context.Context, assetURL, expectedSHA, newVersion string) error {
	executable, err := os.Executable()
	if err != nil {
		return fmt.Errorf("定位 Agent 文件失败：%w", err)
	}
	executable, err = filepath.EvalSymlinks(executable)
	if err != nil {
		return fmt.Errorf("解析 Agent 文件路径失败：%w", err)
	}

	request, err := http.NewRequestWithContext(ctx, http.MethodGet, assetURL, nil)
	if err != nil {
		return err
	}
	request.Header.Set("User-Agent", "RelayForge-Agent-Updater/1")
	response, err := updateHTTPClient().Do(request)
	if err != nil {
		return fmt.Errorf("下载 Agent 失败：%w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("Agent 下载返回 HTTP %d", response.StatusCode)
	}
	if response.ContentLength > maxUpdateBinarySize {
		return errors.New("下载文件超过大小限制")
	}

	temporary := executable + fmt.Sprintf(".update.%d", os.Getpid())
	file, err := os.OpenFile(temporary, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o755)
	if err != nil {
		return fmt.Errorf("创建升级临时文件失败：%w", err)
	}
	hash := sha256.New()
	written, copyErr := io.Copy(io.MultiWriter(file, hash), io.LimitReader(response.Body, maxUpdateBinarySize+1))
	closeErr := file.Close()
	if copyErr != nil {
		_ = os.Remove(temporary)
		return fmt.Errorf("写入升级文件失败：%w", copyErr)
	}
	if closeErr != nil {
		_ = os.Remove(temporary)
		return fmt.Errorf("关闭升级文件失败：%w", closeErr)
	}
	if written > maxUpdateBinarySize {
		_ = os.Remove(temporary)
		return errors.New("下载文件超过大小限制")
	}
	if actualSHA := hex.EncodeToString(hash.Sum(nil)); actualSHA != expectedSHA {
		_ = os.Remove(temporary)
		return fmt.Errorf("升级文件校验失败：期望 %s，实际 %s", expectedSHA, actualSHA)
	}

	if err := restartWithBinary(executable, temporary); err != nil {
		_ = os.Remove(temporary)
		return fmt.Errorf("替换 Agent 失败：%w", err)
	}
	log.Printf("Agent 已升级到 %s，正在重启", newVersion)
	return nil
}

func isHTTPSURL(raw string) bool {
	parsed, err := url.Parse(raw)
	return err == nil && parsed.Scheme == "https" && parsed.Host != ""
}

func resolveAssetURL(manifestURL, assetURL string) (string, error) {
	assetURL = strings.TrimSpace(assetURL)
	if assetURL == "" {
		return "", errors.New("更新文件地址为空")
	}
	parsed, err := url.Parse(assetURL)
	if err != nil {
		return "", err
	}
	if parsed.IsAbs() {
		if !isHTTPSURL(assetURL) {
			return "", errors.New("更新文件地址必须使用 HTTPS")
		}
		return assetURL, nil
	}
	base, err := url.Parse(manifestURL)
	if err != nil || !isHTTPSURL(manifestURL) {
		return "", errors.New("更新清单地址无效")
	}
	resolved := base.ResolveReference(parsed).String()
	if !isHTTPSURL(resolved) {
		return "", errors.New("解析后的更新文件地址必须使用 HTTPS")
	}
	return resolved, nil
}

func updateHTTPClient() *http.Client {
	return &http.Client{
		CheckRedirect: func(request *http.Request, _ []*http.Request) error {
			if request.URL.Scheme != "https" {
				return errors.New("更新地址重定向到非 HTTPS 地址")
			}
			return nil
		},
	}
}

func isSHA256(value string) bool {
	if len(value) != sha256.Size*2 {
		return false
	}
	_, err := hex.DecodeString(value)
	return err == nil
}

func compareVersions(left, right string) int {
	leftParts := versionParts(left)
	rightParts := versionParts(right)
	for index := 0; index < len(leftParts) || index < len(rightParts); index++ {
		leftValue, rightValue := 0, 0
		if index < len(leftParts) {
			leftValue = leftParts[index]
		}
		if index < len(rightParts) {
			rightValue = rightParts[index]
		}
		if leftValue > rightValue {
			return 1
		}
		if leftValue < rightValue {
			return -1
		}
	}
	return 0
}

func versionParts(value string) []int {
	value = strings.TrimPrefix(strings.TrimSpace(value), "v")
	parts := strings.Split(value, ".")
	result := make([]int, 0, len(parts))
	for _, part := range parts {
		number := 0
		for _, char := range part {
			if char < '0' || char > '9' {
				break
			}
			number = number*10 + int(char-'0')
		}
		result = append(result, number)
	}
	return result
}
