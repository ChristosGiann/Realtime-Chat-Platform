# Apothiki v1 deployment

The first public topology is **one API container plus one managed PostgreSQL database**. Redis and Ollama are disabled. PostgreSQL is the durable source of truth; SignalR carries transient events. Lost events are recovered through REST. The multi-instance Redis architecture remains available for later use; see [Stage 6 handoff](stage6-production-handoff.md).

## Build and run locally

Build both targets from the repository root:

```sh
docker build --target migrations -t apothiki-migrations:local .
docker build -t apothiki:local .
```

The Node and .NET base images are pinned by digest, `npm ci` uses the committed lockfile, and the SDK/tool versions are pinned in `global.json` and `dotnet-tools.json`. Review and update base image digests with dependency advisories during maintenance.

Create a PostgreSQL database and a private, writable directory for Data Protection keys. Supply the connection string through a secret manager or an environment file outside this repository. Never use Docker build arguments for secrets. Apply migrations **before** starting the new image:

```sh
docker run --rm --network <database-network> -e ConnectionStrings__ChatDatabase='<postgres-connection-string>' apothiki-migrations:local
docker run --rm --network <database-network> -p 8080:8080 \
  -e ConnectionStrings__ChatDatabase='<postgres-connection-string>' \
  -e SharedDataProtection__KeyRingPath=/keys \
  -e SharedDataProtection__ApplicationName=apothiki-v1 \
  -v <private-key-directory>:/keys \
  apothiki:local
```

The key directory must already exist and be writable by the image's non-root `APP_UID`. Keep the same directory and application name across restarts and releases. The image refuses to start without the mounted directory. Migrations are **never** run by the application startup. The separate migration target uses the same source revision and pinned `dotnet-ef` tool. Back up the database before migrating. Review schema compatibility before switching images; this first topology has no zero-downtime guarantee.

The image listens on `8080` by default. Set `PORT` to a valid TCP port to override it and publish the matching container port. HTTP inside the container is intended for a trusted TLS-terminating reverse proxy. Test `GET /health/live`, `GET /health/ready`, `GET /`, a deep link such as `/conversations`, and an API route. The SPA fallback serves `index.html` for browser paths; unknown `/api`, `/hubs` and `/health` paths stay 404. Compiled assets are inside the image; the runtime image has no Node runtime or build secrets.

For a disposable local container behind an HTTPS reverse proxy, run `PRODUCTION_BASE_URL=https://localhost:8443 npm --prefix frontend run test:e2e:production`. Set `PRODUCTION_DOCKER_CONTAINER=<container-name>` to include the controlled restart/key-persistence check. The test fixture in `tests/deployment/Caddyfile` supplies a local TLS proxy with a self-signed certificate; it is **only a test proxy**, not a Railway configuration. CI builds the image, applies migrations separately, starts it on dynamic port 8090, and runs this browser test through HTTPS/WSS. CI also recreates the application container with the same key volume and checks that both existing sessions survive redeploy.

## Railway

