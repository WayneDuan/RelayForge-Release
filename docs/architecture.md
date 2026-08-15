# RelayForge Architecture

RelayForge is a self-hosted control plane with three runtime components:

```text
Browser -> vite-frontend (Nginx)
              | /api/v1, /flow, /system-info
              v
         dotnet-backend (control plane) -> MySQL
              |
              | encrypted WebSocket
              v
         go-gost agent -> TCP / UDP target services
```

## Backend layers

The backend uses a small modular-monolith layout. Modules are separated by dependency direction, not by deployment unit:

```text
Api -> Application -> Domain
 |         |
 +------> Infrastructure
```

- `Api`: HTTP routes, request contracts, authentication extraction, WebSocket entry points, and response formatting.
- `Application`: use cases for forwarding, flow reconciliation, speed-limit changes, and background work. `ForwardOperations.cs`, `FlowOperations.cs`, and `SpeedLimitOperations.cs` keep orchestration and business rules out of the route definitions.
- `Domain`: stable identity types, row projections, port-range rules, and GOST protocol builders. It does not open database connections or handle HTTP startup.
- `Infrastructure`: MySQL access, encrypted credentials, Telegram/3x-ui clients, and the node gateway implementation.
- `Bootstrap`: composition root, configuration, middleware, and health checks. `Infrastructure/DatabaseInitializer.cs` owns idempotent schema creation, compatibility migrations, and first-admin creation.

The current database is intentionally kept compatible with the existing schema. Schema initialization remains idempotent so a self-hosted installation can upgrade without a migration service. New database changes should be added as a named, idempotent migration step and covered by a startup test.

## Frontend layers

```text
pages/features -> api modules -> api/network.ts -> backend HTTP API
       |              |
       +-----------> types/
```

- `api/network.ts`: one HTTP client, authorization, API envelope handling, and token-expiration behavior.
- `api/*.ts`: one module per feature area. These functions are the only place that knows endpoint paths.
- `types/`: shared response and feature contracts. Avoid `Record<string, any>` for new code.
- `pages/`: view composition and local UI state. Business calls go through feature API modules.

## Deployment boundary

The frontend is a static artifact and can be served by the included Nginx image or any static host. The backend is the only component that needs database credentials. Agents only need the panel WebSocket address and their node secret. Compose files keep MySQL private on the internal network and expose only the frontend and backend ports.

## Change guidelines

1. Keep `/api/v1` paths and response envelopes backward compatible unless a versioned API is introduced.
2. Add new backend behavior as an application service or use case before adding route code.
3. Keep SQL and external HTTP calls in infrastructure-facing classes.
4. Add a typed frontend API function and a feature type before using a new endpoint in a page.
5. Do not add secrets, local `.env` files, IDE state, build output, or generated release artifacts to the repository.
