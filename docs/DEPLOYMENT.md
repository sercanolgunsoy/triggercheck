# Deployment

TriggerCheck runs as **one process**: the Razor Pages web app plus the monitoring engine (scheduler, alerting,
retention). Run exactly one instance per database, because the scheduler assumes it is the only one. Data lives in
SQLite. Postgres notes come with OPS-2.

## Container image

CI (`.github/workflows/ci.yml`) builds the image on every push and pull request:

- The `docker-build` job handles pull requests and other branches. It has a read-only token and pushes nothing.
- The `docker-publish` job runs only on pushes to `main`. It is the only job with `packages: write`, and it pushes to
  GHCR with the workflow's `GITHUB_TOKEN`.

Third-party actions are pinned to commit SHAs, with the tag in a comment.

The published tags:

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

> **Bind mount instead of a named volume:** the image creates `/data` owned by the `app` user, but a bind-mounted
> host directory keeps the host's ownership. The app then cannot create the database and fails at startup. Give
> the directory to UID 1654 first: `sudo mkdir -p /srv/triggercheck && sudo chown 1654:1654 /srv/triggercheck`, then
> `-v /srv/triggercheck:/data`. Do the same after restoring files into it. On SELinux hosts, also add `:Z` to the
> mount.

> **Reverse proxy:** behind a TLS-terminating proxy set `ForwardedHeaders__Enabled=true` and list the proxy in
> `ForwardedHeaders__KnownProxies` (or its network in `ForwardedHeaders__KnownNetworks`). The app then takes the client
> IP and the https scheme from `X-Forwarded-For` / `X-Forwarded-Proto`: the auth cookie is marked `Secure`, HTTPS
> redirection works, and the per-IP API rate limit sees real clients (without it every client shares the proxy's IP,
> so one client could exhaust the limit for everybody). Headers from any other address are ignored.

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
| `ANTHROPIC_API_KEY` (or `Ai__ApiKey`) | for AI features | Claude API key. Used only when `Ai__Enabled=true`. CI uses a fake client and needs no key. See [AI features](#ai-features). |
| `Ai__Enabled` | no (false) | Global AI kill switch. See [AI features](#ai-features). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (or a per-signal `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` / `_METRICS_ENDPOINT` / `_LOGS_ENDPOINT`) | no | Turns on OTLP export, e.g. `http://otel-collector:4317`. Each signal is configured separately: the generic variable enables traces and metrics, while a per-signal variable enables only that signal (logs additionally need `Telemetry__ExportLogs=true`). Per OTel convention, a per-signal endpoint is used as-is: for http/protobuf, include the path, e.g. `http://collector:4318/v1/traces`. Its protocol comes from `OTEL_EXPORTER_OTLP_<SIGNAL>_PROTOCOL`, or else `OTEL_EXPORTER_OTLP_PROTOCOL` (default grpc). With the generic endpoint, the standard `OTEL_*` variables apply (`OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`). |
| `Telemetry__OtlpEndpoint`, `Telemetry__OtlpProtocol` (`grpc` \| `http/protobuf`) | no | Base collector address set through configuration. For http/protobuf, the `/v1/traces`, `/v1/metrics` and `/v1/logs` paths are appended. It is a per-signal fallback: it applies to each signal that has neither its own `OTEL_EXPORTER_OTLP_<SIGNAL>_ENDPOINT` nor the generic `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| `Telemetry__ExportLogs` | no (false) | Also exports logs through OTLP. **Off by default because logs contain personal data**: alert recipient addresses, monitor names and targets (masked where they hold secrets), error texts. Turn it on only if the collector and its storage are approved for that data. |
| `Monitoring__Enabled` | no (true) | Runs the scheduler in this process. |
| `Monitoring__SchedulerTickMilliseconds` (1000), `Monitoring__MaxConcurrency` (50), `Monitoring__RetentionCleanupMinutes` (60) | no | Engine tuning. |
| `Monitoring__AllowPrivateTargets` | **never in production** | Turns SSRF protection off. |
| `ForwardedHeaders__Enabled` | behind a reverse proxy (false) | Processes `X-Forwarded-For` and `X-Forwarded-Proto`. |
| `ForwardedHeaders__KnownProxies`, `ForwardedHeaders__KnownNetworks` | with the above | Trusted proxy IPs (comma separated) / networks in CIDR form, e.g. `10.0.0.0/8`. With neither set only loopback is trusted. |
| `ForwardedHeaders__ForwardLimit` | no (1) | Number of proxy hops to unwind. |
| `Api__RequestsPerMinute` (120), `Api__RequestsPerMinutePerCustomer` (300), `Api__RequestsPerMinutePerIp` (600) | no | REST API rate limits, see [API.md](API.md). |
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

When an OTLP endpoint is configured, the app exports:

- **Traces:** incoming ASP.NET Core requests (except `/health*`) and one `monitor.check` span per probe
  (ActivitySource `TriggerCheck.Monitoring`). The probe span carries the monitor id, customer id and type, never the
  target. Outgoing HttpClient calls are **not traced**, because their URLs carry secrets: webhook tokens live in the
  path, and probe targets may contain keys.
- **Metrics:** ASP.NET Core, .NET runtime, and Meter `TriggerCheck.Monitoring`. `System.Net.Http` metrics are not
  collected: their `server.address` tag would create one series per customer target.
  - `triggercheck.monitoring.checks`, `triggercheck.monitoring.check.duration`: tagged by `monitor.type` and
    `result` (up/down/error)
  - `triggercheck.monitoring.scheduler.tick_lag`
  - `.due_monitors`: due monitors at the last tick, including ones still in flight
  - `.in_flight`
  - `.heartbeat`: unix seconds of the last completed tick. Alert when `time() - heartbeat > 120`.
  - `triggercheck.monitoring.alerts`: tagged by `channel.type` and `result` (sent/failed). The alerting code has to
    call `MonitoringMetrics.RecordAlert`; until it does, this counter stays at zero.
- **Logs:** only with `Telemetry__ExportLogs=true` (see the PII note above).

If no endpoint is configured, OpenTelemetry is not registered at all and nothing is exported.

## Backup (SQLite, safe with WAL)

The database runs in WAL mode, so recent commits may exist only in `triggercheck.db-wal`. **Do not `cp` the `.db`
file while the app runs.** The copy can be stale or torn. Use `VACUUM INTO` or sqlite3 `.backup`. Both read a
consistent snapshot and work while the app keeps running.

`scripts/backup-sqlite.sh [DB_PATH] [BACKUP_DIR] [KEEP]` does the following:

1. takes a `flock` on `BACKUP_DIR/.backup-sqlite.lock`, so overlapping runs fail fast instead of colliding. It also
   removes `.partial` leftovers of killed runs. Where `flock` is unavailable (macOS), it prints a warning and runs
   without the lock, so do not schedule overlapping runs there.
2. runs `VACUUM INTO` to write `BACKUP_DIR/triggercheck-<UTC yyyyMMddTHHmmssZ>.db` (a compact, self-contained
   file; it waits up to 30 s if the database is busy)
3. runs `PRAGMA integrity_check` on the copy and keeps it only if the result is `ok`
4. deletes all but the newest `KEEP` backups (default 14). Only files matching its own
   `triggercheck-YYYYMMDDTHHMMSSZ.db` name are affected, so manual copies in the same directory are kept.
5. exits non-zero on any failure, so your cron or alerting can catch it

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

## AI features

The AI layer (`src/TriggerCheck.Ai`) interprets monitoring data with Claude and runs an L1 statistical baseline. The
product works fully without it: status, incidents and alerts are always decided by the deterministic engine.

| Variable | Default | Purpose |
|---|---|---|
| `Ai__Enabled` | `false` | **Global kill switch.** `false` (or no API key) registers a no-op client: nothing leaves the system and no AI rows are written. Restart to apply. The SuperAdmin *AI usage* page shows the state read-only. |
| `ANTHROPIC_API_KEY` / `Ai__ApiKey` | none | Claude API key (`Ai__ApiKey` wins when both are set). Keep it in the secret store. |
| `Ai__Model` | `claude-opus-5-5` | Default model (decision K1). |
| `Ai__Effort` | `low` | Default effort: `low`, `medium`, `high`, `xhigh` or `max`. Any other value (also per feature) stops the startup. |
| `Ai__Features__<Feature>__Model`, `__Effort`, `__MaxTokens` | inherit | Per-feature overrides, e.g. `Ai__Features__IncidentInterpretation__Model=claude-haiku-5-5`. Change a model or effort only after the eval run passes (`evals/README.md`). |
| `Ai__MaxTokens` | 16000 | Output cap per call (thinking + JSON). Calls that hit it are recorded as `Truncated` (visible on the SuperAdmin page); raise the feature's `MaxTokens` if that happens. |
| `Ai__TimeoutSeconds` (60), `Ai__MaxRetries` (2) | | `TimeoutSeconds` is the overall cap per call. The SDK retries 429/5xx/network errors, and each attempt gets `TimeoutSeconds / (MaxRetries + 1)` (at least 5 s). |
| `Ai__RetryCooldownMinutes` | 60 | An identical input that was refused or failed is not sent again within this time. |
| `Ai__QueueCapacity` (200), `Ai__MaxConcurrency` (2) | | Background AI queue. AI calls never run on a request or check path; a full queue drops the job (logged). |
| `Ai__Pricing__<model>__InputPerMTok`, `__OutputPerMTok`, `__CacheReadPerMTok`, `__CacheWritePerMTok` | list prices | USD prices for cost estimates (SuperAdmin *AI usage*). |
| `Ai__Baseline__Enabled` (true), `__IntervalSeconds` (60), `__RefreshMinutes` (60), `__WindowDays` (7), `__ZThreshold` (4), `__K` / `__N` (3 of 5), `__CloseAtMost` (1) | | L1 baseline / anomaly job (no LLM, no key needed). Runs only when `Monitoring__Enabled` is true. |

**Slowdown detection latency (AI-1):** a slowdown is flagged when 3 of the last 5 successful checks are anomalous.
The first job run after the third slow check flags it, so the latency is 3 check intervals plus up to
`Ai__Baseline__IntervalSeconds` (default 60 s). Each run reads only the newest checks (one query per batch of monitors
and check interval). Baselines are rebuilt about once per `RefreshMinutes`, spread over that period. A signal closes when
at most `CloseAtMost` of the last 5 checks are anomalous, or when the monitor is disabled, deleted or its customer is
suspended.

**Who gets AI:** a call is made only when all of these hold:

- `Ai__Enabled=true` and a key is set;
- the customer has opted in on the *AI features* page (CustomerAdmin). Customers created after this release start opted in; existing customers start opted out (decision K2);
- the customer's plan includes the feature;
- the plan's monthly AI budget is not used up. The budget (calls and tokens) is edited on the SuperAdmin *Plans* page.

Opt-out, not-in-plan and over-budget jobs are recorded in `AiDecisions` with a `Skipped*` status. The UI then silently
shows no AI content, or "AI summary unavailable this month" when the budget is used up.

The budget is reserved before each call: a `Pending` row is written in the same database transaction as the check, so
parallel calls cannot overshoot the call limit. The token limit can be exceeded by the calls in flight, at most
`MaxConcurrency × MaxTokens`. These count towards the budget:

- **Calls:** calls that returned token usage, plus pending calls. Calls that failed before the model answered (network, rate limit) are free.
- **Tokens:** input + cache writes + output, with cache reads counted at 1/10.

**Privacy (decision K5):** only masked technical data is sent: monitor type, **host name** (URLs are reduced to scheme
+ host; paths, query strings and fragments are dropped), status codes, error types and timings. These are masked first:

- URL credentials and webhook URLs;
- e-mail addresses and phone numbers;
- key/value secrets (`Authorization`, `api_key`, `*_password`, `client_secret`, quoted JSON values, ...);
- bare credential shapes: JWTs, `sk-…` keys, AWS keys, Telegram / GitHub / Slack tokens, and any long letter+digit token.

Probe error texts and response snippets are normalized (NFKC), stripped of control characters, truncated and sent in
delimited `<untrusted_data>` blocks that the prompt declares to be data, never instructions. Model output is validated
(hypotheses need evidence, actions only from a fixed list, autonomy capped per feature) and never triggers an action by
itself. Context is loaded per tenant only. Requests go to the Claude API (Anthropic); update the privacy notice before
enabling AI in production.

**Audit and retention:** `AiDecisions` has one row per call or skip. Each row holds the feature, subject, input hash,
model, effort, validated output, confidence, autonomy level, status, tokens, estimated cost and latency. Each user's
helpful / not helpful vote is in `AiFeedback`. Failures store a short code, never exception text: `timeout`,
`rate_limited`, `refusal:<category>`, `max_tokens`, `invalid_output`, and so on. The retention cleanup deletes these
after the plan's `RetentionDays`:

- AI decisions, except the current month's, which the budget needs;
- their feedback;
- closed anomaly signals.
