# Security Policy

RelayForge is a self-hosted control plane. Operators are responsible for the
network, database, release source, and secrets used by their installation.

## Reporting a vulnerability

Please report security issues through a private GitHub Security Advisory for
the repository. Do not publish credentials, exploit details, or a working
proof-of-concept in a public issue. Include the affected version, deployment
mode, reproduction steps, impact, and any suggested mitigation.

We will acknowledge a report when it is received, keep the report private while
the fix is prepared, and publish a release note when a fix is available.

## Deployment requirements

- Set independent random values for `JWT_SECRET` and `INTEGRATION_ENCRYPTION_KEY`.
- Use `PANEL_REQUIRE_HTTPS=true` behind a TLS reverse proxy for production.
- Keep MySQL and the backend port private; expose the frontend or reverse proxy.
- Set `XUI_ALLOW_PRIVATE_NETWORKS=true` only when the operator intentionally
  needs to connect to a private 3x-ui panel.
- Remove `INITIAL_ADMIN_USERNAME` and `INITIAL_ADMIN_PASSWORD_B64` after the
  first startup and change the initial password immediately.
- Back up database files and node configuration files as secrets.

## Credential rotation

If a JWT key, integration key, node secret, database password, Telegram token,
or 3x-ui credential may have been exposed:

1. Stop affected agents and revoke or rotate the credential at its source.
2. Change the RelayForge secret or database credential and restart the service.
3. Recreate affected node credentials and integrations.
4. Review access logs and database records for unauthorized changes.

Changing `JWT_SECRET` invalidates all active panel sessions. Changing the
integration encryption key requires re-entering stored Telegram and 3x-ui
credentials unless the old key is retained for a controlled migration.

Any passwords, node secrets, JWT keys, database credentials, and API tokens
present in those commits must be rotated even after the history is rewritten.
