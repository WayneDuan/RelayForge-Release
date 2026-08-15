# RelayForge / 铸流

RelayForge 是面向自有节点、端口转发和内网穿透场景的转发控制面板。它把节点 Agent、隧道拓扑和转发服务统一到一个控制面中，适合个人基础设施、小型团队网络和远程维护场景。

RelayForge 以免费、自部署、可审计为目标开放源码。Docker 镜像、Compose 文件、节点二进制和安装脚本可以从 Release 仓库获取，也可以由使用者自行构建。

> RelayForge 只应部署在你拥有或获得明确授权的网络、节点和目标服务上。使用者需要自行遵守所在地区法律法规、云厂商条款和目标服务的访问策略。

## 能力概览

### 面板控制台

- 总览页展示节点在线率、运行中的转发、累计上下行流量、节点流量排名和转发流量排名。
- 节点管理支持创建、编辑、删除、状态刷新和安装命令生成。
- 转发服务支持创建、编辑、删除、批量导入、暂停、恢复、端口调整和目标诊断。
- 隧道编排支持查看链路拓扑、修改运行参数、同步节点配置、诊断链路和删除隧道。
- 面板设置支持保存面板主机名/IP、前端端口、后端端口和 WSS 端口；这些设置保存在服务端数据库中。
- 管理操作通过 WebSocket 接收节点状态、CPU、内存和网络流量信息，页面无需依赖浏览器缓存才能恢复面板地址。

### 节点与 Agent

- Agent 基于 Go GOST，支持 Linux amd64、Linux arm64 和 Windows amd64 发布包。
- Linux 安装脚本创建 /etc/gost 和 gost.service，Windows 安装脚本创建 RelayForgeAgent Windows 服务。
- Agent 会向面板上报版本、在线状态、CPU、内存、运行时间和网络收发流量。
- 面板可以通过加密 WebSocket 下发服务、链路、限速器、暂停/恢复和诊断命令。
- 新版 Agent 默认自动升级：启动约 30 秒后首次检查，之后每 6 小时检查一次；下载后必须通过 SHA-256 校验才会替换并重启。
- 生产 Agent 默认使用 WSS；本地 Compose 示例可通过 `PANEL_REQUIRE_HTTPS=false` 使用 HTTP/WS，公网部署应使用 HTTPS/WSS。

### 隧道与转发

面板中的资源关系是：节点组成隧道，隧道承载转发服务，转发服务把入口端口映射到一个或多个目标地址。

| 隧道类型 | 数据路径 | 适用场景 |
| --- | --- | --- |
| 直连出口 | 入口节点 -> 目标地址 | 目标可由入口节点直接访问 |
| 中继隧道 | 入口节点 -> 出口节点 -> 目标地址 | 入口和目标之间需要经过另一台节点 |
| 内网反向中继 | 公网入口节点 <- 内网节点主动连接 -> 内网目标 | 内网 Windows 或其他无公网入口机器 |

隧道支持以下运行参数：

- 流量按单向或双向统计，并可设置隧道总流量上限。
- 流量统计倍率，例如 1.5 表示按实际流量的 1.5 倍计入统计。
- 隧道级限速，单位为 KB/s。
- TCP/UDP 监听地址和可选绑定网卡。
- TLS、TCP、AnyTLS 和 QUIC 协议。AnyTLS 需要设置密码；直连服务固定使用 TLS 逻辑。

转发服务支持：

- TCP 和 UDP 转发；AnyTLS 模式只生成 TCP 服务。
- 目标地址使用逗号分隔时，可以配置多个后端目标。
- fifo 故障切换、round 轮询、random 随机和 hash 固定来源四种目标选择策略。
- 手动填写入口端口，或根据节点端口池自动分配端口。
- 中继内部端口、网卡绑定、单条转发流量上限和运行状态管理。
- 流量达到单条转发或隧道上限后自动暂停；提高额度后可恢复服务。
- TCP 连通性、平均连接耗时和失败率诊断；隧道诊断还会检查节点在线状态、入口到出口链路和出口访问 www.cloudflare.com:443 的能力。

## 架构

~~~text
浏览器
          |
          v
