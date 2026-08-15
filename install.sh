#!/bin/bash
set -e
umask 077

# 获取系统架构
get_architecture() {
    ARCH=$(uname -m)
    case $ARCH in
        x86_64)
            echo "amd64"
            ;;
        aarch64|arm64)
            echo "arm64"
            ;;
        *)
            echo "不支持的系统架构: $ARCH（仅支持 amd64 和 arm64）" >&2
            return 1
            ;;
    esac
}

RELEASE_BASE_URL="${RELAYFORGE_RELEASE_BASE_URL:-https://github.com/WayneDuan/RelayForge-Release/releases/latest/download}"
RELEASE_BASE_URL="${RELEASE_BASE_URL%/}"
case "$RELEASE_BASE_URL" in
  https://?*) ;;
  *)
    echo "❌ RELAYFORGE_RELEASE_BASE_URL 必须是 HTTPS 地址。" >&2
    exit 1
    ;;
esac

# 构建下载地址
build_download_url() {
    local ARCH=$(get_architecture)
    echo "${RELEASE_BASE_URL}/gost-${ARCH}"
}

build_manifest_url() {
  echo "${RELEASE_BASE_URL}/agent-manifest.json"
}

build_checksums_url() {
  echo "${RELEASE_BASE_URL}/checksums.txt"
}

# 下载地址
DOWNLOAD_URL=$(build_download_url)
UPDATE_MANIFEST_URL=$(build_manifest_url)
CHECKSUMS_URL=$(build_checksums_url)
INSTALL_DIR="/etc/gost"

# A mirror is opt-in. Downloading both an asset and its checksum through an
# automatically selected third party makes the trust boundary needlessly wide.
if [[ -n "${RELAYFORGE_RELEASE_MIRROR:-}" ]]; then
  RELEASE_MIRROR="${RELAYFORGE_RELEASE_MIRROR%/}"
  DOWNLOAD_URL="${RELEASE_MIRROR}/${DOWNLOAD_URL}"
  UPDATE_MANIFEST_URL="${RELEASE_MIRROR}/${UPDATE_MANIFEST_URL}"
  CHECKSUMS_URL="${RELEASE_MIRROR}/${CHECKSUMS_URL}"
fi

UPDATE_MANIFEST_URL="${RELAYFORGE_AGENT_MANIFEST_URL:-$UPDATE_MANIFEST_URL}"

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    echo "❌ 找不到 sha256sum 或 shasum，无法校验 Agent。" >&2
    return 1
  fi
}

normalize_hash() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}



# 显示菜单
show_menu() {
  echo "==============================================="
  echo "              管理脚本"
  echo "==============================================="
  echo "请选择操作："
  echo "1. 安装"
  echo "2. 更新"
  echo "3. 卸载"
  echo "4. 退出"
  echo "==============================================="
}

# Do not delete the caller's shell when this file is piped to bash. Leaving
# the downloaded file is safer and makes failures easier to inspect.
delete_self() {
  return 0
}

# 获取用户输入的配置参数
get_config_params() {
  if [[ -z "$SERVER_ADDR" || -z "$SECRET" ]]; then
    echo "请输入配置参数："

    if [[ -z "$SERVER_ADDR" ]]; then
      read -p "服务器地址: " SERVER_ADDR
    fi

    if [[ -z "$SECRET" ]]; then
      read -p "密钥: " SECRET
    fi

    if [[ -z "$SERVER_ADDR" || -z "$SECRET" ]]; then
      echo "❌ 参数不完整，操作取消。"
      exit 1
    fi
  fi
}

# 解析命令行参数
while getopts "a:s:" opt; do
  case $opt in
    a) SERVER_ADDR="$OPTARG" ;;
    s) SECRET="$OPTARG" ;;
    *) echo "❌ 无效参数"; exit 1 ;;
  esac
done

