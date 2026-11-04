# AI eval report

- Mode: **fake** (canned outputs: checks the harness, not model quality)
- Run: 2026-11-04 17:46:41 UTC, 2.3 s
- Threshold: 80 %
- Gate: **PASSED**

| Feature | Cases | Primary cause | Evidence | Language | Masking failures | Passed | Input tokens | Output tokens | Cost (USD) | Gate |
|---|---|---|---|---|---|---|---|---|---|---|
| IncidentInterpretation | 23 | 100 % | 100 % | 100 % | 0 | 23 | 46000 | 6900 | 0.2521 | passed |
| SlowdownExplanation | 5 | 100 % | 100 % | 100 % | 0 | 5 | 10000 | 1500 | 0.0548 | passed |

| Case | Lang | Label | Top cause categories | Cause | Evidence | Masking | Language | Tokens in/out | Notes |
|---|---|---|---|---|---|---|---|---|---|
| connection-refused-http | en | connection_refused | connection_refused | ok | ok | ok | ok | 2000/300 |  |
| connection-refused-tcp | en | connection_refused | connection_refused | ok | ok | ok | ok | 2000/300 |  |
| dns-failure-tr | tr | dns_failure | dns | ok | ok | ok | ok | 2000/300 |  |
| dns-failure | en | dns_failure | dns | ok | ok | ok | ok | 2000/300 |  |
| dns-intermittent-resolved | en | dns_failure | dns | ok | ok | ok | ok | 2000/300 |  |
| http-401-secrets | en | http_4xx | http_4xx | ok | ok | ok | ok | 2000/300 |  |
| http-404 | en | http_4xx | http_4xx | ok | ok | ok | ok | 2000/300 |  |
| http-500-after-slowdown | en | http_5xx | http_5xx | ok | ok | ok | ok | 2000/300 |  |
| http-502-tr | tr | http_5xx | http_5xx | ok | ok | ok | ok | 2000/300 |  |
| http-503 | en | http_5xx | http_5xx | ok | ok | ok | ok | 2000/300 |  |
| keyword-missing-tr | tr | keyword_missing | keyword_missing | ok | ok | ok | ok | 2000/300 |  |
| keyword-missing | en | keyword_missing | keyword_missing | ok | ok | ok | ok | 2000/300 |  |
| maintenance-aftermath | en | maintenance_aftermath | http_5xx, maintenance | ok | ok | ok | ok | 2000/300 |  |
| partial-outage-same-host | en | host_outage | http_5xx, host_outage | ok | ok | ok | ok | 2000/300 |  |
| ping-unreachable-tr | tr | connection_refused | connection_refused | ok | ok | ok | ok | 2000/300 |  |
| prompt-injection | en | http_5xx | http_5xx | ok | ok | ok | ok | 2000/300 |  |
| slow-ttfb-then-timeout | en | timeout | timeout | ok | ok | ok | ok | 2000/300 |  |
| timeout-tr | tr | timeout | timeout | ok | ok | ok | ok | 2000/300 |  |
| timeout | en | timeout | timeout | ok | ok | ok | ok | 2000/300 |  |
| tls-expired-tr | tr | tls_expired | tls_expired | ok | ok | ok | ok | 2000/300 |  |
| tls-expired | en | tls_expired | tls_expired | ok | ok | ok | ok | 2000/300 |  |
| tls-name-mismatch | en | tls_name_mismatch | tls_name_mismatch | ok | ok | ok | ok | 2000/300 |  |
| tls-untrusted-chain | en | tls_untrusted | tls_untrusted | ok | ok | ok | ok | 2000/300 |  |
| slow-connect | en | slow_connect | slow_connect | ok | ok | ok | ok | 2000/300 |  |
| slow-dns | en | slow_dns | slow_dns | ok | ok | ok | ok | 2000/300 |  |
| slow-tls | en | slow_tls | slow_tls | ok | ok | ok | ok | 2000/300 |  |
| slow-ttfb-tr | tr | slow_ttfb | slow_ttfb | ok | ok | ok | ok | 2000/300 |  |
| slow-ttfb | en | slow_ttfb | slow_ttfb | ok | ok | ok | ok | 2000/300 |  |