React + Vite + Nginx  ------ /api/v1、/flow、/system-info ------> ASP.NET Core API
                                                                    |
                                                                    v
                                                               MySQL 8.4+
                                                                    ^
                                                                    |
                         AES-256-GCM 加密 WebSocket               |
             Linux / Windows Go GOST Agent <-----------------------+
                         |
                  真实 TCP/UDP 转发
~~~

组件说明：

| 目录/组件 | 作用 |
| --- | --- |
| vite-frontend | React 18 + Vite 控制台，生产环境由 Nginx 提供静态资源并代理 API/WebSocket |
| dotnet-backend | ASP.NET Core .NET 10 控制面、JWT 会话、数据库访问和节点网关 |
| go-gost | 基于 Go GOST 的节点 Agent、转发运行时、状态/流量上报和自动升级 |
| doc/windows-rdp-forwarding.md | Windows 多端口转发示例和操作步骤 |
| docker-compose-v4.yml | Docker IPv4 部署配置 |
| docker-compose-v6.yml | Docker IPv6 部署配置 |

代码按运行时和职责分层。后端的 `Api` 只负责路由、鉴权和协议适配，`Application` 负责节点、转发、流量等业务用例，`Infrastructure` 负责 MySQL、外部 3x-ui 和通知实现；前端的 `api`、`types` 和页面功能域分别承担 HTTP 传输、数据契约和交互状态。详细边界见 [docs/architecture.md](docs/architecture.md)。

## 部署

### 环境要求

面板服务器需要：

- Docker Engine 和 Docker Compose plugin，或兼容的 docker-compose 命令。
- 可访问 Docker Hub 和 Release 下载地址的网络环境。
- 对外开放前端端口；默认是 6311。生产环境建议只对外开放 TLS 反向代理。
- 节点能够访问面板 WebSocket 地址。Compose 默认只把后端绑定到 `127.0.0.1`，生产环境建议通过 TLS 反向代理转发 `/system-info`；若使用节点直连，请把 `BACKEND_BIND_ADDRESS` 改为 `0.0.0.0`，并只在防火墙中对节点 IP 放行 `BACKEND_PORT`。
- 不要把 MySQL 端口暴露到公网。

节点服务器需要：

- Linux：systemd、amd64 或 arm64，并具备 root 权限。
- Windows：64 位系统、管理员 PowerShell，并允许创建 Windows 服务。
- 节点到面板的出站网络连接。
- 入口端口、中继端口和目标服务端口按照拓扑配置防火墙规则。

### 使用安装脚本

推荐先下载并检查发布脚本，再在面板服务器上执行：

~~~bash
curl --fail --location --proto '=https' --tlsv1.2 -o panel_install.sh https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/panel_install.sh
bash -c 'curl --fail --location --proto "=https" --tlsv1.2 -o checksums.txt https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/checksums.txt && grep "  panel_install.sh$" checksums.txt | sha256sum -c -'
bash panel_install.sh
~~~

首次安装会询问：

1. 前端端口，默认 6311。
2. 后端端口，默认 6315。
3. 管理员用户名，默认 admin_user。
4. 管理员密码，至少 8 位。

安装脚本会：

- 自动判断主机是否支持 IPv6，并选择 docker-compose-v4.yml 或 docker-compose-v6.yml。
- 生成随机数据库名、数据库用户、业务数据库密码、独立 root 密码、JWT 密钥和集成加密密钥。
- 拉取固定版本的 `wayneduan/relayforge-frontend`、`wayneduan/relayforge-backend` 和 MySQL 8.4 镜像，并在替换前校验发布文件 SHA-256。
- 在后端首次启动时创建数据库表和管理员账号。
- 将运行文件集中保存到安装目录，默认是 /opt/relayforge。

安装完成后打开：

~~~text
http://面板服务器地址:6311
~~~

后端健康检查：

~~~bash
curl http://127.0.0.1:6315/health
~~~

正常响应为类似以下内容：

~~~json
{"status":"ok"}
~~~

### 手动 Docker Compose

如果不使用安装脚本，需要先创建 .env。空数据库首次启动时，INITIAL_ADMIN_USERNAME 和 INITIAL_ADMIN_PASSWORD_B64 必须存在，密码必须是 Base64 编码且原文至少 8 位。

