# TriggerCheck REST API (v1)

Tenant-scoped JSON API for monitors, checks, incidents and a status summary. Everything a key can see or change
belongs to the customer that owns the key; a customer id is never accepted from the client, and ids of other
customers answer `404`.

- Base URL: `https://<host>/api/v1`
- OpenAPI document: [`/api/v1/openapi.json`](/api/v1/openapi.json) (anonymous, contains no data)
- Messages in API responses are English.

## API keys

Create keys in the panel under **Settings > API keys** (`/App/ApiKeys`, CustomerAdmin only; viewers see the list).

- Format: `tc_<prefix>_<secret>`. The full key is shown **once**, right after creation. Only a SHA-256 hash is
  stored; the public prefix identifies the key in the list.
- Access: **Read-only** (GET endpoints) or **Read & write** (all endpoints).
- Optional expiry (30 / 90 / 365 days or never). Revoked or expired keys, and keys of suspended customers, get `401`.
- "Last used" is updated at most once a minute per key.
- Keys belong to the customer, not to the user who created them: they keep working after that user is deactivated or
  deleted. Revoke keys you no longer need. At most 20 active keys per customer. A SuperAdmin can list a customer's
  keys and revoke them all at once (SuperAdmin > Customers > customer > API keys).
- After creation the panel redirects and shows the key once (kept server-side for at most 5 minutes until that page
  is opened; never stored in a cookie). Refreshing does not show it again or create another key.

Send the key as a bearer token:

```bash
export TC=https://triggercheck.example.com
export TC_KEY=tc_abcdefghij_...
curl -H "Authorization: Bearer $TC_KEY" $TC/api/v1/status
```

A browser session (cookie) never authenticates the API, and an API key never opens panel pages.

## Rate limit

Three limits apply to `/api` requests; over any of them the API answers `429 Too Many Requests` (problem+json) with a
`Retry-After` header (seconds):

| Limit | Default | Setting | Notes |
|-------|---------|---------|-------|
| Per API key | 120 / min (fixed window) | `Api:RequestsPerMinute` | |
| Per customer, all keys together | 300 / min (fixed window) | `Api:RequestsPerMinutePerCustomer` | |
| Per client IP | 600 / min (sliding window) | `Api:RequestsPerMinutePerIp` | Applied before authentication, so it also throttles invalid-key floods. Behind a reverse proxy configure forwarded headers, otherwise all clients share the proxy's IP. |

Limits are kept in memory per app instance.

## Errors

Errors use [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807) `application/problem+json`:

| Status | When |
|--------|------|
| 400 | Validation failed (`errors` maps each field to its messages), malformed JSON, bad query parameter |
| 401 | Missing / invalid / revoked / expired key, or the customer is suspended |
| 403 | Write with a read-only key |
| 404 | Unknown id or an id of another customer |
| 409 | Plan monitor quota reached, or a concurrent change (retry) |
| 415 | Body is not JSON, or has an unknown charset |
| 429 | Rate limit exceeded (see `Retry-After`) |
| 500 | Unexpected error (generic message; details are only logged) |
| 503 | Database busy; retry after `Retry-After` |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "intervalSeconds": ["This check interval is not available on your plan (Basic). Allowed: 5 min."] }
}
```

## Endpoints

| Method | Path | Scope | Description |
|--------|------|-------|-------------|
| GET | `/monitors` | read | All monitors of the customer |
| GET | `/monitors/{id}` | read | One monitor |
| POST | `/monitors` | write | Create (`201` + `Location`) |
| PUT | `/monitors/{id}` | write | Replace the monitor's settings (omitted fields get their defaults) |
| DELETE | `/monitors/{id}` | write | Delete with its history (`204`) |
| POST | `/monitors/{id}/pause` | write | Pause (closes an open incident); idempotent |
| POST | `/monitors/{id}/resume` | write | Resume (status `Unknown`, checked right away); idempotent |
| GET | `/monitors/{id}/checks?from&to&limit` | read | Check results, newest first. Default: last 24 h, `limit` 100 (max 1000). Times in ISO 8601 |
| GET | `/incidents?status=open\|resolved&monitorId&limit` | read | Incidents, newest first. `limit` 100 (max 500) |
| GET | `/status` | read | Counts by status, open incidents, 24 h uptime, plan limits |

### Monitor body (POST / PUT)

The same fields and rules as the panel's monitor form: plan interval and monitor quota, private / internal targets
rejected (SSRF protection), type-specific fields.

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `name` | string | (required) | max 200 |
| `type` | `Http` \| `Keyword` \| `Tcp` \| `Ping` | `Http` | |
| `target` | string | (required) | URL for Http/Keyword, host or IP for Tcp/Ping |
| `port` | int | | required for Tcp (1-65535) |
| `keyword` | string | | required for Keyword |
| `httpMethod` | `GET` \| `HEAD` \| `POST` | `GET` | Http/Keyword |
| `expectedStatusCodes` | string | `200-299` | e.g. `200-299,301` |
| `intervalSeconds` | 300 \| 60 \| 30 \| 15 | 300 | must be allowed by the plan |
| `timeoutSeconds` | int | 10 | 1-60, below the interval |
| `failureThreshold` | int | 2 | 1-5 |

Responses contain these fields plus `id`, `status` (`Unknown`/`Up`/`Down`/`Paused`), `isPaused`, `lastCheckedAtUtc`,
`nextCheckAtUtc`, `lastResponseTimeMs`, `lastError`, `statusChangedAtUtc`, `createdAtUtc` (UTC, ISO 8601).

## Examples

```bash
# List monitors
curl -H "Authorization: Bearer $TC_KEY" $TC/api/v1/monitors

# Create an HTTP monitor checked every minute
curl -X POST -H "Authorization: Bearer $TC_KEY" -H "Content-Type: application/json" \
  -d '{"name":"Website","type":"Http","target":"https://example.com/","intervalSeconds":60}' \
  $TC/api/v1/monitors

# Change it to a keyword monitor
curl -X PUT -H "Authorization: Bearer $TC_KEY" -H "Content-Type: application/json" \
  -d '{"name":"Website","type":"Keyword","target":"https://example.com/","keyword":"Welcome","intervalSeconds":60}' \
  $TC/api/v1/monitors/42

# Pause during maintenance, then resume
curl -X POST -H "Authorization: Bearer $TC_KEY" $TC/api/v1/monitors/42/pause
curl -X POST -H "Authorization: Bearer $TC_KEY" $TC/api/v1/monitors/42/resume

# Checks of the last hour
curl -H "Authorization: Bearer $TC_KEY" "$TC/api/v1/monitors/42/checks?from=$(date -u -v-1H +%Y-%m-%dT%H:%M:%SZ)&limit=500"

# Open incidents
curl -H "Authorization: Bearer $TC_KEY" "$TC/api/v1/incidents?status=open"

# Delete
curl -X DELETE -H "Authorization: Bearer $TC_KEY" $TC/api/v1/monitors/42
```

(`date -v-1H` is the macOS / BSD form; on Linux use `date -u -d '-1 hour' +%Y-%m-%dT%H:%M:%SZ`.)
