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
| `Site__ContactEmail` | no | Sales / support address shown on the marketing pages. It also receives operator alerts, such as "refund needed" for a refused payment. |
| `Site__PublicBaseUrl` | **yes outside Development** | Public address of the app, e.g. `https://app.example.com`. Used in e-mail links (sign-up confirmation, password reset), for the iyzico callback, for the "Acknowledge" link in DOWN and escalation alerts, and for heartbeat ping URLs. **The app refuses to start without it** in any environment other than Development. The former `Billing__PublicBaseUrl` is still read as a fallback. |
| `Escalation__CheckIntervalSeconds` (30), `Escalation__AckLinkValidHours` (72) | no | How often the escalation job runs; lifetime of acknowledge links. |
| `Monitoring__DnsResolvers__0`, `__1`, ... | no (1.1.1.1, 8.8.8.8) | Resolvers for DNS monitors, tried in order: `ip`, `ip:port` or `[ipv6]:port`. Operator configuration (not subject to the SSRF guard). |
| `Heartbeat__PingsPerMinutePerToken` (10), `Heartbeat__PingsPerMinutePerClient` (6000), `Heartbeat__PingsPerSecondGlobal` (1000) | no | Limits of the `/hb/{token}` ping endpoint. The per-client limit is per IPv4 address / IPv6 /64 and deliberately high (many hosts behind one NAT). **Behind a reverse proxy enable ForwardedHeaders**, otherwise all pings share the proxy's address. |
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
| `Billing__MaintenanceIntervalMinutes` (15) | no | How often the billing job runs. `0` disables it. Run it in **one** instance only, the same as the monitoring scheduler. Every step is idempotent, and its state changes are conditional. |
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
| `Ai__QueueCapacity` (200), `Ai__MaxConcurrency` (2) | | Background AI queue. AI calls never run on a request or check path; a full queue drops the job (logged). |
| `Ai__Pricing__<model>__InputPerMTok`, `__OutputPerMTok`, `__CacheReadPerMTok`, `__CacheWritePerMTok` | list prices | USD prices for cost estimates (SuperAdmin *AI usage*). |
| `Ai__Baseline__Enabled` (true), `__IntervalSeconds` (60), `__RefreshMinutes` (60), `__WindowDays` (7), `__ZThreshold` (4), `__K` / `__N` (3 of 5), `__CloseAtMost` (1) | | L1 baseline / anomaly job (no LLM, no key needed). Runs only when `Monitoring__Enabled` is true. |
| `Ai__Events__PollSeconds` (10), `Ai__Events__LookbackMinutes` (15) | | Event watcher of AI-2 / AI-3: how often new / resolved incidents and new slowdown signals are looked up, and how old an event without an AI decision may be to still be picked up after a restart. Runs only when AI is on and a key is set. |
| `Ai__Events__MonitorCooldownMinutes` (30), `__HostWindowMinutes` (10), `__MaxJobsPerCustomerPerScan` (5) | | Cost damping of AI-2: a monitor whose incident was interpreted within the cool-down gets no new summary for the same (normalised) cause (flapping; a different cause is interpreted, at most once per cool-down); of incidents of one customer on the same host within the window only the first is interpreted (its page is linked from the others); per scan at most this many jobs per customer, round-robin across customers. Events over the cap or refused by a full queue are retried on the next scan. |

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

- **Calls:** calls that returned token usage, plus pending calls. Calls that failed before the model answered (network, rate limit) are free.
- **Tokens:** input + cache writes + output, with cache reads counted at 1/10.

**Privacy (decision K5):** only masked technical data is sent: monitor type, **host name** (URLs are reduced to scheme
+ host; paths, query strings and fragments are dropped), status codes, error types and timings. These are masked first:

- URL credentials and webhook URLs;
- e-mail addresses and phone numbers;
- key/value secrets (`Authorization`, `api_key`, `*_password`, `client_secret`, quoted JSON values, ...);
- bare credential shapes: JWTs and other dot-separated opaque tokens, `sk-…` keys, AWS keys, Telegram / GitHub / GitLab / Slack tokens, grouped license-style keys, and any long letter+digit token (base64 included). System-generated ids skip only this last rule.

Customer-entered text is not sent: monitor names (the model sees "this monitor", "monitor B", ...), keywords, JSON
paths, expected values, maintenance window names and acknowledgement notes. Heartbeat monitors send no target at all.

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