~~~dotenv
DB_NAME=relayforge
DB_USER=relayforge
DB_PASSWORD=请替换为强密码
DB_ROOT_PASSWORD=请替换为不同的 root 密码
DB_SSL_MODE=Preferred
JWT_SECRET=请替换为独立的随机密钥
INTEGRATION_ENCRYPTION_KEY=请替换为第二个独立的随机密钥
FRONTEND_PORT=6311
BACKEND_PORT=6315
BACKEND_BIND_ADDRESS=127.0.0.1
PANEL_REQUIRE_HTTPS=false
XUI_ALLOW_PRIVATE_NETWORKS=false
INITIAL_ADMIN_USERNAME=admin_user
INITIAL_ADMIN_PASSWORD_B64=请填写管理员密码的Base64
~~~

生成管理员密码的 Base64：

~~~bash
printf %s '请替换为管理员密码' | base64 | tr -d '\n'
~~~

IPv4 环境：

~~~bash
docker compose -f docker-compose-v4.yml pull
docker compose -f docker-compose-v4.yml up -d
~~~

IPv6 环境需要 Docker daemon 已配置 IPv6：

~~~bash
docker compose -f docker-compose-v6.yml pull
docker compose -f docker-compose-v6.yml up -d
~~~

查看状态和日志：

~~~bash
docker compose -f docker-compose-v4.yml ps
docker compose -f docker-compose-v4.yml logs -f backend
docker compose -f docker-compose-v4.yml logs -f frontend
~~~

首次初始化完成后，可以删除 .env 中的 INITIAL_ADMIN_USERNAME 和 INITIAL_ADMIN_PASSWORD_B64，避免把一次性初始化凭据长期留在配置文件中。安装脚本会自动完成这一步，并写入 ADMIN_CONFIGURED=1。

### 安装目录和管理命令

安装脚本默认使用 /opt/relayforge，可以在首次安装时修改：

~~~bash
RELAYFORGE_INSTALL_DIR=/data/relayforge ./panel_install.sh
~~~

脚本会把 Compose 文件、.env、管理脚本和运行相关文件放在同一目录。进入目录后可执行：

~~~bash
cd /opt/relayforge
./panel_install.sh update
./panel_install.sh export
./panel_install.sh uninstall
~~~

命令说明：

- update：下载最新 Compose 配置，拉取前后端镜像并重建前后端容器；不会重新询问管理员账号，也不会覆盖面板设置。
- export：使用 mysqldump 生成 database_backup_YYYYMMDD_HHMMSS.sql。
- uninstall：停止并删除容器、镜像和 Docker volume，包括数据库数据；执行前务必完成备份。
- menu：进入交互式管理菜单。

数据库结构由后端启动时自动创建和兼容升级，不再要求安装包提供公开 SQL 文件。后端只补齐缺失的表、字段和默认配置，不会覆盖已经保存的配置。

### 迁移和备份

同一台服务器更新时，Compose 中名为 `mysql_data` 的命名 volume 会保留数据库内容；实际 Docker volume 名称通常会带上 Compose 项目前缀，可以使用 `docker volume ls` 确认。迁移到新服务器时有两种方式：

1. 在旧服务器执行 ./panel_install.sh export，把生成的 SQL 文件复制到新服务器，并导入新建的 MySQL 数据库。
2. 停止旧环境后复制完整的 mysql_data volume，再使用相同的 .env 数据库凭据启动。

恢复数据库后再启动后端，面板设置、节点密钥、隧道和转发记录即可一起恢复。节点密钥属于敏感凭据，备份和传输时应限制文件权限。

旧版本使用 MySQL 5.7，不能直接把原数据目录交给 MySQL 8.4。升级前先执行 `./panel_install.sh export`，停止旧 Compose，删除旧数据库 volume 后使用新 Compose 创建 MySQL 8.4，再导入备份：

~~~bash
docker compose down
docker compose up -d mysql
docker compose exec -T mysql mysql -u root -p"$DB_ROOT_PASSWORD" "$DB_NAME" < database_backup_YYYYMMDD_HHMMSS.sql
docker compose up -d backend frontend
~~~

确认备份已经恢复前不要执行 `docker compose down -v` 以外的清理操作；该命令会删除数据库 volume，属于不可逆操作。

### 自建发布地址

面板和节点安装脚本默认从 WayneDuan/RelayForge-Release 下载发布文件。可以使用以下变量切换到自建 Release 目录：

