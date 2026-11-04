# Deployment

TriggerCheck runs as **one process**: the Razor Pages web app plus the monitoring engine (scheduler, alerting,
retention). Run exactly one instance per database, because the scheduler assumes it is the only one. Data lives in
SQLite. Postgres notes come with OPS-2.

## Container image

CI (`.github/workflows/ci.yml`) builds the image on every push and pull request. It pushes the image to GHCR only on
pushes to `main`:

- `ghcr.io/sercanolgunsoy/triggercheck:latest` is the head of `main`
- `ghcr.io/sercanolgunsoy/triggercheck:sha-<short sha>` is an immutable tag for that commit. Deploy this one.

The image:

- is multi-stage (`sdk:9.0` builds the app, `aspnet:9.0` runs it) and runs as the non-root `app` user (UID 1654)
- sets `ASPNETCORE_ENVIRONMENT=Production` and listens on **HTTP 8080**. Terminate TLS at a reverse proxy or load
  balancer.
- keeps all state in the **`/data` volume**:
  - `/data/triggercheck.db` (+ `-wal`, `-shm`) is the database (`ConnectionStrings__Default=Data Source=/data/triggercheck.db`)
  - `/data/.aspnet/DataProtection-Keys` holds the cookie / antiforgery keys (`HOME=/data`). If these are lost, every
    user is signed out. The keys are stored unencrypted, which is why the app logs a warning at startup. Protect the
    volume like the database.

```bash
docker volume create triggercheck-data
docker run -d --name triggercheck -p 8080:8080 \
  -v triggercheck-data:/data \
  -e Seed__SuperAdminPassword='<first-run only, strong>' \
  -e Smtp__Host=smtp.example.com -e Smtp__User=... -e Smtp__Password=... -e Smtp__From=alerts@example.com \
  ghcr.io/sercanolgunsoy/triggercheck:sha-<short sha>
```

To build locally, run `docker build -t triggercheck .` from the repository root.

> **Reverse proxy:** the app does not process `X-Forwarded-*` headers yet. Behind a TLS-terminating proxy it sees
> plain HTTP, so `UseHttpsRedirection` logs "Failed to determine the https port" and the auth cookie is not marked
> `Secure`. Follow-up: enable `ForwardedHeaders` for the proxy's network.

## Configuration (environment variables)

ASP.NET Core maps `Section__Key` environment variables to `Section:Key` settings. Put secrets in the orchestrator's
secret store, never in the image or in `appsettings.json`.

| Variable | Required | Purpose |
|---|---|---|
| `ConnectionStrings__Default` | preset in image | SQLite connection string. Default: `Data Source=/data/triggercheck.db`. |
| `Seed__SuperAdminPassword` | **first start** | Creates the SuperAdmin (`Seed__SuperAdminEmail`, default `superadmin@triggercheck.local`). The account must change this password at first login. Remove the variable afterwards: once a SuperAdmin exists the app ignores it and logs a warning. |
| `Seed__SuperAdminEmail` | no | SuperAdmin login. |
| `Smtp__Host`, `Smtp__Port` (587), `Smtp__UseSsl` (true), `Smtp__User`, `Smtp__Password`, `Smtp__From`, `Smtp__TimeoutSeconds` | for email alerts | If `Smtp__Host` is empty, alert emails are only written to the log. |
| `Twilio__*` | coming (SMS/voice, FEAT-7) | Twilio account SID / auth token / sender. Names are fixed when the feature lands. |
| `ANTHROPIC_API_KEY` | coming (AI features) | API key for the AI features. CI uses a fake client and needs no key. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | Enables OTLP export of traces, metrics and logs, e.g. `http://otel-collector:4317`. The standard `OTEL_*` variables apply too (`OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`). |
| `Telemetry__OtlpEndpoint`, `Telemetry__OtlpProtocol` (`grpc` \| `http/protobuf`) | no | Same as above, set through configuration. Used only when `OTEL_EXPORTER_OTLP_ENDPOINT` is not set. |
| `Monitoring__Enabled` | no (true) | Runs the scheduler in this process. |
| `Monitoring__SchedulerTickMilliseconds` (1000), `Monitoring__MaxConcurrency` (50), `Monitoring__RetentionCleanupMinutes` (60) | no | Engine tuning. |
| `Monitoring__AllowPrivateTargets` | **never in production** | Turns SSRF protection off. |
| `Site__ContactEmail` | no | Sales / support address shown on the marketing pages. |
| `Seed__DemoData` | no | Ignored outside Development. |

## Health and telemetry

| Endpoint | Meaning | Use as |
|---|---|---|
| `/health/live` (alias `/health`) | The process is serving requests. Runs no checks. | liveness probe |
| `/health/ready` | The database is reachable, and the scheduler completed a tick in the last 2 minutes (or 3 tick intervals if that is longer). The scheduler check only applies when `Monitoring:Enabled`. Returns **503** when unhealthy. Shortly after startup, before the first tick, it reports `Degraded` with 200. | readiness probe, uptime alerting |

