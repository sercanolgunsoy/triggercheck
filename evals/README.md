# AI evals (AI-11)

Quality gate for the AI features. The harness runs labelled, synthetic scenarios through the **production path**:
each case is stored in a scratch SQLite database, the feature's registered context provider builds the model input
(the same masking, untrusted-data blocks and prompt as in production), `AiInterpreter` makes the call (schema, budget,
validation) and the grader scores the exact request and the raw model output.

## The gate

- A change to a **prompt** (`src/TriggerCheck.Ai/Execution/AiPrompts.cs`, `src/TriggerCheck.Ai/Features/AiFeaturePrompts.cs`),
  a context provider, the output schema, a **model** (`Ai:Model`, `Ai:Features:<Name>:Model`) or an **effort** level
  is merged only after a real run (below) passes for the affected feature: primary-cause accuracy, evidence rate and
  language rate each **≥ 80 %**, **zero** masking failures, and for AI-3 the "no outage yet" statement in ≥ 80 % of
  cases. Paste the summary table of `evals/reports/last-report.md` into the pull request.
- Decision K1 (moving a high-volume feature from `claude-opus-5-5` to `claude-haiku-5-5`) is made with the same run:
  `--model claude-haiku-5-5`.
- **Autonomy upgrades** (raising `MaxAutonomy` in `src/TriggerCheck.Domain/AiFeatures.cs`, e.g. A0 → A1) need a real
  run with primary-cause accuracy **≥ 90 %** on at least 20 cases of that feature, zero masking failures, and, once
  the feature has been live for four weeks, a 👍 share of **≥ 80 %** in `AiFeedback` for it (≥ 90 % acceptance for
  AI-4, decision K3). Record the numbers in docs/BACKLOG.md next to the feature.

## Two modes

| Mode | Client | Key | Where | What it checks |
|---|---|---|---|---|
| Fake (CI) | `FakeAiClient` answering with each case's `canned` output | none | `dotnet test` (`tests/TriggerCheck.Tests/Ai/Features/EvalHarnessTests.cs`) and `dotnet run` without `--real` | The harness itself: fixtures load, seeding, context building, masking of the real request, grading and the report. It never measures model quality. |
| Real (manual) | `AnthropicAiClient` | `ANTHROPIC_API_KEY` | a developer, before merging a prompt / model / effort change | Real model output against the labels. Costs money. |

CI never needs `ANTHROPIC_API_KEY` and never calls the API.

## Running

```
# fake mode (free, deterministic): same as CI
dotnet run --project evals/TriggerCheck.Evals

# real mode (paid). Without --yes it only prints how many cases would be sent.
ANTHROPIC_API_KEY=... dotnet run --project evals/TriggerCheck.Evals -- --real
ANTHROPIC_API_KEY=... dotnet run --project evals/TriggerCheck.Evals -- --real --yes

# options
--feature IncidentInterpretation | SlowdownExplanation    only one feature
--case tls                                                only cases whose id contains "tls"
--model claude-haiku-5-5  --effort medium                 override Ai:Model / Ai:Effort for the run
--threshold 0.8                                           gate threshold
--cases <dir>  --out <dir>                                defaults: evals/cases, evals/reports
```

The exit code is 0 when the gate passes, 1 when it fails, 2 for usage errors. A real run of all 28 cases costs
roughly 28 × (4k input + 1.5k output tokens) ≈ 0.11M input + 0.04M output tokens, i.e. about **$1.30 with
`claude-opus-5-5`** at list prices (less with prompt caching); the report shows the measured tokens and cost per case.

## Layout

```
evals/
  README.md
  TriggerCheck.Evals/              console app: scenario loader, harness, grader, report
  cases/
    incident-interpretation/       AI-2: 23 labelled scenarios (en + tr)
    slowdown-explanation/          AI-3: 5 labelled scenarios
  reports/                         gitignored, except sample-report.md
    last-report.md / .json         last run (per feature and per case, tokens, cost)
    prompts/<case>.txt             the user turn actually sent for each case
```

## Case format

Synthetic data only (`example.test` hosts, no real customer data). Times are minutes before a fixed clock, so prompts
are byte-identical between runs.

```json
{
  "id": "tls-expired",
  "feature": "IncidentInterpretation",
  "language": "en",
  "description": "Certificate expired two days ago.",
  "expect": { "primaryCause": "tls_expired" },
  "monitor": { "name": "Shop API", "type": "Http", "target": "https://shop.example.test/health", "status": "Down",
               "certExpiresInDays": -2, "certError": "RemoteCertificateChainErrors: NotTimeValid (expired)" },
  "incident": { "startedMinutesAgo": 5, "cause": "..." },
  "checks": [ { "minutesAgo": 1, "repeat": 5, "everyMinutes": 1, "up": false, "ms": 30, "error": "..." } ],
  "sameHost": [], "anomaly": null, "maintenance": [], "baselineMedianMs": 180,
  "secrets": ["strings that must never reach the model"],
  "canned": { "summary": "...", "hypotheses": [ ... ], "suggested_actions": [ ... ], "unknowns": [ ... ] }
}
```

AI-3 cases use `"feature": "SlowdownExplanation"`, an `anomaly` (the subject) and `"expect": { "primaryCause":
"slow_ttfb", "noOutageStatement": true }`. Add a case by copying one, changing the data and the label, and writing a
correct `canned` answer (the fake run must stay at 100 %).

## Grading

Code-graded, no LLM judge (`EvalGrader`):

- **Primary cause**: the most confident hypothesis of the raw output is classified into cause categories by English
  and Turkish word-boundary patterns (`dns`, `tls_expired`, `tls_name_mismatch`, `tls_untrusted`, `http_5xx`,
  `http_4xx`, `timeout`, `connection_refused`, `keyword_missing`, `host_outage`, `maintenance`; for AI-3 `slow_dns`,
  `slow_connect`, `slow_tls`, `slow_ttfb`). Status codes count only next to "HTTP" / "status" / "returned" or a reason
  phrase (so "1500 ms" or "check #5004" are not 5xx), and "resolved" is not DNS. The category mentioned **first** is the
  primary cause and must be accepted for the case's label; a hedge that names several categories ("maybe DNS or a
  timeout") has no primary cause and fails. Negative probes are in `EvalHarnessTests`.
- **Evidence**: every raw hypothesis (before validation drops any) cites at least one piece of evidence that is
  grounded in the input: a check id that was sent ("check #1234"), a number of two or more digits, a quoted text or a
  CamelCase identifier (e.g. `RemoteCertificateNameMismatch`) that occurs in the user content.
- **Masking**: none of the case's `secrets`, the monitor names, the monitor's URL path / query and no e-mail address
  appear in the request actually sent (system prompt + user content).
- **Language**: the request asks for the case's language and the summary is written in it.
- **No outage statement** (AI-3): the summary says that there is no outage yet.

Labels that need judgement (for example `maintenance_aftermath`) are kept few; when a real run disagrees with a label,
look at `reports/prompts/<case>.txt` and the model's answer before changing either.

## Later

- Cases recorded from the local UAT test servers (ports 8765 / 8767 / 8799, docs/UAT-REPORT.md) and from 👎 feedback in
  `AiDecisions` (exported without customer data).
- Cases for AI-4 to AI-8 as those features land, each with its own folder and threshold.
