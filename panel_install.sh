#!/bin/bash
set -e
umask 077

# 解决 macOS 下 tr 可能出现的非法字节序列问题
export LANG=en_US.UTF-8
export LC_ALL=C

SCRIPT_PATH="$(readlink -f "$0" 2>/dev/null || realpath "$0" 2>/dev/null || printf '%s' "$0")"
INSTALL_DIR="${RELAYFORGE_INSTALL_DIR:-/opt/relayforge}"
if [[ "$INSTALL_DIR" != /* ]]; then
  INSTALL_DIR="$(pwd)/$INSTALL_DIR"
fi



# 全局下载地址配置
RELEASE_BASE_URL="${RELAYFORGE_RELEASE_BASE_URL:-https://github.com/WayneDuan/RelayForge-Release/releases/latest/download}"
RELEASE_BASE_URL="${RELEASE_BASE_URL%/}"
case "$RELEASE_BASE_URL" in
  https://?*) ;;
  *)
    echo "错误：RELAYFORGE_RELEASE_BASE_URL 必须是 HTTPS 地址。" >&2
    exit 1
    ;;
esac
DOCKER_COMPOSEV4_URL="${RELEASE_BASE_URL}/docker-compose-v4.yml"
DOCKER_COMPOSEV6_URL="${RELEASE_BASE_URL}/docker-compose-v6.yml"
PANEL_INSTALL_URL="${RELEASE_BASE_URL}/panel_install.sh"
CHECKSUMS_URL="${RELEASE_BASE_URL}/checksums.txt"

if [[ -n "${RELAYFORGE_RELEASE_MIRROR:-}" ]]; then
  RELEASE_MIRROR="${RELAYFORGE_RELEASE_MIRROR%/}"
  DOCKER_COMPOSEV4_URL="${RELEASE_MIRROR}/${DOCKER_COMPOSEV4_URL}"
  DOCKER_COMPOSEV6_URL="${RELEASE_MIRROR}/${DOCKER_COMPOSEV6_URL}"
  PANEL_INSTALL_URL="${RELEASE_MIRROR}/${PANEL_INSTALL_URL}"
  CHECKSUMS_URL="${RELEASE_MIRROR}/${CHECKSUMS_URL}"
fi



# 根据IPv6支持情况选择docker-compose URL
get_docker_compose_url() {
  if check_ipv6_support > /dev/null 2>&1; then
    echo "$DOCKER_COMPOSEV6_URL"
  else
    echo "$DOCKER_COMPOSEV4_URL"
  fi
}

# 检查 docker-compose 或 docker compose 命令
check_docker() {
  if ! command -v curl &> /dev/null; then
    echo "错误：面板安装和更新需要 curl。"
    exit 1
  fi
  if command -v docker-compose &> /dev/null; then
    DOCKER_CMD="docker-compose"
  elif command -v docker &> /dev/null; then
    if docker compose version &> /dev/null; then
      DOCKER_CMD="docker compose"
    else
      echo "错误：检测到 docker，但不支持 'docker compose' 命令。请安装 docker-compose 或更新 docker 版本。"
      exit 1
    fi
  else
    echo "错误：未检测到 docker 或 docker-compose 命令。请先安装 Docker。"
    exit 1
  fi
  echo "检测到 Docker 命令：$DOCKER_CMD"
}

# 检测系统是否支持 IPv6
check_ipv6_support() {
  echo "🔍 检测 IPv6 支持..."

  # 检查是否有 IPv6 地址（排除 link-local 地址）
  if ip -6 addr show | grep -v "scope link" | grep -q "inet6"; then
    echo "✅ 检测到系统支持 IPv6"
    return 0
  elif ifconfig 2>/dev/null | grep -v "fe80:" | grep -q "inet6"; then
    echo "✅ 检测到系统支持 IPv6"
    return 0
  else
    echo "⚠️ 未检测到 IPv6 支持"
    return 1
  fi
}



# 配置 Docker 启用 IPv6
configure_docker_ipv6() {
  echo "🔧 配置 Docker IPv6 支持..."

  # 检查操作系统类型
  OS_TYPE=$(uname -s)

  if [[ "$OS_TYPE" == "Darwin" ]]; then
    # macOS 上 Docker Desktop 已默认支持 IPv6
    echo "✅ macOS Docker Desktop 默认支持 IPv6"
    return 0
  fi

  # Docker daemon 配置文件路径
  DOCKER_CONFIG="/etc/docker/daemon.json"

  # 检查是否需要 sudo
  if [[ $EUID -ne 0 ]]; then
    SUDO_CMD="sudo"
  else
    SUDO_CMD=""
  fi

  # 检查 Docker 配置文件
  if [ -f "$DOCKER_CONFIG" ]; then
    # 检查是否已经配置了 IPv6
    if grep -q '"ipv6"' "$DOCKER_CONFIG"; then
      echo "✅ Docker 已配置 IPv6 支持"
    else
      echo "📝 更新 Docker 配置以启用 IPv6..."
      # 备份原配置
      $SUDO_CMD cp "$DOCKER_CONFIG" "${DOCKER_CONFIG}.backup"

      # 使用 jq 或可移植的临时文件添加 IPv6 配置
      temporary="$(mktemp "${TMPDIR:-/tmp}/relayforge-daemon.XXXXXX")"
      if command -v jq &> /dev/null; then
        if ! $SUDO_CMD jq '. + {"ipv6": true, "fixed-cidr-v6": "fd00::/80"}' "$DOCKER_CONFIG" > "$temporary"; then
          rm -f "$temporary"
          echo "错误：Docker daemon.json 不是有效 JSON。" >&2
          return 1
        fi
      else
        if grep -Eq '^[[:space:]]*\{[[:space:]]*\}[[:space:]]*$' "$DOCKER_CONFIG"; then
          if ! printf '%s\n' '{' '  "ipv6": true,' '  "fixed-cidr-v6": "fd00::/80"' '}' > "$temporary"; then
            rm -f "$temporary"
            return 1
          fi
        elif ! $SUDO_CMD sed '1s/^[[:space:]]*{/&\n  "ipv6": true,\n  "fixed-cidr-v6": "fd00::\/80",/' "$DOCKER_CONFIG" > "$temporary"; then
          rm -f "$temporary"
          return 1
        fi
      fi
      $SUDO_CMD mv "$temporary" "$DOCKER_CONFIG"

      echo "🔄 重启 Docker 服务..."
      if command -v systemctl &> /dev/null; then
        $SUDO_CMD systemctl restart docker
      elif command -v service &> /dev/null; then
        $SUDO_CMD service docker restart
      else
        echo "⚠️ 请手动重启 Docker 服务"
      fi
      sleep 5
    fi
  else
    # 创建新的配置文件
    echo "📝 创建 Docker 配置文件..."
    $SUDO_CMD mkdir -p /etc/docker
    echo '{
  "ipv6": true,
  "fixed-cidr-v6": "fd00::/80"
}' | $SUDO_CMD tee "$DOCKER_CONFIG" > /dev/null

    echo "🔄 重启 Docker 服务..."
    if command -v systemctl &> /dev/null; then
      $SUDO_CMD systemctl restart docker
    elif command -v service &> /dev/null; then
      $SUDO_CMD service docker restart
    else
      echo "⚠️ 请手动重启 Docker 服务"
    fi
    sleep 5
  fi
}

# 显示菜单
show_menu() {
  echo "==============================================="
  echo "          面板管理脚本"
  echo "==============================================="
  echo "请选择操作："
  echo "1. 安装面板"
  echo "2. 更新面板"
  echo "3. 卸载面板"
  echo "4. 导出备份"
  echo "5. 退出"
  echo "==============================================="
}

generate_random() {
  local length="${1:-48}"
  LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c"$length"
}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    echo "错误：需要 sha256sum 或 shasum 才能校验发布文件。" >&2
    return 1
  fi
}

normalize_hash() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

download_verified_asset() {
  local url="$1"
  local target="$2"
  local asset_name
  local expected
  local actual
  local temporary
  asset_name="$(basename "${url%%\?*}")"
  if ! temporary="$(mktemp "${target}.download.XXXXXX")"; then
    echo "错误：无法创建 $asset_name 的临时文件。" >&2
    return 1
  fi
  if ! curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$CHECKSUMS_URL" -o "${target}.checksums"; then
    rm -f "${target}.checksums" "$temporary"
    echo "错误：无法下载发布校验清单。" >&2
    return 1
  fi
  expected="$(awk -v name="$asset_name" '$2 == name || $2 == "*" name {print $1; exit}' "${target}.checksums")"
  rm -f "${target}.checksums"
  if [[ ! "$expected" =~ ^[A-Fa-f0-9]{64}$ ]]; then
    rm -f "$temporary"
    echo "错误：发布清单中没有 $asset_name 的 SHA-256。" >&2
    return 1
  fi
  if ! curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$url" -o "$temporary"; then
    rm -f "$temporary"
    echo "错误：无法下载 $asset_name。" >&2
    return 1
  fi
  actual="$(sha256_file "$temporary")"
  if [[ "$(normalize_hash "$actual")" != "$(normalize_hash "$expected")" ]]; then
    rm -f "$temporary"
    echo "错误：$asset_name 的 SHA-256 校验失败。" >&2
    return 1
  fi
  mv -f "$temporary" "$target"
}

# Keep the installer available for inspection and safe when invoked through a
# shell pipe. The installed management script is refreshed separately.
delete_self() {
  return 0
}

ensure_install_dir() {
  if [[ ! -d "$INSTALL_DIR" ]]; then
    if ! mkdir -p "$INSTALL_DIR" 2>/dev/null; then
      if command -v sudo &> /dev/null; then
        sudo mkdir -p "$INSTALL_DIR"
        sudo chown "$(id -u):$(id -g)" "$INSTALL_DIR"
      else
        echo "错误：无法创建安装目录 $INSTALL_DIR，请使用 root 或设置 RELAYFORGE_INSTALL_DIR。"
        return 1
      fi
    fi
  fi

  if [[ ! -w "$INSTALL_DIR" ]]; then
    echo "错误：安装目录不可写：$INSTALL_DIR"
    return 1
  fi

  cd "$INSTALL_DIR"
  echo "安装目录：$INSTALL_DIR"
}

copy_management_script() {
  local target="$INSTALL_DIR/panel_install.sh"
  local refreshed="${target}.refresh"

  if [[ "$SCRIPT_PATH" == "$target" ]]; then
    # Refresh the installed script for the next invocation without replacing
    # the file that the current shell is executing.
    if download_verified_asset "$PANEL_INSTALL_URL" "$refreshed"; then
      mv -f "$refreshed" "$target"
      chmod +x "$target"
      echo "已检查并更新面板管理脚本。"
    else
      rm -f "$refreshed" "${refreshed}.download"
      echo "⚠️ 无法更新面板管理脚本，继续使用当前版本。" >&2
    fi
  elif [[ -f "$SCRIPT_PATH" ]]; then
    cp "$SCRIPT_PATH" "$target"
    chmod +x "$target"
  elif [[ ! -f "$SCRIPT_PATH" ]]; then
    download_verified_asset "$PANEL_INSTALL_URL" "$target"
    chmod +x "$target"
  fi
}

prepare_workspace() {
  ensure_install_dir
  copy_management_script
}



# 获取用户输入的配置参数
get_config_params() {
  if [[ -f ".env" ]]; then
    DB_NAME=$(grep "^DB_NAME=" .env | cut -d'=' -f2- || true)
    DB_USER=$(grep "^DB_USER=" .env | cut -d'=' -f2- || true)
    DB_PASSWORD=$(grep "^DB_PASSWORD=" .env | cut -d'=' -f2- || true)
    DB_ROOT_PASSWORD=$(grep "^DB_ROOT_PASSWORD=" .env | cut -d'=' -f2- || true)
    DB_SSL_MODE=$(grep "^DB_SSL_MODE=" .env | cut -d'=' -f2- || true)
    JWT_SECRET=$(grep "^JWT_SECRET=" .env | cut -d'=' -f2- || true)
    INTEGRATION_ENCRYPTION_KEY=$(grep "^INTEGRATION_ENCRYPTION_KEY=" .env | cut -d'=' -f2- || true)
    FRONTEND_PORT=$(grep "^FRONTEND_PORT=" .env | cut -d'=' -f2- || true)
    BACKEND_PORT=$(grep "^BACKEND_PORT=" .env | cut -d'=' -f2- || true)
    BACKEND_BIND_ADDRESS=$(grep "^BACKEND_BIND_ADDRESS=" .env | cut -d'=' -f2- || true)

    if (( ${#DB_USER} > 32 )); then
      DB_USER="${DB_USER:0:32}"
      sed -i "s/^DB_USER=.*/DB_USER=$DB_USER/" .env
      echo "检测到数据库用户名超过 MySQL 的 32 位限制，已截取为 32 位。"
    fi

    if [[ -n "$DB_NAME" && -n "$DB_USER" && -n "$DB_PASSWORD" && -n "$JWT_SECRET" ]]; then
      DB_ROOT_PASSWORD=${DB_ROOT_PASSWORD:-$DB_PASSWORD}
      INTEGRATION_ENCRYPTION_KEY=${INTEGRATION_ENCRYPTION_KEY:-$JWT_SECRET}
      grep -q '^DB_ROOT_PASSWORD=' .env || printf 'DB_ROOT_PASSWORD=%s\n' "$DB_ROOT_PASSWORD" >> .env
      grep -q '^INTEGRATION_ENCRYPTION_KEY=' .env || printf 'INTEGRATION_ENCRYPTION_KEY=%s\n' "$INTEGRATION_ENCRYPTION_KEY" >> .env
      FRONTEND_PORT=${FRONTEND_PORT:-6311}
      BACKEND_PORT=${BACKEND_PORT:-6315}
      BACKEND_BIND_ADDRESS=${BACKEND_BIND_ADDRESS:-127.0.0.1}
      DB_SSL_MODE=${DB_SSL_MODE:-Preferred}
      echo "检测到已有配置，继续使用固定目录中的数据库凭据。"
      return 0
    fi
  fi

  echo "🔧 请输入配置参数："



  read -p "前端端口（默认 6311）: " FRONTEND_PORT
  FRONTEND_PORT=${FRONTEND_PORT:-6311}

  read -p "后端端口（默认 6315）: " BACKEND_PORT
  BACKEND_PORT=${BACKEND_PORT:-6315}

  read -p "后端绑定地址（默认 127.0.0.1；节点直连请填写 0.0.0.0）: " BACKEND_BIND_ADDRESS
  BACKEND_BIND_ADDRESS=${BACKEND_BIND_ADDRESS:-127.0.0.1}

  DB_NAME=$(generate_random 48)
  DB_USER=$(generate_random 32)
  DB_PASSWORD=$(generate_random)
  DB_ROOT_PASSWORD=$(generate_random)
  JWT_SECRET=$(generate_random)
  INTEGRATION_ENCRYPTION_KEY=$(generate_random)
}

validate_runtime_config() {
  if ! [[ "$FRONTEND_PORT" =~ ^[0-9]+$ ]] || (( FRONTEND_PORT < 1 || FRONTEND_PORT > 65535 )); then
    echo "错误：前端端口必须是 1-65535。" >&2
    return 1
  fi
  if ! [[ "$BACKEND_PORT" =~ ^[0-9]+$ ]] || (( BACKEND_PORT < 1 || BACKEND_PORT > 65535 )); then
    echo "错误：后端端口必须是 1-65535。" >&2
    return 1
  fi
  if [[ "$FRONTEND_PORT" == "$BACKEND_PORT" ]]; then
    echo "错误：前端端口和后端端口不能相同。" >&2
    return 1
  fi
  if [[ -z "$BACKEND_BIND_ADDRESS" || ! "$BACKEND_BIND_ADDRESS" =~ ^[A-Za-z0-9_.:-]+$ ]]; then
    echo "错误：后端绑定地址包含非法字符。" >&2
    return 1
  fi
}

remove_initial_admin_config() {
  [[ -f .env ]] || return 0
  local temporary=".env.cleanup.$$"
  sed '/^INITIAL_ADMIN_USERNAME=/d; /^INITIAL_ADMIN_PASSWORD_B64=/d; /^ADMIN_CONFIGURED=/d' .env > "$temporary"
  mv -f "$temporary" .env
}

# 安装功能
get_admin_config() {
  read -p "管理员用户名（默认 admin_user）: " ADMIN_USERNAME
  ADMIN_USERNAME=${ADMIN_USERNAME:-admin_user}

  if [[ ! "$ADMIN_USERNAME" =~ ^[A-Za-z0-9_.-]+$ ]]; then
    echo "错误：管理员用户名只能包含字母、数字、点、下划线和短横线。"
    return 1
  fi

  while true; do
    read -r -s -p "管理员密码（至少 8 位）: " ADMIN_PASSWORD
    echo ""
    read -r -s -p "确认管理员密码: " ADMIN_PASSWORD_CONFIRM
    echo ""

    if [[ ${#ADMIN_PASSWORD} -lt 8 ]]; then
      echo "错误：管理员密码至少需要 8 位。"
    elif [[ "$ADMIN_PASSWORD" != "$ADMIN_PASSWORD_CONFIRM" ]]; then
      echo "错误：两次输入的管理员密码不一致。"
    elif [[ "$ADMIN_PASSWORD" == *$'\n'* || "$ADMIN_PASSWORD" == *$'\r'* ]]; then
      echo "错误：管理员密码不能包含换行符。"
    else
      return 0
    fi
  done
}

wait_for_database() {
  for i in {1..60}; do
    DB_CONTAINER=$($DOCKER_CMD ps -q mysql 2>/dev/null || true)
    if [[ -n "$DB_CONTAINER" ]]; then
      DB_HEALTH=$(docker inspect -f '{{.State.Health.Status}}' "$DB_CONTAINER" 2>/dev/null || echo "unknown")
      if [[ "$DB_HEALTH" == "healthy" ]]; then
        return 0
      fi
    fi
    sleep 1
  done

  echo "错误：数据库服务启动超时。"
  $DOCKER_CMD logs --tail 50 mysql 2>&1 || true
  return 1
}

sql_escape_string() {
  printf '%s' "$1" | sed "s/'/''/g"
}

sql_escape_identifier() {
  printf '%s' "$1" | sed 's/`/``/g'
}

ensure_database_user() {
  local sql_user sql_password sql_database
  sql_user=$(sql_escape_string "$DB_USER")
  sql_password=$(sql_escape_string "$DB_PASSWORD")
  sql_database=$(sql_escape_identifier "$DB_NAME")

  if ! $DOCKER_CMD exec -T mysql mysql -uroot -p"$DB_ROOT_PASSWORD" <<SQL
CREATE USER IF NOT EXISTS '$sql_user'@'%' IDENTIFIED WITH caching_sha2_password BY '$sql_password';
ALTER USER '$sql_user'@'%' IDENTIFIED WITH caching_sha2_password BY '$sql_password';
GRANT ALL PRIVILEGES ON \`$sql_database\`.* TO '$sql_user'@'%';
FLUSH PRIVILEGES;
SQL
  then
    echo "错误：无法创建或更新数据库业务账号。" >&2
    return 1
  fi
}

wait_for_backend() {
  for i in {1..90}; do
    if curl -fsS "http://127.0.0.1:${BACKEND_PORT}/health" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done

  echo "错误：后端服务启动超时。"
  $DOCKER_CMD logs --tail 50 backend 2>&1 || true
  return 1
}

install_panel() {
  echo "🚀 开始安装面板..."
  check_docker
  prepare_workspace
  get_config_params
  get_admin_config
  validate_runtime_config

  echo "🔽 下载必要文件..."
  DOCKER_COMPOSE_URL=$(get_docker_compose_url)
  echo "📡 选择配置文件：$(basename "$DOCKER_COMPOSE_URL")"
  download_verified_asset "$DOCKER_COMPOSE_URL" docker-compose.yml

  echo "✅ 文件准备完成"

  echo "📦 拉取 RelayForge Docker 镜像..."
  cat > .env <<EOF
DB_NAME=$DB_NAME
DB_USER=$DB_USER
DB_PASSWORD=$DB_PASSWORD
DB_ROOT_PASSWORD=$DB_ROOT_PASSWORD
DB_SSL_MODE=${DB_SSL_MODE:-Preferred}
JWT_SECRET=$JWT_SECRET
INTEGRATION_ENCRYPTION_KEY=$INTEGRATION_ENCRYPTION_KEY
FRONTEND_PORT=$FRONTEND_PORT
BACKEND_PORT=$BACKEND_PORT
BACKEND_BIND_ADDRESS=$BACKEND_BIND_ADDRESS
PANEL_REQUIRE_HTTPS=false
INITIAL_ADMIN_USERNAME=$ADMIN_USERNAME
INITIAL_ADMIN_PASSWORD_B64=$(printf '%s' "$ADMIN_PASSWORD" | base64 | tr -d '\n')
EOF
  chmod 600 .env

  $DOCKER_CMD pull
  echo "✅ Docker 镜像拉取完成"

  # 自动检测并配置 IPv6 支持
  if check_ipv6_support; then
    echo "🚀 系统支持 IPv6，自动启用 IPv6 配置..."
    configure_docker_ipv6
  fi


  echo "🚀 启动 docker 服务..."
  $DOCKER_CMD up -d

  echo "等待数据库启动并创建管理员账号..."
  wait_for_database
  ensure_database_user
  wait_for_backend
  remove_initial_admin_config
  printf '\nADMIN_CONFIGURED=1\n' >> .env
  trap - EXIT

  echo "🎉 部署完成"
  echo "🌐 访问地址: http://服务器IP:$FRONTEND_PORT"
  echo "📖 部署完成后请阅读下使用文档，求求了啊，不要上去就是一顿操作"
  echo "📚 文档: RelayForge 铸流私人转发面板"
  echo "💡 管理员账号: $ADMIN_USERNAME"


}

restore_panel_compose() {
  if [[ -n "${PANEL_COMPOSE_BACKUP:-}" && -f "$PANEL_COMPOSE_BACKUP" ]]; then
    mv -f "$PANEL_COMPOSE_BACKUP" docker-compose.yml
    $DOCKER_CMD up -d --no-deps backend frontend >/dev/null 2>&1 || true
  fi
  PANEL_COMPOSE_BACKUP=""
}

# 更新功能
update_panel() {
  echo "🔄 开始更新面板..."
  check_docker
  prepare_workspace

  if [[ -f docker-compose.yml ]] && grep -q 'mysql:5\.7' docker-compose.yml; then
    echo "❌ 检测到 MySQL 5.7。请先执行 ./panel_install.sh export，并按 README 的 MySQL 迁移步骤完成备份恢复后再更新。"
    return 1
  fi

  if [[ ! -f .env ]]; then
    echo "错误：未找到 .env，无法安全更新现有面板。请使用 install 重新初始化。" >&2
    return 1
  fi
  get_config_params
  validate_runtime_config

  echo "🔽 下载最新配置文件..."
  DOCKER_COMPOSE_URL=$(get_docker_compose_url)
  echo "📡 选择配置文件：$(basename "$DOCKER_COMPOSE_URL")"
  PANEL_COMPOSE_BACKUP=""
  if [[ -f docker-compose.yml ]]; then
    if ! PANEL_COMPOSE_BACKUP="$(mktemp "${INSTALL_DIR}/docker-compose.yml.previous.XXXXXX")"; then
      echo "错误：无法备份现有 docker-compose.yml。" >&2
      return 1
    fi
    if ! cp docker-compose.yml "$PANEL_COMPOSE_BACKUP"; then
      rm -f "$PANEL_COMPOSE_BACKUP"
      PANEL_COMPOSE_BACKUP=""
      echo "错误：无法备份现有 docker-compose.yml。" >&2
      return 1
    fi
  fi
  if ! download_verified_asset "$DOCKER_COMPOSE_URL" docker-compose.yml; then
    if [[ -n "${PANEL_COMPOSE_BACKUP:-}" ]]; then
      rm -f "$PANEL_COMPOSE_BACKUP"
    fi
    PANEL_COMPOSE_BACKUP=""
    return 1
  fi
  echo "✅ 下载完成"

  # 更新只处理容器和镜像，数据库结构由后端代码负责兼容。
  if check_ipv6_support; then
    echo "🚀 系统支持 IPv6，自动启用 IPv6 配置..."
    if ! configure_docker_ipv6; then
      restore_panel_compose
      return 1
    fi
  fi

  echo "🗄️ 确保数据库服务运行..."
  if ! $DOCKER_CMD up -d mysql; then
    restore_panel_compose
    return 1
  fi
  if ! wait_for_database || ! ensure_database_user; then
    restore_panel_compose
    return 1
  fi

  echo "⬇️ 拉取前后端最新镜像..."
  if ! $DOCKER_CMD pull backend frontend; then
    restore_panel_compose
    return 1
  fi

  echo "🚀 更新前后端服务..."
  if ! $DOCKER_CMD up -d --no-deps backend frontend; then
    restore_panel_compose
    return 1
  fi

  echo "⏳ 等待后端服务健康..."
  for i in {1..90}; do
    BACKEND_CONTAINER=$($DOCKER_CMD ps -q backend 2>/dev/null || true)
    if [[ -n "$BACKEND_CONTAINER" ]]; then
      BACKEND_HEALTH=$(docker inspect -f '{{.State.Health.Status}}' "$BACKEND_CONTAINER" 2>/dev/null || echo "unknown")
      if [[ "$BACKEND_HEALTH" == "healthy" ]]; then
        if [[ -n "$PANEL_COMPOSE_BACKUP" ]]; then
          rm -f "$PANEL_COMPOSE_BACKUP"
        fi
        PANEL_COMPOSE_BACKUP=""
        echo "✅ 后端服务健康检查通过"
        echo "✅ 更新完成"
        return 0
      fi
    else
      BACKEND_HEALTH="not_running"
    fi
    if [ $i -eq 90 ]; then
      echo "❌ 后端服务启动超时（90秒）"
      echo "🔍 当前状态: $($DOCKER_CMD ps backend 2>/dev/null || echo '容器不存在')"
      echo "🛑 更新终止"
      restore_panel_compose
      return 1
    fi
    if [ $((i % 15)) -eq 1 ]; then
      echo "⏳ 等待后端服务启动... ($i/90) 状态：${BACKEND_HEALTH:-unknown}"
    fi
    sleep 1
  done
}

# 导出数据库备份
export_database_backup() {
  check_docker
  prepare_workspace
  echo "📄 开始导出数据库备份..."

  # 获取数据库配置信息
  echo "🔍 获取数据库配置信息..."

  # 先检查后端容器是否在运行
  if [[ -z "$($DOCKER_CMD ps -q backend 2>/dev/null || true)" ]]; then
    echo "❌ 后端容器未运行，尝试从 .env 文件读取配置..."

    # 从 .env 文件读取配置
    if [[ -f ".env" ]]; then
      DB_NAME=$(grep "^DB_NAME=" .env | cut -d'=' -f2 2>/dev/null)
      DB_PASSWORD=$(grep "^DB_PASSWORD=" .env | cut -d'=' -f2 2>/dev/null)
      DB_USER=$(grep "^DB_USER=" .env | cut -d'=' -f2 2>/dev/null)
      DB_ROOT_PASSWORD=$(grep "^DB_ROOT_PASSWORD=" .env | cut -d'=' -f2 2>/dev/null)

      if [[ -n "$DB_NAME" && -n "$DB_PASSWORD" && -n "$DB_USER" ]]; then
        echo "✅ 从 .env 文件读取数据库配置成功"
      else
        echo "❌ .env 文件中的数据库配置不完整"
        return 1
      fi
    else
      echo "❌ 未找到 .env 文件"
      return 1
    fi
  else
    # 从容器环境变量获取数据库信息
    DB_INFO=$($DOCKER_CMD exec backend env | grep "^DB_" 2>/dev/null || echo "")

    if [[ -n "$DB_INFO" ]]; then
      DB_NAME=$(echo "$DB_INFO" | grep "^DB_NAME=" | cut -d'=' -f2)
      DB_PASSWORD=$(echo "$DB_INFO" | grep "^DB_PASSWORD=" | cut -d'=' -f2)
      DB_USER=$(echo "$DB_INFO" | grep "^DB_USER=" | cut -d'=' -f2)

      echo "✅ 从容器环境变量读取数据库配置成功"
    else
      echo "❌ 无法从容器获取数据库配置，尝试从 .env 文件读取..."

      if [[ -f ".env" ]]; then
        DB_NAME=$(grep "^DB_NAME=" .env | cut -d'=' -f2 2>/dev/null)
        DB_PASSWORD=$(grep "^DB_PASSWORD=" .env | cut -d'=' -f2 2>/dev/null)
        DB_USER=$(grep "^DB_USER=" .env | cut -d'=' -f2 2>/dev/null)

        if [[ -n "$DB_NAME" && -n "$DB_PASSWORD" && -n "$DB_USER" ]]; then
          echo "✅ 从 .env 文件读取数据库配置成功"
        else
          echo "❌ .env 文件中的数据库配置不完整"
          return 1
        fi
      else
        echo "❌ 未找到 .env 文件"
        return 1
      fi
    fi
  fi

  # 检查必要的数据库配置
  if [[ -z "$DB_PASSWORD" || -z "$DB_USER" || -z "$DB_NAME" ]]; then
    echo "❌ 数据库配置不完整（缺少必要参数）"
    return 1
  fi

  echo "📋 数据库配置："
  echo "   数据库名: $DB_NAME"
  echo "   用户名: $DB_USER"

  # 检查数据库容器是否运行
  if [[ -z "$($DOCKER_CMD ps -q mysql 2>/dev/null || true)" ]]; then
    echo "❌ 数据库容器未运行，无法导出数据"
    echo "🔍 当前运行的容器："
    docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
    return 1
  fi

  # 生成数据库备份文件
  SQL_FILE="database_backup_$(date +%Y%m%d_%H%M%S).sql"
  echo "📝 导出数据库备份: $SQL_FILE"

  # 使用 mysqldump 导出数据库
  echo "⏳ 正在导出数据库..."
  if $DOCKER_CMD exec mysql mysqldump -u "$DB_USER" -p"$DB_PASSWORD" --single-transaction --routines --triggers "$DB_NAME" > "$SQL_FILE" 2>/dev/null; then
    echo "✅ 数据库导出成功"
  else
    echo "⚠️ 使用用户密码失败，尝试root密码..."
    DB_ROOT_PASSWORD=${DB_ROOT_PASSWORD:-$DB_PASSWORD}
    if $DOCKER_CMD exec mysql mysqldump -u root -p"$DB_ROOT_PASSWORD" --single-transaction --routines --triggers "$DB_NAME" > "$SQL_FILE" 2>/dev/null; then
      echo "✅ 数据库导出成功"
    else
      echo "❌ 数据库导出失败"
      rm -f "$SQL_FILE"
      return 1
    fi
  fi

  # 检查文件大小
  if [[ -f "$SQL_FILE" ]] && [[ -s "$SQL_FILE" ]]; then
    FILE_SIZE=$(du -h "$SQL_FILE" | cut -f1)
    echo "📁 文件位置: $(pwd)/$SQL_FILE"
    echo "📊 文件大小: $FILE_SIZE"
  else
    echo "❌ 导出的文件为空或不存在"
    rm -f "$SQL_FILE"
    return 1
  fi
}


# 卸载功能
uninstall_panel() {
  prepare_workspace
  echo "🗑️ 开始卸载面板..."
  check_docker

  if [[ ! -f "docker-compose.yml" ]]; then
    echo "⚠️ 未找到 docker-compose.yml 文件，正在下载以完成卸载..."
    DOCKER_COMPOSE_URL=$(get_docker_compose_url)
    echo "📡 选择配置文件：$(basename "$DOCKER_COMPOSE_URL")"
    download_verified_asset "$DOCKER_COMPOSE_URL" docker-compose.yml
    echo "✅ docker-compose.yml 下载完成"
  fi

  read -p "确认卸载面板吗？此操作将停止并删除所有容器和数据 (y/N): " confirm
  if [[ "$confirm" != "y" && "$confirm" != "Y" ]]; then
    echo "❌ 取消卸载"
    return 0
  fi

  echo "🛑 停止并删除容器、镜像、卷..."
  $DOCKER_CMD down --rmi all --volumes --remove-orphans
  echo "🧹 删除配置文件..."
  rm -f docker-compose.yml gost.sql .env
  echo "✅ 卸载完成"
}

# 主逻辑
main() {
  case "${1:-install}" in
    install)
      if [[ -f "$INSTALL_DIR/.env" && -f "$INSTALL_DIR/docker-compose.yml" ]] && grep -q '^ADMIN_CONFIGURED=1$' "$INSTALL_DIR/.env"; then
        update_panel
      else
        install_panel
      fi
      return
      ;;
    update)
      update_panel
      return
      ;;
    uninstall)
      uninstall_panel
      return
      ;;
    export)
      export_database_backup
      return
      ;;
    menu)
      ;;
    *)
      echo "用法：$0 [install|update|uninstall|export|menu]"
      return 1
      ;;
  esac

  # 显示交互式菜单
  while true; do
    show_menu
    read -p "请输入选项 (1-5): " choice

    case $choice in
      1)
        install_panel
        delete_self
        exit 0
        ;;
      2)
        update_panel
        delete_self
        exit 0
        ;;
      3)
        uninstall_panel
        delete_self
        exit 0
        ;;
      4)
        export_database_backup
        delete_self
        exit 0
        ;;
      5)
        echo "👋 退出脚本"
        delete_self
        exit 0
        ;;
      *)
        echo "❌ 无效选项，请输入 1-5"
        echo ""
        ;;
    esac
  done
}

# 执行主函数
main "$@"