# 安装功能
install_gost() {
  echo "🚀 开始安装 GOST..."
  get_config_params

  if ! command -v systemctl >/dev/null 2>&1; then
    echo "❌ Linux Agent 需要 systemd/systemctl。"
    return 1
  fi
  if ! command -v curl >/dev/null 2>&1; then
    echo "❌ 安装 Agent 需要 curl。"
    return 1
  fi

  mkdir -p "$INSTALL_DIR"

  # 下载并校验 gost，校验成功前保留旧版本
  echo "⬇️ 下载 gost 中..."
  TEMP_BINARY="$INSTALL_DIR/gost.download"
  if ! curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$DOWNLOAD_URL" -o "$TEMP_BINARY"; then
    rm -f "$TEMP_BINARY"
    echo "❌ 下载失败，请检查网络或下载链接。"
    return 1
  fi
  if [[ ! -f "$TEMP_BINARY" || ! -s "$TEMP_BINARY" ]]; then
    rm -f "$TEMP_BINARY"
    echo "❌ 下载失败，请检查网络或下载链接。"
    return 1
  fi
  chmod +x "$TEMP_BINARY"
  if ! "$TEMP_BINARY" -V >/dev/null 2>&1; then
    rm -f "$TEMP_BINARY"
    echo "❌ 下载的 Agent 无法通过版本检查。"
    return 1
  fi
  ASSET_NAME="gost-$(get_architecture)"
  CHECKSUM_TEXT=""
  if ! CHECKSUM_TEXT=$(curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$CHECKSUMS_URL"); then
    rm -f "$TEMP_BINARY"
    echo "❌ 无法下载发布校验清单。"
    return 1
  fi
  EXPECTED_SHA=$(printf '%s\n' "$CHECKSUM_TEXT" | awk -v name="$ASSET_NAME" '$2 == name || $2 == "*" name {print $1; exit}')
  if [[ ! "$EXPECTED_SHA" =~ ^[A-Fa-f0-9]{64}$ ]]; then
    rm -f "$TEMP_BINARY"
    echo "❌ 发布清单中没有 $ASSET_NAME 的 SHA-256。"
    return 1
  fi
  ACTUAL_SHA=$(sha256_file "$TEMP_BINARY")
  if [[ "$(normalize_hash "$ACTUAL_SHA")" != "$(normalize_hash "$EXPECTED_SHA")" ]]; then
    rm -f "$TEMP_BINARY"
    echo "❌ Agent SHA-256 校验失败。"
    return 1
  fi

  # 写入 config.json (安装时总是创建新的)
  CONFIG_FILE="$INSTALL_DIR/config.json"
  SERVER_ADDR_JSON=$(json_escape "$SERVER_ADDR")
  SECRET_JSON=$(json_escape "$SECRET")
  UPDATE_MANIFEST_URL_JSON=$(json_escape "$UPDATE_MANIFEST_URL")
  echo "📄 创建新配置: config.json"
  cat > "$CONFIG_FILE" <<EOF
{
  "addr": "$SERVER_ADDR_JSON",
  "secret": "$SECRET_JSON",
  "autoUpdate": true,
  "updateManifestUrl": "$UPDATE_MANIFEST_URL_JSON"
}
EOF

  # 写入 gost.json
  GOST_CONFIG="$INSTALL_DIR/gost.json"
  if [[ -f "$GOST_CONFIG" ]]; then
    echo "⏭️ 跳过配置文件: gost.json (已存在)"
  else
    echo "📄 创建新配置: gost.json"
    cat > "$GOST_CONFIG" <<EOF
{}
EOF
  fi

  # 加强权限
  chmod 600 "$INSTALL_DIR"/*.json

  # 创建 systemd 服务
  SERVICE_FILE="/etc/systemd/system/gost.service"
  cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Gost Proxy Service
After=network.target

[Service]
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/gost
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

  # Stop the existing service only after the replacement and service config
  # have been prepared successfully.
  if systemctl list-units --full -all | grep -Fq "gost.service"; then
    echo "🔍 检测到已存在的gost服务"
    if ! systemctl stop gost 2>/dev/null; then
      echo "❌ 无法停止现有 gost 服务，已保留原服务。" >&2
      return 1
    fi
    echo "🛑 停止服务"
    if ! systemctl disable gost 2>/dev/null; then
      systemctl enable gost >/dev/null 2>&1 || true
      systemctl start gost >/dev/null 2>&1 || true
      echo "❌ 无法禁用现有 gost 服务，已恢复原服务。" >&2
      return 1
    fi
    echo "🚫 禁用自启"
  fi

  mv -f "$TEMP_BINARY" "$INSTALL_DIR/gost"
  chmod +x "$INSTALL_DIR/gost"
  echo "✅ 下载完成"
  echo "🔎 gost 版本：$($INSTALL_DIR/gost -V)"

  # 启动服务
  systemctl daemon-reload
  systemctl enable gost

  # 检查状态
  echo "🔄 检查服务状态..."
  if systemctl start gost && systemctl is-active --quiet gost; then
    echo "✅ 安装完成，gost服务已启动并设置为开机启动。"
    echo "📁 配置目录: $INSTALL_DIR"
    echo "🔧 服务状态: $(systemctl is-active gost)"
  else
    echo "❌ gost服务启动失败，请执行以下命令查看日志："
    echo "journalctl -u gost -f"
    return 1
  fi
}

# 更新功能
update_gost() {
  echo "🔄 开始更新 GOST..."

  if ! command -v systemctl >/dev/null 2>&1; then
    echo "❌ Linux Agent 需要 systemd/systemctl。"
    return 1
  fi

  if [[ ! -d "$INSTALL_DIR" ]]; then
    echo "❌ GOST 未安装，请先选择安装。"
    return 1
  fi

  echo "📥 使用下载地址: $DOWNLOAD_URL"

  # 先下载新版本
  echo "⬇️ 下载最新版本..."
  if ! curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$DOWNLOAD_URL" -o "$INSTALL_DIR/gost.new"; then
    rm -f "$INSTALL_DIR/gost.new"
    echo "❌ 下载失败。"
    return 1
  fi
  if [[ ! -f "$INSTALL_DIR/gost.new" || ! -s "$INSTALL_DIR/gost.new" ]]; then
    echo "❌ 下载失败。"
    return 1
  fi
  ASSET_NAME="gost-$(get_architecture)"
  CHECKSUM_TEXT=""
  if ! CHECKSUM_TEXT=$(curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$CHECKSUMS_URL"); then
    rm -f "$INSTALL_DIR/gost.new"
    echo "❌ 无法下载发布校验清单。"
    return 1
  fi
  EXPECTED_SHA=$(printf '%s\n' "$CHECKSUM_TEXT" | awk -v name="$ASSET_NAME" '$2 == name || $2 == "*" name {print $1; exit}')
  ACTUAL_SHA=$(sha256_file "$INSTALL_DIR/gost.new")
  if [[ ! "$EXPECTED_SHA" =~ ^[A-Fa-f0-9]{64}$ || "$(normalize_hash "$ACTUAL_SHA")" != "$(normalize_hash "$EXPECTED_SHA")" ]]; then
    rm -f "$INSTALL_DIR/gost.new"
    echo "❌ Agent SHA-256 校验失败。"
    return 1
  fi

  # 停止服务
  if systemctl list-units --full -all | grep -Fq "gost.service"; then
    echo "🛑 停止 gost 服务..."
    if ! systemctl stop gost; then
      echo "❌ 无法停止 gost 服务，取消更新。" >&2
      return 1
    fi
  fi

  # 替换文件
  PREVIOUS_BINARY="$INSTALL_DIR/gost.previous"
  if [[ -f "$INSTALL_DIR/gost" ]]; then
    mv -f "$INSTALL_DIR/gost" "$PREVIOUS_BINARY"
  fi
  if ! mv -f "$INSTALL_DIR/gost.new" "$INSTALL_DIR/gost"; then
    [[ -f "$PREVIOUS_BINARY" ]] && mv -f "$PREVIOUS_BINARY" "$INSTALL_DIR/gost"
    systemctl start gost >/dev/null 2>&1 || true
    echo "❌ 替换 Agent 文件失败，已恢复原版本。" >&2
    return 1
  fi
  chmod +x "$INSTALL_DIR/gost"

  # 打印版本
  echo "🔎 新版本：$($INSTALL_DIR/gost -V)"

  # 重启服务
  echo "🔄 重启服务..."
  if systemctl start gost && systemctl is-active --quiet gost; then
    rm -f "$PREVIOUS_BINARY"
    echo "✅ 更新完成，服务已重新启动。"
  else
    rm -f "$INSTALL_DIR/gost"
    if [[ -f "$PREVIOUS_BINARY" ]]; then
      mv -f "$PREVIOUS_BINARY" "$INSTALL_DIR/gost"
      chmod +x "$INSTALL_DIR/gost"
      systemctl start gost >/dev/null 2>&1 || true
    fi
    echo "❌ gost 服务启动失败，请执行以下命令查看日志："
    echo "journalctl -u gost -f"
    return 1
  fi
}

# 卸载功能
uninstall_gost() {
  echo "🗑️ 开始卸载 GOST..."

  read -p "确认卸载 GOST 吗？此操作将删除所有相关文件 (y/N): " confirm
  if [[ "$confirm" != "y" && "$confirm" != "Y" ]]; then
    echo "❌ 取消卸载"
    return 0
  fi

  # 停止并禁用服务 when systemd is available.
  if command -v systemctl >/dev/null 2>&1 && systemctl list-units --full -all | grep -Fq "gost.service"; then
    echo "🛑 停止并禁用服务..."
    systemctl stop gost 2>/dev/null || true
    systemctl disable gost 2>/dev/null || true
  fi

  # 删除服务文件
  if [[ -f "/etc/systemd/system/gost.service" ]]; then
    rm -f "/etc/systemd/system/gost.service"
    echo "🧹 删除服务文件"
  fi

  # 删除安装目录
  if [[ -d "$INSTALL_DIR" ]]; then
    rm -rf "$INSTALL_DIR"
    echo "🧹 删除安装目录: $INSTALL_DIR"
  fi

  # 重载 systemd
  if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload
  fi

  echo "✅ 卸载完成"
}

# 主逻辑
main() {
  # 如果提供了命令行参数，直接执行安装
  if [[ -n "$SERVER_ADDR" && -n "$SECRET" ]]; then
    install_gost
    delete_self
    exit 0
  fi

  # 显示交互式菜单
  while true; do
    show_menu
    read -p "请输入选项 (1-4): " choice

    case $choice in
      1)
        install_gost
        delete_self
        exit 0
        ;;
      2)
        update_gost
        delete_self
        exit 0
        ;;
      3)
        uninstall_gost
        delete_self
        exit 0
        ;;
      4)
        echo "👋 退出脚本"
        delete_self
        exit 0
        ;;
      *)
        echo "❌ 无效选项，请输入 1-4"
        echo ""
        ;;
    esac
  done
}

# 执行主函数
main