~~~bash
RELAYFORGE_RELEASE_BASE_URL=https://download.example.com/relayforge ./panel_install.sh
~~~

如果所在网络必须通过下载代理，使用 `RELAYFORGE_RELEASE_MIRROR` 显式指定代理前缀；脚本不会再根据 IP 自动选择第三方镜像。代理应当能够安全转发原始 HTTPS 发布地址，并且仍会通过 `checksums.txt` 校验下载内容。

Linux Agent 的自动升级清单可以单独指定：

~~~bash
RELAYFORGE_AGENT_MANIFEST_URL=https://download.example.com/relayforge/agent-manifest.json ./install.sh
~~~

更新清单和其中的二进制地址必须使用 HTTPS。清单需要提供当前平台的二进制地址和 SHA-256 值。

## 安装节点 Agent

### Linux

1. 在面板的“节点管理”中创建节点，填写节点名称、节点公网地址/内网地址和可选端口池。
2. 在节点卡片中复制 Linux 安装命令。
3. 在目标 Linux 服务器以 root 执行命令。
4. 回到面板刷新节点状态，确认节点在线。

安装后文件和服务：

~~~text
/etc/gost/gost
/etc/gost/config.json
/etc/gost/gost.json
/etc/systemd/system/gost.service
~~~

常用排查命令：

~~~bash
systemctl status gost
journalctl -u gost -f
/etc/gost/gost -V
~~~

### Windows

1. 在面板创建 Windows 节点并复制 Windows 命令。
2. 使用管理员权限打开 PowerShell。
3. 执行面板生成的命令，等待 RelayForgeAgent 服务启动。

Windows 安装位置：

~~~text
%ProgramData%\RelayForge\Agent
~~~

Windows 安装脚本只支持 64 位 Windows，服务名为 RelayForgeAgent，启动类型为自动。脚本也支持通过 -ReleaseBaseUrl 指定 HTTPS 发布地址，Agent 自动升级也会使用该地址；卸载时可以只传入 -Uninstall。

### Agent 自动升级

新安装的 Linux 和 Windows Agent 默认开启自动升级。首次检查在启动约 30 秒后执行，之后每 6 小时执行一次。只有版本号更高、下载地址为 HTTPS 且 SHA-256 校验通过时才会替换当前二进制。

旧版 Agent 如果没有自动升级逻辑，需要先在节点上手动执行一次安装脚本的“更新”，之后才能使用自动升级。升级期间 Agent 会短暂重启，面板上的节点状态可能短暂离线。

## 快速使用流程

### 1. 配置面板地址

首次登录后打开“面板设置”，检查以下值：

- 面板主机名或 IP：节点能够解析和访问的地址。
- 前端端口：浏览器访问面板的端口，默认 6311。
- 后端端口：节点直连 `ws://` 时使用的宿主机端口，默认 6315；只有 `BACKEND_BIND_ADDRESS` 对外监听时才可直连。
- 开启 WSS：使用 HTTPS 反向代理或 Cloudflare 时启用，并填写代理的 WSS 端口，通常为 443。

节点安装命令会根据这里的配置生成 ws:// 或 wss:// 地址。外部 Nginx、Caddy、Traefik 或 Cloudflare 配置必须允许 WebSocket Upgrade，并确保 /system-info 可以转发到后端。

### 2. 创建节点和端口池

创建节点时：

- 节点公网地址用于其他节点连接和反向中继入口；没有公网地址的机器可以留空。
- 节点内网地址用于标识节点自身的内网地址。
- 自动分配端口池支持单端口、连续范围和逗号组合，例如 50000-50010,51000。

使用自动分配端口或中继隧道时，应为相关节点配置可用端口池。面板会检查入口端口和中继内部端口是否与已有转发冲突。

### 3. 创建隧道

常用配置：

- 直连出口：入口节点和出口节点为同一节点，目标地址由该节点直接访问。
- 中继隧道：入口节点和出口节点为不同节点；入口节点需要能连接出口节点，中继内部端口需要可用。
- 内网反向中继：入口节点选择公网服务器，出口节点选择无公网的 Windows 节点。入口节点必须填写 server_ip，Windows 节点只需要主动连接公网入口。

### 4. 创建转发

在转发服务中选择隧道，填写目标地址，例如：

~~~text
127.0.0.1:3389
192.168.1.20:445,192.168.1.21:445
~~~

