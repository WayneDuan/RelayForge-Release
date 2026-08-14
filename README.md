# RelayForge / 铸流

RelayForge 是我们维护的转发控制面板，面向自有节点、隧道和端口转发场景。源码仓库为私有仓库，部署文件通过公开发布仓库分发。

## 当前架构

- 前端：React + Vite，桌面优先的运维工作台
- 后端：ASP.NET Core `.NET 10`
- 节点 Agent：Go GOST
- 节点通信：AES-256-GCM 加密 WebSocket
- 数据库：MySQL 5.7+


## 主要能力

- 节点在线状态、CPU、内存和流量监控
- TCP/UDP 转发服务管理
- 直连和中继隧道编排
- 转发暂停、恢复和目标诊断
- 节点 Agent 加密通信
- JWT 管理会话与 PBKDF2 密码存储
- 支持反向中继部分无公网的机器可支持

##  部署

使用安装脚本：

```bash
curl -fsSL https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/panel_install.sh -o panel_install.sh
chmod +x panel_install.sh
./panel_install.sh
```

安装脚本会把所有部署文件集中保存到 `/opt/relayforge`，包括 `docker-compose.yml`、`gost.sql`、`.env`、管理脚本和数据库备份。安装完成后可使用以下命令：

```bash
cd /opt/relayforge
./panel_install.sh update
./panel_install.sh export
./panel_install.sh uninstall
```

面板设置页中的节点地址、端口和 WSS 配置保存在 MySQL 的 `vite_config` 表中，不依赖浏览器缓存。更新面板只更新镜像和服务，不再执行安装脚本迁移；后端启动时只补齐缺失的默认配置，不会覆盖已保存的值。同一台服务器换电脑登录也会自动恢复。迁移到新服务器时，请先在旧服务器执行 `./panel_install.sh export`，再恢复导出的数据库备份和 `mysql_data` 数据后启动面板。

首次安装可以通过 `RELAYFORGE_INSTALL_DIR` 修改目录，例如：

```bash
RELAYFORGE_INSTALL_DIR=/data/relayforge ./panel_install.sh
```

脚本支持通过 `RELAYFORGE_RELEASE_BASE_URL` 指定自建发布文件地址。节点安装脚本同样支持该变量。

节点 Agent 新安装后默认开启自动升级：启动 30 秒后首次检查，之后每 6 小时检查发布清单，并在 SHA-256 校验通过后自动替换和重启。升级清单默认使用公开发布仓库，也可以通过 `RELAYFORGE_AGENT_MANIFEST_URL` 自定义。旧版 Agent 不包含更新器，需要先在节点执行一次安装脚本的“更新”，后续版本即可自动升级。

## 合规说明

RelayForge 仅用于合法、合规且经过授权的网络转发和远程维护。使用者需要自行确保节点、目标地址和流量用途符合所在地区法律法规以及服务商政策。
