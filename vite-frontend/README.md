# RelayForge frontend

RelayForge 是私人转发基础设施的 React/Vite 控制台，围绕节点健康、隧道拓扑、转发服务和快速维护设计。

## 本地运行

```bash
npm install
npm run dev
```

默认开发地址为 `http://localhost:3000`。API 请求默认发送到当前站点的 `/api/v1`，生产环境由 Nginx 转发到 `.NET 10` 后端。

## 构建

```bash
npm run build
```