The endpoints are anonymous and return only `{"status":"Healthy|Degraded|Unhealthy"}`. Per-check details are shown
in Development only, and exception texts are never returned. The `aspnet` image has no `curl`, so use the
orchestrator's HTTP probes (Kubernetes `httpGet`, Compose with a sidecar, etc.).

Metrics (Meter `TriggerCheck.Monitoring`) and traces (ActivitySource `TriggerCheck.Monitoring`, one `monitor.check`
span per probe) are exported through OTLP together with the ASP.NET Core, HttpClient and .NET runtime
instrumentation:

- `triggercheck.monitoring.checks`, `triggercheck.monitoring.check.duration`: tagged by `monitor.type` and `result`
  (up/down/error)
- `triggercheck.monitoring.scheduler.tick_lag`, `.due_monitors`, `.in_flight`, `.heartbeat` (unix seconds of the last
  completed tick). Alert when `time() - heartbeat > 120`.
- `triggercheck.monitoring.alerts`: tagged by `channel.type` and `result` (sent/failed)

If no endpoint is configured, nothing is exported.

## Backup (SQLite, safe with WAL)

The database runs in WAL mode, so recent commits may exist only in `triggercheck.db-wal`. **Do not `cp` the `.db`
file while the app runs.** The copy can be stale or torn. Use `VACUUM INTO` or sqlite3 `.backup`. Both read a
consistent snapshot and work while the app keeps running.

`scripts/backup-sqlite.sh [DB_PATH] [BACKUP_DIR] [KEEP]` does the following:

1. runs `VACUUM INTO` to write `BACKUP_DIR/triggercheck-<UTC yyyyMMddTHHmmssZ>.db` (a compact, self-contained
   file; it waits up to 30 s if the database is busy)
2. runs `PRAGMA integrity_check` on the copy and keeps it only if the result is `ok`
3. deletes all but the newest `KEEP` backups (default 14)
4. exits non-zero on any failure, so your cron or alerting can catch it

It needs the `sqlite3` CLI (>= 3.27). The runtime image does not include it, so run the script from the host or a
sidecar that mounts the same volume:

```bash
# Host with the volume mounted (or a bind mount):
scripts/backup-sqlite.sh /var/lib/docker/volumes/triggercheck-data/_data/triggercheck.db /srv/backups/triggercheck 14

# Or a throwaway container on the same volume:
docker run --rm -v triggercheck-data:/data -v "$PWD/scripts:/scripts:ro" alpine:3 \
  sh -c 'apk add --no-cache bash sqlite >/dev/null && /scripts/backup-sqlite.sh /data/triggercheck.db /data/backups 14'
```

Schedule the script, e.g. hourly or daily with cron or a Kubernetes CronJob, and copy `BACKUP_DIR` off the machine
(object storage). A backup on the same disk as the database does not protect against losing that disk. Back up the
data-protection keys (`/data/.aspnet`) too. Without them, restored sessions are lost, but no data is.

The one-off equivalent with the sqlite3 shell is `sqlite3 /data/triggercheck.db ".backup '/backups/manual.db'"`.

## Restore

1. **Stop the app.** One writer at a time. The restore must not race the scheduler.
2. Check the backup: `sqlite3 backup.db "PRAGMA integrity_check;"` must print `ok`.
3. Move the current files aside. Do not delete them until the restore is verified:
   `mv triggercheck.db triggercheck.db.before-restore` and remove the old `triggercheck.db-wal` and
   `triggercheck.db-shm`. A stale WAL file next to a different database corrupts it.
4. Copy the backup into place as `triggercheck.db`, then make it writable by the app user:
   `chown 1654:1654 triggercheck.db` (volume) or the service user.
5. Start the app. At startup it switches the file back to WAL mode. `/health/ready` must return 200. Sign in and check
   the latest monitors, incidents and notifications.
6. Monitors resume from their stored `NextCheckAtUtc`. Overdue monitors are checked right away. Data written after
   the backup was taken is lost, so tell customers about the gap if needed.

**Verified once (OPS-6):** this was tested against a published build running in Production mode with the container's
environment (port 8080 replaced by a local port, `/data` replaced by a scratch directory). At the time, the fresh
database's data was still entirely in the `-wal` file: the main `.db` file was 4 KB. Steps:

- ran the script three times with `KEEP=2` while the app was running. Retention removed the oldest file.
- confirmed the backup was complete: it held the plans and the SuperAdmin.
- changed a row after the backup, stopped the app, and restored with the steps above.
- started the app again: the changed row was back to its backed-up value, `/health/ready` returned 200, and the
  journal mode was back to `wal`.
