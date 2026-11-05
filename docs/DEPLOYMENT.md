# Deployment

TriggerCheck runs as **one process**: the Razor Pages web app plus the monitoring engine (scheduler, alerting,
retention). Several instances may share one database (see [Running multiple instances](#running-multiple-instances));
one instance is the default. Data lives in SQLite. Postgres notes come with OPS-2.

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
| `Site__ContactEmail` | no | Sales / support address shown on the marketing pages. It also receives operator alerts, such as "refund needed" for a refused payment. |
| `Site__PublicBaseUrl` | **yes outside Development** | Public address of the app, e.g. `https://app.example.com`. Used in e-mail links (sign-up confirmation, password reset), for the iyzico callback, for the "Acknowledge" link in DOWN and escalation alerts, and for heartbeat ping URLs. **The app refuses to start without it** in any environment other than Development. The former `Billing__PublicBaseUrl` is still read as a fallback. |
| `Escalation__CheckIntervalSeconds` (30), `Escalation__AckLinkValidHours` (72) | no | How often the escalation job runs; lifetime of acknowledge links. |
| `Monitoring__DnsResolvers__0`, `__1`, ... | no (1.1.1.1, 8.8.8.8) | Resolvers for DNS monitors, tried in order: `ip`, `ip:port` or `[ipv6]:port`. Operator configuration (not subject to the SSRF guard). |
| `Heartbeat__PingsPerMinutePerToken` (10), `Heartbeat__PingsPerMinutePerClient` (6000), `Heartbeat__PingsPerSecondGlobal` (1000) | no | Limits of the `/hb/{token}` ping endpoint. The per-client limit is per IPv4 address / IPv6 /64 and deliberately high (many hosts behind one NAT). **Behind a reverse proxy enable ForwardedHeaders**, otherwise all pings share the proxy's address. |
| `Regions__AgentStaleAfterSeconds` (90), `Regions__VoteWindowFactor` (2), `Regions__LeaseGraceSeconds` (30), `Regions__ClockSkewSeconds` (30), `Regions__MinTransitionSeconds` (60), `Regions__AgentRequestsPerMinute` (300) | no | Multi-region checks, see [Probe agents](#probe-agents-multi-region-checks). |
| `StatusPages__CacheSeconds` (30), `StatusPages__RequestsPerMinutePerClient` (120), `StatusPages__SubscriptionsPerHourPerClient` (10), `StatusPages__NotifyIntervalSeconds` (60, 0 = off), `StatusPages__IncidentMailIntervalMinutes` (30), `StatusPages__ConfirmationsPerAddressPerDay` (3), `StatusPages__ConfirmationsPerPagePerHour` (30), `StatusPages__DomainRecheckHours` (24), `StatusPages__DomainRecheckFailures` (3) | no | Public status pages (FEAT-4): output cache lifetime, per-client limits (IPv4 / IPv6 /64; enable ForwardedHeaders behind a proxy), subscriber notification job. See [Status pages](#public-status-pages-and-custom-domains). |
| `Reports__JobIntervalMinutes` (60, 0 = off), `Reports__CatchUpDays` (7), `Reports__DownloadLinkDays` (30) | no | Monthly SLA report e-mail (FEAT-10): how often the job looks for due reports, how many days into a month the previous month is still sent, lifetime of the signed PDF link. |
| `Seed__DemoData` | no | Ignored outside Development. |

Acknowledge links are protected with ASP.NET Core Data Protection (purpose `IncidentAck.v1`), the same key ring as
the login cookies. In the container `HOME=/data`, so the keys live in `/data/.aspnet/DataProtection-Keys` and links
stay valid across restarts; keep that directory on the persistent volume (it is part of `/data`).

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

## Running multiple instances

Two or more instances can share one database (for availability or load). Every instance runs the full app,
scheduler included; nothing has to be switched off per instance.

- **Instance id:** `Monitoring__InstanceId` names the instance in leases (max 100 characters, must be unique per running
  process). Default: machine name + process id + a random suffix. **Recommended: a stable id per instance** (e.g. the
  pod / container name of a StatefulSet, `web-1`, `web-2`): at startup the instance frees the leases it still held
  before a crash, so its monitors and jobs resume at once. With the default id a crashed instance's leases are only
  taken over when they expire (see below).
- **Monitor checks:** each check slot runs on exactly one instance. A free instance claims the due monitor with a
  conditional update of `Monitors.LeasedUntilUtc` / `LeaseOwner` (compare-and-set, portable to Postgres), runs it,
  advances the next check and releases the lease. A crashed instance's leases expire after 2 x the monitor's timeout +
  `Monitoring__SchedulerLeaseMarginSeconds` (30) and are then taken over.
- **"Check now"** takes the same lease, so it never overlaps with a scheduled check (or another "check now") on any
  instance: it waits while another run holds the monitor (at most the probe timeout + 5 s, capped at 30 s) and then
  asks the user to try again. The schedule is unchanged by the lease. This matters because the monitor's concurrency
  token only guards status transitions and user edits: overlapping runs could lose a failure count, skip the delay of
  a confirmation probe or send a certificate warning twice.
- **Jobs that run on one instance at a time** (cluster lease in `ClusterLeases`, renewed by the holder, taken over
  about three intervals after the holder stops, or at once when it shuts down cleanly): billing maintenance, the AI
  event watcher and the AI baseline job. Long runs renew the lease before each side effect (each billing item, each
  baseline batch) and stop if it was lost; the AI event watcher starts its scan over whenever its hold was interrupted.
- **Jobs that are safe on every instance:** alert delivery (each instance sends what its own checks produced), escalation
  (each step is claimed in the database), the e-mail outbox (rows are claimed), monthly SLA reports (unique
  customer + month row written with the e-mails), status page notices (unique event keys, also for digests), remote
  probe leases (claimed conditionally, only while still due), retention cleanup (idempotent deletes; it just runs N
  times).
- **Per instance, so effectively N times the configured value:** rate limits (logins, "check now", API, agent
  requests), alert delivery throttles and queue sizes, output / domain caches (a change can take up to their TTL to
  show on the other instances), `/health/ready` (each instance reports its own scheduler heartbeat).
- **SQLite:** all instances must reach the same database file on a local filesystem (several containers on one host
  sharing a volume). WAL mode does not work over network filesystems; for instances on different hosts use Postgres
  (OPS-2). Postgres could later select due monitors with `FOR UPDATE SKIP LOCKED` to save wasted claims; the
  compare-and-set stays the guarantee.
- **Limit:** a check that outlives its lease (e.g. a database stall longer than the margin) can overlap with the
  instance that reclaimed it. The concurrency token still prevents a duplicate incident or status alert, but the
  overlap can lose a failure count or repeat a certificate warning; it is logged as a warning ("outlived its scheduler
  lease").

**Upgrading an existing installation:** OPS-3 adds columns (`Monitors.LeasedUntilUtc`, `Monitors.LeaseOwner`) and a
table (`ClusterLeases`). Until the EF migrations (OPS-1) exist, the schema is created by `EnsureCreated` only on an
empty database; on an existing one it does nothing, and the scheduler then fails with "no such column". Back up and
recreate the database, or apply the migration once OPS-1 lands (both columns nullable, no default needed).

**Upgrading an existing installation (AI-6):** the AI report narratives add a table (`ReportNarratives`, unique
`CustomerId+Kind+PeriodKey`, index on `Status`). On a database created before AI-6 `EnsureCreated` does not add it:
the Reports page and the monthly e-mail keep working without narratives (the lookup degrades to "off" and is logged),
and the narrative job logs an error on each run. Recreate the database or apply the migration once OPS-1 lands.

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

## Self-service sign-up and billing (iyzico)

Visitors sign up at `/Account/Register` (BIZ-1). A paid self-service plan starts a free trial (14 days by default) on
that plan's limits. A trial is granted once per address and to at most 3 sign-ups per company domain; `+tag` aliases
count as the same address. Public mail providers (gmail.com, outlook.com.tr, mynet.com, ...) are only limited per
address. When no trial is granted, the workspace starts on the free plan, and the confirmation e-mail says so. The page
stays the same for every outcome. The account must confirm its e-mail address before it can sign in. Sign-ups that are never
confirmed are deleted after 2 days. This applies only while they are still in their initial state: once a SuperAdmin
has edited the customer or changed its trial, it is never purged. If the same unconfirmed address signs up again, the account takes over the new
submission, and earlier confirmation links stop working. `/Account/ForgotPassword` sends a one-day reset link. It shows
the same answer for every address. A completed reset also confirms the address.

Paid plans are sold as monthly iyzico subscriptions (BIZ-2). The card is entered only on iyzico's hosted payment page,
never in TriggerCheck. **Online paid checkout is switched off by default** (`Billing__PaidCheckoutEnabled=false`). The
Billing page then lists the plans with "contact us to upgrade", and the checkout and plan-change handlers are disabled.
Sign-up and trials work either way. Complete the sandbox checklist below before you switch it on.

### Settings

| Variable | Required | Purpose |
|---|---|---|
| `Iyzico__DefinitiveCheckoutErrorCodes` | no (empty) | iyzico error codes that mean "not paid / token not found" and close a checkout early. See the checklist. |
| `Billing__PaidCheckoutEnabled` | no (false) | Turns on the iyzico checkout, upgrades and downgrades. Leave it off until the sandbox checklist is done. |
| `Iyzico__ApiKey`, `Iyzico__SecretKey` | for paid checkout | iyzico API credentials (merchant panel > Settings). **Secrets.** Store them in the secret store only. |
| `Iyzico__MerchantId` | for webhooks | Part of the webhook signature (`X-IYZ-SIGNATURE-V3`). Without it every webhook is rejected with 401. |
| `Iyzico__BaseUrl` | no | `https://sandbox-api.iyzipay.com` (default, sandbox) or `https://api.iyzipay.com` (production). |
| `Iyzico__CheckoutPageUrl` | no | Hosted payment page, used when the initialize response has no `paymentPageUrl`. Default `https://sandbox-cpp.iyzipay.com`; production `https://cpp.iyzipay.com`. See the checklist. |
| `Billing__TrialDays` (14), `Billing__TrialReminderDaysBefore` (3), `Billing__PastDueGraceDays` (7) | no | Trial length, when the "trial ends soon" e-mail goes out, and how long a failed renewal keeps its plan. |
| `Billing__MaxTrialsPerDomain` (3), `Billing__PublicEmailDomains` | no | Trial cap per company domain. `0` means no cap. Public mail providers (gmail.com, outlook.com, ...) are only limited per address. |
| `Billing__FallbackPlanName` (`Basic`) | no | Plan that customers move to when a trial ends without a subscription, a past-due subscription passes its grace period, or a cancelled subscription reaches the end of its paid period. |
| `Billing__MaintenanceIntervalMinutes` (15) | no | How often the billing job runs. `0` disables it. With several instances it runs on one of them at a time (cluster lease, see [Running multiple instances](#running-multiple-instances)). Every step is idempotent, and its state changes are conditional. |
| `Billing__CheckoutRecheckAfterMinutes` (10), `Billing__CheckoutTimeoutMinutes` (60) | no | An open checkout is checked again with iyzico after 10 minutes, which covers a closed tab or a lost callback. It is closed as failed after 60 minutes if iyzico has not confirmed it. |
| `Billing__WebhookMaxAgeHours` (72) | no | Webhook events older than this, or more than 10 minutes in the future, are recorded but not applied. |
| `Billing__UnconfirmedAccountDays` (2) | no | Unconfirmed sign-ups are deleted after this many days. |
| `Billing__RegistrationsPerHourPerIp` (5), `Billing__WebhooksPerMinutePerIp` (120) | no | Per-IP rate limits for sign-up POSTs, password-reset requests, and the iyzico callback/webhook endpoints. Behind a reverse proxy, enable `ForwardedHeaders__*` (above). Otherwise all clients share the proxy's IP. |

SMTP (`Smtp__*`) is also needed for sign-up and password reset. If it is not configured, those e-mails are not sent.
In Development their full text, including the link, is logged at Information level (marked "DEV ONLY"). In other
environments only a warning is logged, because the links carry account tokens.

### iyzico setup

1. In the iyzico merchant panel (sandbox first), create a subscription **product** and a **monthly pricing plan** for
   each paid self-service plan.
2. In TriggerCheck, go to SuperAdmin > Plans > Edit and paste the product and pricing plan reference codes. A paid plan
   without a pricing plan code cannot be bought online. The plan option "Self-service sign-up and billing" decides
   whether it is offered at sign-up and on the Billing page. It is off for new plans and for Enterprise, which keeps a
   "Contact sales" link.
3. Set the iyzico webhook (notification) URL to `https://<host>/billing/iyzico/webhook`. The checkout callback is
   `https://<host>/billing/iyzico/callback`. TriggerCheck sends it with each checkout.
4. Webhooks are verified with `X-IYZ-SIGNATURE-V3` = hex HMAC-SHA256(secretKey, merchantId + secretKey + eventType +
   subscriptionReferenceCode + orderReferenceCode + customerReferenceCode). An event is identified by the signed
   fields only (type + subscription + order reference) and is processed once. Events older than the last one applied to
   the subscription are recorded but not applied. `subscription.order.success` keeps or restores the subscription.
   `subscription.order.failure` marks it `PastDue`, which moves the customer to the fallback plan after the grace
   period. The webhook request makes no calls to iyzico. An event for an unknown subscription is stored as an ignored
   payment event (`unknown-subscription`), and the billing job reconciles it: for 30 days, at most hourly per event and
   20 events per run, the job re-checks the checkouts that were started before the event and are still open or failed
   unpaid. When one of them turns out to be the subscription, the job applies the event. When no such checkout is left,
   the event is marked `unmatched` and is no longer re-checked; reconcile it by hand. Each provider re-check step of the
   job has a time budget (`Billing__ReconcileBudgetSeconds`, 60), and at most `Billing__MaxCheckoutRechecksPerRun` (50)
   open checkouts are re-checked per run.

**Flow.** Billing page > Subscribe > billing details form > "Continue to secure payment" link to iyzico > iyzico posts
to the callback > TriggerCheck retrieves the checkout result from iyzico (server to server) and only then activates the
plan.
- A checkout is only closed early on a definitive answer from iyzico: a state such as CANCELED, EXPIRED or UNPAID, or
  an error code listed in `Iyzico__DefinitiveCheckoutErrorCodes`. That list is empty until the codes are verified.
  Network errors, 401/403/429, other 4xx and 5xx keep the checkout open, and the job checks it again. At the timeout,
  a checkout that gets no answer is asked once more before it is closed. Failed checkouts that never reached "paid" are
  asked again by the job for 30 days (every 6 hours, 20 per run), so a late confirmation is not lost.
- A paid checkout is never activated when iyzico reports a different or missing pricing plan, or when the customer
  already has an active subscription (for example, two tabs). The "already subscribed" check runs inside the
  activation transaction. In that case the subscription is cancelled at iyzico and an Error is logged. A "Refund
  needed" e-mail goes to `Site__ContactEmail`, and the customer is told that the amount will be refunded. The return
  page shows "refused", never "success". The refund also appears on **SuperAdmin > Refunds**, and the overview shows a
  badge until an operator marks it as refunded. The page records who marked it and when, and the action is logged.
  **Refunds are done by hand in the iyzico panel** until the subscription refund API is verified (see the checklist).
- Checkout POSTs are limited to `Billing__CheckoutsPerHourPerCustomer` (10) per customer. The limit is counted before
  form validation, so it is generous on purpose. A customer can have at most
  `Billing__MaxOpenCheckouts` (3) unfinished checkouts.
- All e-mails (confirmation, password reset, "already registered", billing notices, operator "Refund needed" notices)
  go to a durable outbox table (`EmailOutbox`). The body is cleared as soon as an e-mail is sent or abandoned, and an
  e-mail still unsent after one day is abandoned, so stored account tokens do not outlive their purpose. A background sender delivers them, so requests never wait for SMTP, and
  response times do not reveal whether an account exists. Queued e-mails survive restarts. Failures are retried after
  1, 5, 15 and 60 minutes, and the sender gives up after 5 attempts (logged as an Error). Rows are claimed before
  sending, so several instances never send the same e-mail twice. Sent and abandoned rows are deleted after 7 days.
  Logs contain the recipient and subject only, never the body, which can contain account tokens. Without SMTP, e-mails
  are logged as described above and counted as sent.
- **Upgrades** use iyzico's upgrade endpoint with `NOW`. **Downgrades** use `NEXT_PERIOD` and take effect when the
  period rolls over, which the Billing page shows. A downgrade first checks that the monitors fit the new plan. When it
  applies, the check intervals are clamped and the monitor count is checked again. If monitors were added in the
  meantime, nothing is deleted, and the admins get an e-mail that lists the monitors over the limit.
- **Cancellation** stops renewals at iyzico. The plan stays until the end of the paid period.
- SuperAdmins can extend or end a trial on the customer page. A manual plan change clears a trial or a cancelled
  subscription, and is refused while the customer has an active or past-due subscription.

### Sandbox verification checklist (before `Billing__PaidCheckoutEnabled=true`)

No sandbox account was available during development. Verify these points against the iyzico sandbox. They are marked
`TODO-verify` in `IyzicoPaymentProvider`.
- [ ] **Hosted page vs embedded form.** Does a subscription checkout token open on `CheckoutPageUrl?token=...&lang=..`,
  or does iyzico return a `paymentPageUrl`? If neither works, the `checkoutFormContent` script has to be embedded,
  which needs iyzico's script and frame origins in the CSP (`SecurityHeaders.cs`).
- [ ] **Proration.** What iyzico charges on an upgrade with `NOW`, and what it reports for a `NEXT_PERIOD` downgrade:
  the subscription status and whether a new reference code is returned. The UI promises no proration.
- [ ] **Downgrade at period end.** Does iyzico switch the pricing plan itself at renewal, and does the renewal
  `subscription.order.success` arrive for the old or a new subscription reference? TriggerCheck applies the booked
  plan on the first renewal webhook, or when the job sees the period end.
- [ ] **Undoing a booked downgrade.** "Keep this plan" sends another `NEXT_PERIOD` upgrade back to the current pricing
  plan. Confirm that iyzico accepts this, and that it does not charge or reset the period.
- [ ] **Refund API for subscriptions.** Is there a documented refund for a subscription order (refused duplicate or
  mismatched payment)? Until then, refunds are done manually after the "Refund needed" e-mail.
- [ ] **Definitive error codes.** Which `errorCode` values does the checkout-result retrieval return for "token not
  found / not paid / expired"? Put them in `Iyzico__DefinitiveCheckoutErrorCodes`. Without them, unpaid checkouts
  close only at `CheckoutTimeoutMinutes`.
- [ ] **`iyziEventTime` units.** Milliseconds or seconds (both are accepted today). Also check whether iyzico retries
  webhooks, and for how long, against `WebhookMaxAgeHours`.
- [ ] **Signing path with a query string.** IYZWSv2 signs `randomKey + path + body`. None of the current requests has a
  query string. Confirm the rule before adding one.
- [ ] **Identity number placeholder.** `11111111111` is sent when the customer gives no Turkish identity or tax number.
  Confirm that iyzico accepts it for foreign customers and invoices.
- [ ] **Checkout result for abandoned and expired tokens.** Check what iyzico answers, and the token lifetime, against
  `CheckoutTimeoutMinutes`.
- [ ] **Paid amount and currency** in webhooks or order details. The payment history shows them only when the provider
  reports them.
- [ ] End to end in the sandbox: trial > subscribe > callback, closed tab (webhook / job), renewal success, renewal
  failure > grace > downgrade, upgrade, downgrade at period end, cancel.

## Public status pages and custom domains

Status pages are served by the app itself at `https://<Site__PublicBaseUrl host>/status/<slug>` (plus `.json`,
`/feed` and the subscription pages). They are anonymous, set no cookies (only the subscribe / confirm / unsubscribe
forms carry an antiforgery cookie), are output-cached in memory for `StatusPages__CacheSeconds` per instance and are
rate-limited per client. Subscriber e-mails (double opt-in) go through the e-mail outbox, so SMTP must be configured.
Confirmation and unsubscribe links are signed with the Data Protection key ring in `/data` (keep it persistent).

**Custom domains (Pro / Enterprise).** A customer enters a host such as `status.example.com` in the panel and
publishes two DNS records:

1. `status.example.com CNAME <your app host>` (or A/AAAA records to the reverse proxy), and
2. `_triggercheck.status.example.com TXT "triggercheck-verification=<token>"` (shown in the panel).

After *Verify* succeeds (the TXT lookup uses `Monitoring__DnsResolvers`), requests whose `Host` is that domain are
served the page at `/` (`/index.json`, `/feed`, `/subscribe`, ... likewise); every other path, including sign-in,
the panel and `/api`, answers 404 there. Unverified domains, domains of suspended customers and of plans without
custom domains never serve anything. The domain list is cached for one minute per instance. Several pages may claim
the same domain until one of them verifies it; that drops the other claims. Verified domains are checked again every
`StatusPages__DomainRecheckHours` (24) and stop being served after `StatusPages__DomainRecheckFailures` (3)
consecutive failed checks.

**Host check.** With `Site__PublicBaseUrl` set, a request whose `Host` is neither the app's host nor a verified custom
domain answers **404** (before routing), so a domain still pointing at the proxy after its claim was removed never
shows the app. Exempt, for internal callers: `localhost`, IP-literal hosts, `/health*` (container / load balancer
probes - they may use any host name) and `/status/domain-check` (the proxy's `ask` call). Without
`Site__PublicBaseUrl` (Development only) every host reaches the app.

**Notifications.** Subscribers get at most one incident e-mail per monitor and page every
`StatusPages__IncidentMailIntervalMinutes` (30); changes in between are summarized in the next e-mail. Confirmation
e-mails are capped per address (`StatusPages__ConfirmationsPerAddressPerDay`, 3, over all pages) and per page
(`StatusPages__ConfirmationsPerPagePerHour`, 30) and have a fixed subject.

**TLS is a deployment concern.** The app only sees the `Host` header; the reverse proxy in front of it must accept
arbitrary customer hosts and obtain certificates on demand. With Caddy, for example:

```
{
    on_demand_tls {
        # Only issue certificates for verified custom domains: the app answers 200 for them, 404 otherwise.
        ask http://triggercheck:8080/status/domain-check
    }
}
app.example.com {
    reverse_proxy triggercheck:8080
}
:443 {
    tls { on_demand }
    reverse_proxy triggercheck:8080
}
```

`GET /status/domain-check?domain=<host>` is that `ask` endpoint (anonymous, rate-limited). Never run `on_demand`
without it, or anyone could point a domain at the proxy and exhaust the ACME rate limits. With Traefik or nginx +
cert-manager, issue per-host certificates (HTTP-01) for the verified domains in the same way.
Keep `ForwardedHeaders__Enabled=true` with the proxy's address so per-client limits see real client IPs, and forward
the original `Host` header unchanged.

## SLA reports

The *Reports* page computes monthly SLA figures on the fly (no stored aggregates). PDFs are rendered with PDFsharp
(MIT, pure managed code) and the embedded Roboto font (Apache 2.0), so the runtime image needs no fonts or native
libraries. Opted-in customers get the previous month's report on the 1st (in their report time zone) with a signed,
expiring PDF download link (`/reports/download`, Data Protection purpose `SlaReportDownload.v1`, 10 downloads per
client and minute). Built reports are cached in memory per (customer, month, time zone): closed months for 6 hours,
the running month for 2 minutes.

Fidelity choices (also stated on the Reports page):
- Uptime is **time based**: downtime is the time a monitor had an open incident (from when it went DOWN, i.e. after
  the failure threshold, until it recovered), clipped to the month and to the monitor's lifetime, minus maintenance
  windows. It is not the share of successful checks shown elsewhere in the panel.
- Paused periods count as available (pause history is not stored); deleted monitors disappear from past reports.
- Incident count and MTTR use the incidents that **started** in the month (MTTR with their full duration).
- Response times (average, nearest-rank p95) use successful checks only, without maintenance and confirmation probes.
- Data older than the plan's retention is purged; such months are flagged as possibly incomplete.

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
| `Ai__QueueCapacity` (200), `Ai__MaxConcurrency` (2) | | Background AI queue. AI calls never run on a check path and, except for the AI-9 assistant the user explicitly asks, never on a request path; a full queue drops the job (logged). |
| `Ai__Pricing__<model>__InputPerMTok`, `__OutputPerMTok`, `__CacheReadPerMTok`, `__CacheWritePerMTok` | list prices | USD prices for cost estimates (SuperAdmin *AI usage*). |
| `Ai__Baseline__Enabled` (true), `__IntervalSeconds` (60), `__RefreshMinutes` (60), `__WindowDays` (7), `__ZThreshold` (4), `__K` / `__N` (3 of 5), `__CloseAtMost` (1) | | L1 baseline / anomaly job (no LLM, no key needed). Runs only when `Monitoring__Enabled` is true. |
| `Ai__Events__PollSeconds` (10), `Ai__Events__LookbackMinutes` (15) | | Event watcher of AI-2 / AI-3: how often new / resolved incidents and new slowdown signals are looked up, and how old an event without an AI decision may be to still be picked up after a restart. Runs only when AI is on and a key is set. |
| `Ai__Events__MonitorCooldownMinutes` (30), `__HostWindowMinutes` (10), `__MaxJobsPerCustomerPerScan` (5) | | Cost damping of AI-2: a monitor whose incident was interpreted within the cool-down gets no new summary for the same (normalised) cause (flapping; a different cause is interpreted, at most once per cool-down); of incidents of one customer on the same host within the window only the first is interpreted (its page is linked from the others); per scan at most this many jobs per customer, round-robin across customers. Events over the cap or refused by a full queue are retried on the next scan. |
| `Ai__Narrative__Enabled` (true), `__IntervalMinutes` (15, 0 = off), `__MaxPerRun` (20), `__AutoMonthlyDays` (7), `__MaxAttempts` (3), `__RetryMinutes` (60), `__RunningTimeoutMinutes` (15, must exceed two `Ai__TimeoutSeconds`), `__RequestCooldownMinutes` (60), `__EmailWaitMinutes` (180, must be shorter than `Reports__CatchUpDays` minus two mailer runs; checked at startup) | | AI-6 narrative report batch job: creates the previous month's narrative for eligible customers (AI opted in, `NarrativeReport` in the plan) in the first days of a month and generates requested ones (weekly / older months, from the Reports page); runs on one instance at a time (cluster lease `ai-narrative-report`, renewed before every claim) and stays correct without it (row claims stamped with the claim time). Throughput: at most `MaxPerRun` narratives per `IntervalMinutes` (default 20 per 15 minutes, monthly ones first), so with the default wait about 240 customers get the narrative in their e-mail; the rest are sent without it. The monthly SLA e-mail waits for a narrative that is still expected at most `EmailWaitMinutes` after the month ended, then goes without it; it never waits when AI is off, the customer is not eligible, the budget is used up or all attempts failed. The model writes no numbers at all: its text may only refer to the computed report values through placeholders (`{{A.uptime}}`), which the server renders (units, Turkish decimal comma, durations, change directions, SLA result); digits, number words, SLA verdicts in words, links, domains, e-mail addresses and phone numbers make a statement (or, in the headline, the whole narrative) be dropped. The free-text incident cause is never sent to the model. |
| `Ai__Suggestions__Enabled` (true), `__IntervalMinutes` (360), `__WindowDays` (7), `__DismissDays` (30), `__MaxChecksPerMonitor` (30000), `__BatchSize` (50) | | AI-5 settings suggestion job (rules, no key needed for detection). Runs only when `Monitoring__Enabled` is true, on the cluster leader. `WindowDays` is the history the rules read (never before the monitor's last settings change) and how often the evidence of an open suggestion is refreshed; `DismissDays` is how long a dismissed kind stays hidden (closed suggestions are kept at least `DismissDays + 1` days regardless of plan retention). |
| `Ai__Qa__MaxQuestionChars` (500), `__MaxIterations` (6), `__MaxToolCalls` (12), `__MaxToolResultChars` (6000), `__MaxTotalToolResultChars` (40000), `__MaxRows` (50), `__MaxRangeDays` (90), `__MaxAllMonitorsRangeDays` (7), `__MaxStatsIncidents` (20000), `__MaxHistoryTurns` (3), `__MaxHistoryAnswerChars` (1500), `__MaxTokensPerCall` (4000), `__TimeoutSeconds` (90), `__CallTimeoutSeconds` (60), `__MaxRetries` (1), `__PerUserPerMinute` (4), `__PerUserPerHour` (30), `__MaxConcurrent` (4), `__Effort` (`medium`) | | AI-7 questions (*Ask AI* page). Caps per question: model calls of the tool loop (the last one must answer without tools), tool calls, characters per tool result and in total, rows per list, time range of a query (statistics over all monitors at most `MaxAllMonitorsRangeDays`; incident rows read for their downtime, newest first, flagged when cut), earlier exchanges sent along. `MaxTokensPerCall` bounds a turn (tool turns and the ~120-word answer need far less; worst case per question is `MaxIterations × MaxTokensPerCall` output tokens). `CallTimeoutSeconds` is per HTTP attempt of one model call and `MaxRetries` (0 or 1) the SDK retries: the Q&A client does **not** use the interpreter's `Ai__TimeoutSeconds / (Ai__MaxRetries + 1)`, which is sized for short low-effort calls. `TimeoutSeconds` is the server-side deadline of the whole question; it is kept below `2 × Ai__TimeoutSeconds` so the Pending sweep never closes a running question. Rate limit per user and questions in parallel per instance (in memory). `Effort` applies unless `Ai__Features__QuestionAnswer__Effort` is set; `Ai__Features__QuestionAnswer__MaxTokens` overrides `MaxTokensPerCall`. |
| `Ai__SetupAssistant__RequestsPerUserPerHour` (20), `__MaxRequestChars` (1000) | | AI-9 monitor setup assistant: requests one user may make per rolling hour (model calls, reused answers and over-budget skips all count; an identical request of the same customer reuses the earlier answer without a call and is recorded as a `Succeeded` row with `ErrorCode` `reused` and no tokens), and the length a description is cut to before masking. Model / effort / tokens per feature as usual (`Ai__Features__MonitorSetupAssistant__…`). |
| `Ai__Insights__WindowDays` (7), `__NoiseMinIncidents` (10), `__NoiseMinAlerts` (30), `__FlapMaxMinutes` (10), `__LimitPressurePercent` (80), `__FastestIntervalSharePercent` (50), `__FastestIntervalMinMonitors` (2), `__AbandonedDownDays` (7), `__MaxRows` (25); `__SummaryEnabled` (true), `__MonthlyCallLimit` (60), `__MonthlyTokenLimit` (2000000), `__MinMinutesBetweenSummaries` (15), `__MaxSummaryCustomers` (10) | | AI-12 SuperAdmin *Insights* page. The signals (noisiest customers, customers at or above `LimitPressurePercent` of a plan limit, monitors DOWN for at least `AbandonedDownDays`) are computed by fixed rules on every page view and need no key. The optional AI summary is generated only when a SuperAdmin asks for it; it is a platform cost, never charged to a customer's AI quota: its `AiDecision` rows have no customer and count against `MonthlyCallLimit` / `MonthlyTokenLimit` (shown on the page and on *AI usage*), at most one new model call per `MinMinutesBetweenSummaries` (an unchanged signal set reuses the last summary for free). Customers and monitors are sent only as labels (`[[C1]]`, `[[M1]]`) and the model writes no numbers (placeholders the server renders). Platform decisions are kept 400 days. |

**Slowdown detection latency (AI-1):** a slowdown is flagged when 3 of the last 5 successful checks are anomalous.
The first job run after the third slow check flags it, so the latency is 3 check intervals plus up to
`Ai__Baseline__IntervalSeconds` (default 60 s). Each run reads only the newest checks (one query per batch of monitors
and check interval). Baselines are rebuilt about once per `RefreshMinutes`, spread over that period. A signal closes when
at most `CloseAtMost` of the last 5 checks are anomalous, or when the monitor is disabled, deleted or its customer is
suspended.

**Incident summaries and slowdown explanations (AI-2, AI-3):** a background watcher (`AiEventWatcher`) looks for new
and resolved incidents and new slowdown signals every `Ai__Events__PollSeconds` and queues one AI job per event. The
monitoring engine does not know about it: DOWN / UP alerts are sent exactly as before and never wait for the model
(decision K4). A summary that is ready after the alert is **not** sent as another alert; it appears on the incident
page, as a short line in the incident list, and (for slowdowns) on the monitor page, with a "Slow" badge on the
dashboard. Summaries are written in the customer's AI language ("Language of AI summaries" on the *AI features* page;
set from the admin's UI language when AI is switched on or the customer signs up). Pages say why there is no summary:
AI switched off, customer opted out, not in the plan, budget used up, failed, or still being prepared. The model gets
the monitor settings, the last ~20 checks around the incident with status codes and DNS / connect / TLS / TTFB timings,
error texts (as untrusted data, grouped after removing numbers and ids), the certificate, other monitors on the same host, slowdown signals and maintenance
periods, all masked as described below. Changing these prompts, a model or an effort level needs a passing eval run
(`evals/README.md`); the effort of both features is `low` by default.

**Related incidents (AI-4, alert noise reduction):** incidents of one customer that start within
`Ai__Correlation__WindowMinutes` (5) of the first one and share a signal are grouped as "primary incident +
affected", for notifications only (no incident is ever closed, hidden or changed). Deterministic L1 rules decide:
the same host or IP with errors of the same family (network: DNS / timeout / refused / unreachable / TLS; application:
HTTP 5xx / 4xx / content) is grouped by the rule; weaker signals (same host with unrelated errors, same parent domain
and error type, or the same network / 5xx error on another host) only make a candidate. The model (feature
`CorrelationGrouping`, same masking and budget as above) is asked only to judge the candidates and write the
explanation; it never runs on the alert path.

- **A1 (today, decision K3):** the watcher (`Ai__Correlation__PollSeconds`, 30) proposes groups on *Incidents →
  Related incidents*; a CustomerAdmin keeps the members they want ("Group the selected incidents") or rejects them.
  Notifications are not changed. The page shows the acceptance rate over `Ai__Correlation__AcceptanceWindowDays` (28);
  undecided proposals expire after `Ai__Correlation__ProposalExpiryHours` (24) and are not counted.
- **A3 (off until `MaxAutonomy` of `CorrelationGrouping` in `AiFeatures.All` is raised after > 90 % acceptance):**
  the DOWN alert of a rule-linked incident is folded into the primary's notification, only when the primary's own
  DOWN was delivered; folded alerts are recorded per channel ("Grouped with incident #…"). The decision is a few
  indexed queries plus one short write transaction: it never waits for the model, waits at most
  `Ai__Correlation__AlertPathLockTimeoutSeconds` (2) for the database write lock, and fails open (any doubt, error
  or busy database sends the alert on its own). Eligibility is re-checked under the lock, so a switch-off in the same
  moment wins.
  - **Follow-up:** one short "Also affected (likely same cause as incident #N): "B", "C"" message per group goes to
    the customer's channels (not voice calls) once the first unreported fold is `Ai__Correlation__FollowUpDelaySeconds`
    (60) old; monitors folded after it go into a later, again debounced, follow-up. Webhooks get status
    `also_affected`, `incidentId` (the primary incident) and `alsoAffected` (`monitorId`, `name`).
  - **Fold cap:** a folded monitor still down `Ai__Correlation__MaxFoldMinutes` (15) after its fold gets its own DOWN.
  - **Undo:** when the primary recovers (after its UP was sent), an admin undoes the group, the customer switches
    grouping off, or the customer is no longer eligible for automatic grouping (AI opt-out, `Ai__Enabled=false`,
    `Ai__Correlation__MaxAutonomy` lowered, plan without `CorrelationGrouping`, budget used up), every folded monitor
    still down gets its own DOWN. The watcher checks all of this every poll, so any of these switches takes effect
    within one `PollSeconds`.
  - **Durable, once:** a held-back DOWN is claimed first (`IncidentGroupMembers.NotifiedAtUtc`, a conditional update
    that only succeeds while it is unmarked and the incident still open) and then queued; if the queue refuses it (full,
    shutting down) the claim is released and the watcher tries again on its next poll, also after a restart. Two
    callers never both send it. A crash between claim and delivery loses it like any alert still in the in-memory
    queue. Follow-ups claim their members (`ReportedAtUtc`) the same way.
  - A held-back DOWN whose incident has recovered by the time it is delivered is dropped, and it never supersedes or
    cancels another alert of the monitor (a waiting UP wins); it does not cancel the monitor's pending escalations
    either. Escalations (FEAT-5), maintenance (FEAT-3) and region quorum (FEAT-6) are otherwise unchanged.
  - A folded monitor that recovers before anybody was told about it (no follow-up, no own DOWN) while the primary is
    still down has its UP folded too; once reported, its UP is sent normally.
  - `Ai__Correlation__MaxAutonomy` (`A3`) can only lower the level (`A0` switches the feature off).
- Customers switch grouping off on the same page; it needs `Ai__Enabled`, the AI opt-in and a plan with
  `CorrelationGrouping`. A used-up budget keeps the free rule-based proposals, skips the model and turns A3 off.
- **Multi-instance (OPS-3):** the watcher scans, sends follow-ups / held-back DOWNs and queues explanations only on
  the holder of the `ai-correlation` cluster lease, renewing it before each step. Each group's explanation is queued
  once per membership (`IncidentGroups.ExplanationQueuedAtUtc`; a member joining an automatic group resets it), also
  when the model cannot explain it. Every instance refreshes an in-memory hint each poll so that, at A1, UP alerts
  run no extra database query. **Rolling changes:** while instances run with different autonomy (e.g. during a rolling
  deploy that lowers `Ai__Correlation__MaxAutonomy` or changes the catalog level), an A1 instance may not see automatic
  groups made by an A3 instance for up to one poll; a folded member's UP is then sent instead of folded. Nothing is
  lost: the group is still undone by the watcher and its held-back DOWNs are sent.

**Upgrading an existing installation (AI-4):** adds the tables `IncidentGroups` and `IncidentGroupMembers` and the
column `Customers.AlertGroupingEnabled`. Until the EF migrations (OPS-1) exist, the schema is created by
`EnsureCreated` only on an empty database: back up and recreate the database, or apply the migration once OPS-1 lands
(`Customers.AlertGroupingEnabled` must default to **true**; the new tables start empty).

**Settings suggestions (AI-5, autonomy A1):** every `Ai__Suggestions__IntervalMinutes` the job reads each monitor's
central checks (outside maintenance, confirmation probes excluded) and incidents of the last `WindowDays` days, but only
those recorded since its last settings change, and suggests: a higher failure threshold when most incidents recovered
at the next check (flapping), a longer timeout when successful responses come close to it (and timeouts at the current
value happen), "the target may be wrong" when no check has succeeded since the monitor was created or last changed,
and a keyword review when the page answers but the keyword is missing. Suggestions are made only for customers that
opted in and whose plan includes `SettingsSuggestions`; they are closed when that stops. The numbers shown come from
the database; the model only explains them, and an explanation with a number that is not in its input is rejected.
A CustomerAdmin applies a suggestion with one click on the monitor page (the same validation, plan rules and
concurrency handling as the edit form) or dismisses it for `DismissDays` days; an applied kind is not suggested again
within `WindowDays`. Failed or budget-skipped explanations are retried after `Ai__RetryCooldownMinutes`. Response
bodies are never stored, so no new keyword is ever proposed (only the current one without stray whitespace).

**Questions in natural language (AI-7, autonomy A0, Enterprise plans):** the *Ask AI* page (`/App/Ask`, every
customer role including viewers) answers questions such as "Which service was slowest last week?" with a tool loop over
five **read-only** tools: list monitors, monitor details, statistics over a time range, incidents over a time range, and
incident details. There is no write tool. The customer id comes from the signed-in session and is not a tool argument,
so an id of another customer gives the same "not found" as a missing one. The model sees what K5 allows (ids, monitor
type, host, status, counts, timings, times, status codes, masked error texts in fields named `*_untrusted`), but **not
monitor names**: it cites records as `[[monitor:12]]` / `[[incident:34]]`, which the page turns into links with the
name only after loading the record for the signed-in customer. A name in the question is matched on the server
("name_contains"); the model only learns which monitors matched. The question and earlier answers of the page are
masked like any other input. Answers are plain text (HTML-encoded) with a list of linked sources; numbers that do not
appear in the tool results are shown with a warning (`IQaAnswerGrounding`, replaceable). Each question is one
`AiDecisions` row (feature `QuestionAnswer`, subject = user id) with the summed tokens and cost and `ModelCalls`; the
question and answer text are not stored (the conversation lives in the page's form only). Every model call of the
loop is reserved against the budget before it is made, so a budget that runs out mid-question stops the loop
(`Failed` / `budget_exhausted`, the calls made still count). The request waits for the answer (async, at most
`Ai__Qa__TimeoutSeconds`); more than `Ai__Qa__MaxConcurrent` questions at once get "busy". A model call is not
aborted when the user closes the tab or a proxy times out: it finishes under the server-side deadline, its usage is
recorded and the loop stops (`cancelled`). A question stopped by the deadline (`timeout`) or the user (`cancelled`)
counts its reserved calls even when no usage came back. Opt-out, plan and budget are checked before the per-user rate
limit (they do not use it up) and are recorded as a skipped decision at most once per user per
`Ai__RetryCooldownMinutes`.

**Monitor setup assistant (AI-9, autonomy A1):** the create-monitor page shows a "Describe what to monitor" box to
CustomerAdmins. The description is sent to the model while the user waits (bounded by `Ai__TimeoutSeconds`); the answer
is a fixed JSON schema of form fields. The server rejects unknown monitor types, ignores any other field, restores the
user's own links and quoted text, normalizes the target and clamps every value (an interval below the plan minimum is
raised to it, with an explanation). The pre-filled form is then validated like a normal post (plan rules, SSRF
pre-check), so a private or internal target shows the usual error. The assistant never saves: the user reviews the form
and clicks Create. Links in the description (with any scheme, or scheme-less: host names incl. Unicode, IPv4, `[v6]`,
`localhost:port`, `user@host/...`, a host followed by a path, query or fragment) reach the model as scheme + host only,
and quoted text ("Pay now"; an unclosed quote runs to the end) not at all; credentials written in plain words
("user admin password x", "şifre x", "token is x") are masked, and the rest of the description is masked like other
free text (secrets, tokens, e-mail, phone). The raw description is capped at twice `MaxRequestChars` on the server
before anything else, and cut to `MaxRequestChars` only after links and quotes were extracted. The default plans
include it from Standard up (the AI-10 tiers did not list it); existing databases get it when a SuperAdmin ticks
`MonitorSetupAssistant` on the *Plans* page. When AI is off, the customer opted out or the plan lacks the feature, the
box is hidden and the form works as before.

**Who gets AI:** a call is made only when all of these hold:

- `Ai__Enabled=true` and a key is set;
- the customer has opted in on the *AI features* page (CustomerAdmin). Customers created after this release start opted in; existing customers start opted out (decision K2);
- the customer's plan includes the feature;
- the plan's monthly AI budget is not used up. The budget (calls and tokens) is edited on the SuperAdmin *Plans* page.

Opt-out, not-in-plan and over-budget jobs are recorded in `AiDecisions` with a `Skipped*` status (the AI-2 / AI-3
event watcher does not queue jobs for opted-out customers or plans without the feature at all). Incident and monitor
pages show a short note for each case ("turned off for your account", "not included in your plan", "AI summary
unavailable this month" when the budget is used up).

The budget is reserved before each call: a `Pending` row is written in the same database transaction as the check, so
parallel calls cannot overshoot the call limit. A reservation is always completed, also on shutdown or an unexpected error (`shutdown` / `error`); rows left `Pending` by a crash become `abandoned` after twice `Ai__TimeoutSeconds` (swept at startup and by the baseline job). The token limit can be exceeded by the calls in flight, at most
`MaxConcurrency × MaxTokens`. These count towards the budget:

- **Calls:** calls that returned token usage, plus pending calls. Calls that failed before the model answered (network, rate limit) are free. A Q&A question counts each model call of its tool loop (`AiDecisions.ModelCalls`).
- **Tokens:** input + cache writes + output, with cache reads counted at 1/10.

**Privacy (decision K5):** only masked technical data is sent: monitor type, **host name** (URLs are reduced to scheme
+ host; paths, query strings and fragments are dropped), status codes, error types and timings. These are masked first:

- URL credentials and webhook URLs;
- e-mail addresses and phone numbers;
- key/value secrets (`Authorization`, `api_key`, `*_password`, `client_secret`, quoted JSON values, ...);
- bare credential shapes: JWTs and other dot-separated opaque tokens, `sk-…` keys, AWS keys, Telegram / GitHub / GitLab / Slack tokens, grouped license-style keys, and any long letter+digit token (base64 included). System-generated ids skip only this last rule.

Customer-entered text is not sent: monitor names (the model sees "this monitor", "monitor B", ...), keywords, JSON
paths, expected values, maintenance window names and acknowledgement notes. Probe errors that quote them ("Keyword '…' not found in response",
"JSON value at … (expected …)") are reduced to the kind of failure before they are sent. Heartbeat monitors send no target at all.

Probe error texts and response snippets are normalized (NFKC), stripped of control characters, truncated and sent in
delimited `<untrusted_data>` blocks that the prompt declares to be data, never instructions. Model output is validated
(hypotheses need evidence; AI-2 / AI-3 hypotheses also need a category from a fixed list and evidence references
that point to data actually sent and fit the category, otherwise they are dropped; actions only from a fixed list, autonomy capped per feature) and never triggers an action by
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

**Upgrading an existing installation (AI-7):** AI-7 adds the column `AiDecisions.ModelCalls` (int, not null, default
0). Until the EF migrations (OPS-1) exist, `EnsureCreated` does not add it to an existing database and AI pages fail
with "no such column". Back up and recreate the database, or apply the migration once OPS-1 lands (DB default 0;
existing rows count as one call each).

## Probe agents (multi-region checks)

FEAT-6. By default every monitor is checked only by the main app (the region `central`). Probe agents add remote
regions: a small worker (`src/TriggerCheck.ProbeAgent`, image built from `Dockerfile.probe`) that runs in another
network / cloud region, asks the main app for due checks, runs them and posts the results back. **Deploying and
operating the agents is the operator's job**; the main app only manages their credentials and uses their results.

How it works:

- **Pull only.** The agent opens no ports. Every few seconds it calls `POST /api/agent/v1/lease` and receives the
  checks due in its region (type, target, port, keyword, expected codes, method, timeout, DNS / JSON assertion
  fields). It never receives customer or monitor names or ids, alert channels, webhook URLs or other secrets. It
  posts outcomes (incl. phase timings) to `POST /api/agent/v1/results`, quoting each lease item id.
- **Same probe code and SSRF rules.** The agent uses the main app's checkers and `TargetGuard`, with
  `Monitoring__AllowPrivateTargets=false` (the image sets it explicitly). Do not enable it in production: probes could
  then reach the agent network's internal addresses.
- **Majority vote.** A region's last result votes when the region is enabled and within the plan's limit, its agent
  called in within `Regions__AgentStaleAfterSeconds`, and the result is younger than `VoteWindowFactor` x interval +
  timeout + `LeaseGraceSeconds`, was not taken in a maintenance window and was taken with the monitor's current
  settings. A success votes UP; a failure votes DOWN only once that region's failure streak reaches the monitor's
  FailureThreshold (single blips abstain). With **three or more** such votes and a strict majority the vote decides:
  DOWN when more than half fail, UP when more than half succeed; a DOWN decided this way lasts at least
  `Regions__MinTransitionSeconds` (hysteresis). In every other case (one or two votes, a split, abstentions) the
  monitor follows central's single-region rules (FailureThreshold + confirm before DOWN), and remote failures only
  serve as the confirmation of a central failure. So with central + one region, a geo-blocked or lying agent can
  neither take a monitor down, keep it down, nor hide an outage central sees. A failure pulls the next probe of the
  regions and central forward to `Monitoring__ConfirmationDelaySeconds`, at most once per interval per monitor (at
  most two probes per region and interval). The incident records the failing regions when the vote decided.
- **Uptime** of a multi-region monitor follows the central check timeline, each check counting as up unless the voted
  status after it was DOWN (the chart's failure marks use the same rule). Remote results are stored per region (shown in the monitor's "Regions" card: latest
  result, 24 h average response time and failures) and are not counted in uptime or the main response-time chart.
- **Plans.** `Plan.MaxRegions` (SuperAdmin > Plans) limits the regions per monitor, central included: 1 = central only
  (Basic, Standard), Pro 3, Enterprise 0 = all. Customers choose regions per monitor (monitor page > Regions > Manage
  regions, CustomerAdmin). Heartbeat monitors are push based and have no regions (changing a monitor to Heartbeat
  removes its regions); DNS monitors can use them (each agent uses its own `Monitoring__DnsResolvers`). After a plan
  downgrade only the first `MaxRegions - 1` assigned regions (by region id) are leased and counted, immediately;
  the others stay assigned (and come back on an upgrade) but are ignored. The public API returns central checks only
  unless `?region=all`; AI baselines and context use central checks only.
- **Not shared:** TLS certificate tracking and expiry alerts use the central probe only.

### Setting up a region

1. SuperAdmin > **Probe regions** > New region: a code (`eu-central`, lower-case letters, digits, dashes) and a
   name. The agent credential (`tcr_...`) is shown **once**; only its SHA-256 hash is stored.
2. Run the agent in the target region:

   ```bash
   docker build -f Dockerfile.probe -t triggercheck-probe .
   docker run -d --name tc-probe-eu --restart unless-stopped \
     -e Agent__CentralUrl=https://app.example.com \
     -e Agent__RegionId=eu-central \
     -e Agent__Credential="$TC_AGENT_CREDENTIAL" \
     triggercheck-probe
   ```

3. The region shows "Active" with a recent "Agent last seen" time. Customers can now add it to monitors.

| Variable | Required | Purpose |
|---|---|---|
| `Agent__CentralUrl` | **yes** | Public base URL of the main app (https; a path base is kept). |
| `Agent__RegionId` | **yes** | Region code. Must match the credential's region, otherwise every request gets 401. |
| `Agent__Credential` | **yes** | The `tcr_...` credential. Keep it in a secret store. |
| `Agent__PollIntervalSeconds` (2), `Agent__MaxConcurrency` (20) | no | Poll frequency; probes in parallel (also the lease size). |
| `Agent__ClientCertificatePath`, `Agent__ClientCertificatePassword` | no | PFX client certificate for mutual TLS (see below). |
| `Agent__AllowInsecureCentral` | development only | Allows an http `CentralUrl`. |
| `Monitoring__DnsResolvers__0`, ... | no | Resolvers of DNS monitors in this region. |
| `Monitoring__AllowPrivateTargets` | **never in production** | Turns the agent's SSRF protection off. |

The agent exits at start with a log line per invalid setting. Rejected credentials are logged (without the
credential) and retried with backoff up to 60 s.

**Network:** the agent needs outbound HTTPS to the main app and outbound access to whatever customers monitor
(HTTP(S) 80/443 and other ports, TCP to any port for TCP monitors, UDP/TCP 53 to its DNS resolvers, ICMP for ping
monitors: in containers ICMP usually needs `--cap-add NET_RAW`, otherwise ping monitors report DOWN from that
region). No inbound rules are needed.

**Time sync:** run NTP (chrony / systemd-timesyncd) on agent hosts. Probe times are taken from the agent clock; a
reported time outside the lease (+/- `Regions__ClockSkewSeconds`) is replaced by the server's estimate, and a large
skew makes the region's results look stale (they then stop voting).

**Rotation and disabling:** SuperAdmin > Probe regions > *Rotate credential* issues a new credential (shown once)
and the old one is rejected from the next request; update `Agent__Credential` and restart the agent. *Disable*
rejects the agent immediately, cancels its outstanding leases and removes the region from all votes (assignments
are kept, so *Enable* restores them). Rotate after any suspected leak and when an operator with access leaves.

**Mutual TLS (optional):** the agent API also works with the bearer credential alone. For defense in depth, put the
main app behind a reverse proxy that requires a client certificate on `/api/agent/` only, and give each agent a
certificate via `Agent__ClientCertificatePath` / `Agent__ClientCertificatePassword` (PFX). Behind a proxy enable
`ForwardedHeaders__*` so the per-IP limit sees the agent's address.

**Limits:** the agent API allows `Regions__AgentRequestsPerMinute` requests per region (plus the per-IP limit of all
`/api` requests), bodies of at most 1 KB (lease) / 256 KB (results) and 100 results per request. A result is accepted
only for the region's own, unexpired lease item, once; foreign, replayed or late results are ignored, and a result for
a monitor that was edited, paused or deleted after the lease is discarded.

**Scaling note:** results are applied in the main app process. With several app instances any of them may serve an
agent: leases are claimed conditionally in the database and results meet on the monitor's concurrency token.

**Upgrading an existing installation:** FEAT-6 adds tables (`Regions`, `MonitorRegionStates`) and columns
(`CheckResults.RegionId`/`VotedUp`, `Incidents.FailedRegions`, `Plans.MaxRegions`, `Monitors.LastProbeFingerprint`/
`RegionsExpeditedAtUtc`). Until the EF migrations (OPS-1) exist, the schema is created by `EnsureCreated` only on an
empty database: back up and recreate the database, or apply the migration once OPS-1 lands (`Plans.MaxRegions` must
default to 1, so existing plans keep central-only checks). Agents are optional: without regions nothing changes.