目标地址中的多个目标会按照所选策略进行故障切换、轮询、随机或固定来源选择。入口端口可以手动填写，也可以让面板从节点端口池自动分配。

反向中继的典型配置：

| 公网入口端口 | Windows 目标 | 示例 |
| --- | --- | --- |
| 53389 | 127.0.0.1:3389 | 远程桌面 |
| 50080 | 127.0.0.1:80 | Windows 本机 Web 服务 |
| 50445 | 192.168.1.20:445 | 内网文件服务 |

只需要在公网入口服务器放行选定的公网端口。内网 Windows 节点不需要把内部服务端口暴露到公网。

### 5. 诊断和维护

- 转发诊断从实际承载流量的节点执行 TCP 探测，显示目标、平均连接耗时和失败率。
- 隧道诊断会分阶段检查入口节点、出口节点、入口到出口链路和出口节点的外部连接能力。
- 暂停/恢复会同步更新 Agent 中的服务状态。
- 流量超额后的状态为“已超额”，提高转发或隧道额度后再执行恢复。

## 权限、流量和安全

后端保留管理员和普通用户模型：

- 管理员可以管理节点、隧道、转发、用户、隧道权限、限速规则和面板配置。
- 普通用户只能查看被授权的隧道和自己的转发资源。
- 用户可以配置有效期、转发数量、流量上限、流量重置时间和启用状态。
- 隧道权限可以单独指定限速规则、流量上限、数量和有效期。

认证和通信实现：

- 登录成功后返回带 issuer/audience 的 HMAC-SHA256 JWT，默认有效期 120 分钟；登录失败会按 IP/账号限流。
- 新密码使用 PBKDF2-SHA256、120,000 次迭代和随机盐保存；保留旧 MD5 密码的兼容校验以便接管旧数据。
- 管理 WebSocket 使用一次性短期 ticket；节点 WebSocket 和流量上报使用请求头中的节点密钥，不把长期凭据放入 URL。
- Agent 与面板使用节点密钥派生 AES-256-GCM 密钥，加密 WebSocket 消息内容。
- 节点密钥同时用于 Agent 身份认证和消息加密，应当视为密码管理，不要提交到 Git 或粘贴到公开渠道。
- 生产环境必须使用 WSS、独立强随机密钥、强数据库密码和最小化防火墙规则；后端默认要求 HTTPS。

## 配置参考

### Docker 环境变量

| 变量 | 说明 | 默认/示例 |
| --- | --- | --- |
| DB_HOST | 后端连接的 MySQL 主机 | Compose 中为 mysql |
| DB_NAME | 数据库名 | 安装脚本随机生成 |
| DB_USER | 数据库用户 | 安装脚本随机生成 |
| DB_PASSWORD | MySQL 业务用户密码 | 安装脚本随机生成 |
| DB_ROOT_PASSWORD | MySQL root 密码 | 安装脚本生成且与业务密码不同 |
| DB_SSL_MODE | MySQL TLS 模式 | Preferred；按数据库部署环境调整 |
| JWT_SECRET | JWT 签名密钥 | 安装脚本随机生成 |
| INTEGRATION_ENCRYPTION_KEY | Telegram/3x-ui 凭据加密密钥 | 安装脚本随机生成 |
| PANEL_REQUIRE_HTTPS | 是否要求后端请求使用 HTTPS | Compose 默认为 false；生产应为 true |
| XUI_ALLOW_PRIVATE_NETWORKS | 是否允许 3x-ui 访问私网地址 | false |
| Cors__AllowedOrigins__0 | 直接跨域访问后端时允许的前端 Origin | 未配置时拒绝跨域 |
| FRONTEND_PORT | 前端宿主机端口 | 6311 |
| BACKEND_PORT | 后端宿主机端口 | 6315 |
| BACKEND_BIND_ADDRESS | 后端端口绑定地址 | 127.0.0.1；节点直连时改为 0.0.0.0 并配置防火墙 |
| INITIAL_ADMIN_USERNAME | 空数据库首次创建的管理员用户名 | admin_user |
| INITIAL_ADMIN_PASSWORD_B64 | 空数据库首次创建的管理员密码 Base64 | 无默认值 |

### 数据库存储的面板设置

后端会自动创建 vite_config 表，并在缺失时插入以下默认配置：