Create one service from the Dockerfile, a managed PostgreSQL service, and a persistent private volume mounted at `/keys`. [Railway mounts volumes as root](https://docs.railway.com/volumes), so set `RAILWAY_RUN_UID=0` on this service. The constrained entrypoint changes ownership of **only `/keys`** and immediately drops to UID 1654 before starting .NET. Without Railway's UID override, the image defaults to its non-root `APP_UID`; pre-own the local key mount accordingly. Verify the final process UID in deployment diagnostics. Configure these service variables in Railway's secret settings:

| Variable | Purpose |
| --- | --- |
| `ConnectionStrings__ChatDatabase` | Managed PostgreSQL connection string, including provider-appropriate SSL settings. |
| `SharedDataProtection__KeyRingPath=/keys` | Persistent mounted key directory. |
| `SharedDataProtection__ApplicationName=apothiki-v1` | Stable key discriminator. |
| `Deployment__PublicOrigin=https://<public-host>` | Exact public HTTPS origin for SignalR browser Origin checks. |
| `Deployment__RailwayEdgeMode=true` | Treat only requests with the exact public Host, `X-Forwarded-Host`, and `X-Forwarded-Proto=https` as HTTPS after Railway edge TLS termination. |
| `PORT` | Railway-provided listener port; the app binds `0.0.0.0:<PORT>`. |
| `RAILWAY_RUN_UID=0` | Allows the entrypoint to prepare Railway's root-owned key volume; the application then runs as UID 1654. |

`Deployment__RequirePersistentKeys=true` is set in the image; do not override it. Explicitly set `Redis__BackplaneEnabled=false` and `UnreadSummaries__Enabled=false` if inherited variables could enable them. Do not configure Redis or Ollama for this topology. No public deployment is created by this repository change.

Keep the PostgreSQL service private to the project. Railway's private network is encrypted in transit; if using an external managed database, require its supported TLS mode and verify certificate behavior in the connection string. Give the application a dedicated database role with only the necessary permissions, and protect the migration role separately if schema ownership requires elevated rights. Do not enable a public database endpoint solely for this application.

Run the `migrations` target as a **separate, one-off job** against the same managed database before switching the application to a new image. The job needs the connection string but no public domain. Check its exit status. Do not put migration commands in the application startup command. Configure `/health/live` for liveness and `/health/ready` for PostgreSQL readiness. `/health/realtime` reports optional Redis connectivity and is not a readiness gate here.

## HTTPS, cookies and proxy trust

The production frontend defaults to relative `/api` and `/hubs/chat` URLs, so REST, SignalR negotiation, WebSockets (`wss://`) and cookies share one HTTPS origin. Local Vite development retains the `http://localhost:5080` API default and its existing CORS policy. Production cookies use `__Host-` names, `Secure`, `HttpOnly`, and existing SameSite/CSRF settings. Do not disable them. The reverse proxy must pass WebSocket upgrades and preserve the public host. Set `Deployment__PublicOrigin` to the exact external HTTPS origin; startup validates it. Leave `Cors__AllowedOrigins` unset for same-origin production.

Forwarded headers are **off by default**. For a reverse proxy with stable peer IPs, set `Deployment__TrustedProxies__0` (and further numbered entries) to exact trusted proxy IPs. The app then processes at most one forwarded hop and restricts forwarded hosts to the configured public host. Never use wildcard proxy trust. Railway [does not publish stable edge proxy peer IPs](https://station.railway.com/questions/asp-net-core-10-secure-forwarded-heade-506a954b), so use the explicit Railway edge mode instead: it changes **only the scheme**, and only when `Host` and `X-Forwarded-Host` exactly match `Deployment__PublicOrigin` and `X-Forwarded-Proto` is exactly `https`. It does not trust `X-Forwarded-For` for a client identity. Railway's public edge replaces incoming forwarded headers, but private networking bypasses that sanitization. Keep the first project environment isolated to this app and its database, without untrusted peer services or a public TCP proxy; otherwise use an independently authenticated ingress with stable trusted proxy addresses. Never enable Railway edge mode on a directly exposed generic HTTP listener.

## Keys, backup, restore and rollback

ASP.NET Core Data Protection keys protect session cookies. Preserve the mounted key directory across restarts and redeployments. Restrict filesystem access to the application identity and back it up securely. [Railway states its volumes and volume backups are encrypted at rest](https://docs.railway.com/enterprise/security); verify equivalent protection if moving to another provider. The key XML files are **not application-encrypted**; do not put them on public or untrusted storage. Losing the keys invalidates active sessions. A copied key ring plus database backup is sensitive; protect both as secrets. Rotate credentials in the hosting provider, never in Git or image layers.

Back up PostgreSQL on a schedule and test restores to an isolated database. Rolling back the application image does **not** roll back an EF migration. Restore a verified backup or make a forward corrective migration after assessing schema compatibility and data loss. Keep the prior image available until readiness, login, messaging, receipt and REST recovery checks pass. A restore can lose messages committed after the backup. There is no durable SignalR replay or outbox.

## Operational checks

- `/health/live` confirms process response. `/health/ready` confirms PostgreSQL access. On failure, check database connectivity, SSL, credentials and migration status without logging connection strings.
- Check root page, refresh and deep links. Unknown `/api/*` paths must remain 404 rather than returning HTML.
- Check login and CSRF on the public HTTPS URL. If sessions fail after restart, inspect key persistence, write permission and stable `ApplicationName`.
- SignalR negotiates `/hubs/chat` on the same origin and upgrades over WSS. A 403 can mean a mismatched `Deployment__PublicOrigin`; 401 means no valid session. The Hub still validates the Identity user and membership is server enforced.
- Redis disabled means no backplane or distributed coordination. Keep **one API instance**. Restart drops connections and transient presence/typing; clients reconnect and recover durable messages/receipts from PostgreSQL via REST.
- Do not enable request logging that captures cookies, CSRF tokens, connection strings or AI prompts. Use managed secrets and least-privilege database credentials.

Known dependency advisories remain: transitive `Microsoft.OpenApi` and npm advisories. This deployment work does not claim to resolve them.
