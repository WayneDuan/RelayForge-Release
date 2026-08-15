# Contributing to RelayForge

RelayForge is a free, self-hosted project. Contributions should preserve backward compatibility for existing installations and keep the control plane usable without a hosted service.

The frontend requires Node.js 20.19 or newer. The backend requires the .NET 10 SDK, and the agent requires Go 1.23.4.

## Local checks

```bash
cd vite-frontend
npm install
npm run lint
npm run build
```

For the backend, install the .NET 10 SDK and run:

```bash
cd dotnet-backend
dotnet restore
dotnet build
```

The backend needs a MySQL instance only for integration or manual runtime checks. Do not commit local connection strings or credentials.

## Pull requests

- Keep changes scoped to one concern.
- Preserve existing API paths and JSON fields when possible.
- Document schema or environment-variable changes in `README.md`.
- Include the commands used to verify the change.
- Do not include generated binaries or private deployment files.