| 配置名 | 说明 | 默认值 |
| --- | --- | --- |
| app_name | 面板名称 | RelayForge |
| panel_host | Agent 使用的面板主机名或 IP | 空，使用当前请求 Host |
| frontend_port | 前端访问端口 | 6311 |
| backend_port | 后端/WS 访问端口 | 6315 |
| panel_secure | 是否生成 WSS 地址 | 0；生产由 `PANEL_REQUIRE_HTTPS` 强制为 1 |
| secure_port | WSS/HTTPS 端口 | 443 |

### Telegram 通知

面板设置页支持 Telegram Bot 通知。后端会使用现有的 AES-GCM 密钥加密保存 Bot Token，配置接口不会返回 Token 原文。

启用步骤：

1. 在 Telegram 中向 `@BotFather` 创建 Bot，复制 Bot Token。
2. 将 Bot 加入目标私聊或群组并发送一条消息，使用 Telegram Bot API 或现有 Bot 工具获取对应 Chat ID。
3. 打开面板的“面板设置 -> Telegram 通知”，填写 Bot Token、Chat ID 和流量通知阈值，保存后发送测试消息。

默认会在用户、转发、用户隧道或隧道流量达到阈值时通知，在额度用尽时再次通知；流量低于阈值后，下一次重新达到阈值可以再次通知。节点上线/离线通知可以单独关闭。

更新面板只更新镜像和容器，不会重新运行旧版安装迁移，也不会覆盖这些已经保存的值。因此在另一台电脑登录，或重启面板后，节点安装地址仍会从数据库恢复。

## API 与节点通信

API 基础路径为 /api/v1，请求主要使用 POST，响应统一包含 code、msg、ts 和 data 字段。需要登录的请求通过 Authorization: Bearer <JWT> 认证。

主要接口分组：

| 路径 | 用途 |
| --- | --- |
| /api/v1/user/* | 登录、用户、密码、用户流量和用户资源包 |
| /api/v1/node/* | 节点列表、增删改、安装命令和状态检查 |
| /api/v1/tunnel/* | 隧道、用户授权和隧道诊断 |
| /api/v1/forward/* | 转发增删改、排序、暂停/恢复和转发诊断 |
| /api/v1/speed-limit/* | 限速规则 |
| /api/v1/config/* | 面板服务端配置 |
| /api/v1/notification/telegram/* | Telegram 状态、保存配置和测试消息 |
| /health | 后端健康检查 |
| /system-info | Agent 和面板管理端的 WebSocket 通道 |
| /flow/upload | Agent 加密流量上报 |
| /flow/config | Agent 流量配置兼容入口 |

/api/v1 和现有数据库字段会保留兼容逻辑，便于旧部署平滑接管。验证码接口目前没有接入实际验证码服务，登录不依赖验证码。

Agent 连接 /system-info 时使用节点密钥进行身份认证；面板发送命令和 Agent 返回结果都会以 AES-256-GCM 包装。生产环境使用反向代理时，除了 API 路由，还必须正确转发 /system-info 的 WebSocket Upgrade。

## 本地开发

### 前端

前端需要 Node.js 20.19 或更高版本，默认开发端口为 3000。开发环境的 VITE_API_BASE 默认为 http://127.0.0.1:6365，也可以改为本地后端地址。

~~~bash
cd vite-frontend
npm install
npm run dev
~~~

常用命令：

~~~bash
npm run build
npm run lint
npm run preview
~~~

### 后端

后端需要可访问的 MySQL。可以使用本地 MySQL，也可以先启动 Compose 中的 MySQL 服务。启动空数据库前需要设置数据库环境变量、JWT_SECRET、集成加密密钥和一次性管理员环境变量：

~~~bash
cd dotnet-backend
export DB_HOST=127.0.0.1
export DB_NAME=relayforge
export DB_USER=relayforge
export DB_PASSWORD='请替换为数据库密码'
export DB_ROOT_PASSWORD='请替换为不同的 root 密码'
export Panel__RequireHttps=false
export JWT_SECRET='请替换为随机JWT密钥'
export INTEGRATION_ENCRYPTION_KEY='请替换为第二个随机密钥'
export INITIAL_ADMIN_USERNAME=admin_user
export INITIAL_ADMIN_PASSWORD_B64="$(printf %s '请替换为管理员密码' | base64 | tr -d '\n')"
dotnet run --urls http://127.0.0.1:6365
~~~

后端目标框架是 .NET 10，容器内部监听 6365。

### Agent

~~~bash
cd go-gost
go test ./...
go build -o gost .
~~~

Agent 目录包含一个嵌套的 go-gost/x 模块，CI 使用 Go 1.23.4 构建 Linux amd64、Linux arm64 和 Windows amd64 二进制。

## 发布维护

.github/workflows/docker-build.yml 只在 main 分支推送时运行，当前流程包括：

- 构建并推送多架构前端镜像 wayneduan/relayforge-frontend。
- 构建并推送多架构 .NET 10 后端镜像 wayneduan/relayforge-backend。
- 构建、压缩并测试 Linux amd64、Linux arm64 和 Windows amd64 Agent。
- 生成包含版本、下载地址和 SHA-256 的 agent-manifest.json，以及覆盖所有部署资产的 checksums.txt。
- 将 Agent 二进制、安装脚本和 Compose 文件上传到当前 GitHub 仓库的 Release。
- 应用代码或 Agent 代码变更前只需修改工作流中的 VERSION 并创建新版本；Release 资产中的 Compose 文件会由 workflow 按该版本自动渲染，已发布版本不会被覆盖。只修改安装脚本或 Compose 文件时，流程只更新对应 Release 资产。

Release 使用当前仓库的内置 `GITHUB_TOKEN`，工作流已为发布任务声明 `contents: write` 权限，无需额外配置 GitHub 发布令牌。Actions 只需要以下外部凭据：

- Docker Hub 用户名和 Token：用于推送前后端镜像。

修改 Agent 代码后，CI 会重新构建 Agent 并更新发布清单；只修改面板脚本或 Compose 文件时，可以只更新 Release 资产。

## 故障排查

### 后端容器不健康

~~~bash
docker compose -f docker-compose-v4.yml logs --tail=200 mysql
docker compose -f docker-compose-v4.yml logs --tail=200 backend
curl http://127.0.0.1:6315/health
~~~

重点检查 .env 中的 DB_NAME、DB_USER、DB_PASSWORD、DB_ROOT_PASSWORD、JWT_SECRET 和 INTEGRATION_ENCRYPTION_KEY 是否存在，MySQL 是否已通过 healthcheck，以及宿主机端口是否被其他程序占用。

### 节点一直离线

1. 检查 systemctl status gost 或 Windows 的 RelayForgeAgent 服务。
2. 检查 Agent config.json 中的面板地址是否使用了正确的 wss://（本地 Compose 明确关闭 HTTPS 时才使用 ws://）、主机和端口。
3. 检查面板后端端口或 WSS 代理端口的防火墙规则。
4. 如果使用外部反向代理，确认 /system-info 支持 WebSocket Upgrade。
5. 确认节点密钥没有被重新生成；删除并重建节点后，旧 Agent 配置不会自动获得新密钥。

### 中继或反向中继创建失败

- 确认入口和出口节点都在线。
- 确认中继节点配置了端口池，且入口端口、中继内部端口没有冲突。
- 反向中继必须把公网服务器设为公网入口，并填写入口节点的公网 server_ip。
- Windows 节点只需要主动访问公网入口，不要依赖公网入口主动访问 Windows 内网地址。
- 检查公网入口服务器和节点本机防火墙是否放行实际使用的端口。

### 转发变成“已超额”

转发状态达到单条转发额度或隧道额度后会自动暂停。提高对应额度后，在面板执行恢复；如果隧道总额度仍然用尽，单独恢复转发也会被拒绝。

### 更新后设置没有变化

面板主机、端口和 WSS 设置存储在 MySQL 的 vite_config 表中。更新只补齐缺失的默认值，不会覆盖已有值。确认使用的是原来的 `.env` 和 Compose 管理的数据库 volume，并检查后端连接的数据库是否正确。

## 许可证

控制面板和本仓库代码使用 Apache License 2.0，详见 LICENSE；前端目录保留其单独的 MIT 许可和 NOTICE，第三方依赖与 GOST 组件还须遵守各自许可证。
安全问题请阅读 [SECURITY.md](SECURITY.md)，发布前请按其中说明清理 Git 历史中的旧私有资产并轮换已暴露凭据。
