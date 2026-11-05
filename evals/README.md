# AI evals (AI-11)

Quality gate for the AI features. The harness runs labelled, synthetic scenarios through the **production path**:
each case is stored in a scratch SQLite database, the feature's registered context provider builds the model input
(the same masking, untrusted-data blocks and prompt as in production), `AiInterpreter` makes the call (schema, budget,
validation) and the grader scores the exact request and the raw model output.

## The gate

- A change to a **prompt** (`src/TriggerCheck.Ai/Execution/AiPrompts.cs`, `src/TriggerCheck.Ai/Features/AiFeaturePrompts.cs`,
  `src/TriggerCheck.Ai/Narrative/NarrativeOutput.cs` for AI-6),
  a context provider, the output schema, a **model** (`Ai:Model`, `Ai:Features:<Name>:Model`) or an **effort** level
  is merged only after a real run (below) passes for the affected feature: primary-cause accuracy, evidence rate and
  language rate each **≥ 80 %**, **zero** masking failures, and for AI-3 the "no outage yet" statement in ≥ 80 % of
  cases; for AI-6 the focus-monitor rate and the **number grounding** rate (the "Primary cause" and "Evidence"
  columns, see below) each ≥ 80 %. Paste the summary table of `evals/reports/last-report.md` into the pull request.
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
--feature IncidentInterpretation | SlowdownExplanation | NarrativeReport    only one feature
--case tls                                                only cases whose id contains "tls"
--model claude-haiku-5-5  --effort medium                 override Ai:Model / Ai:Effort for the run
--threshold 0.8                                           gate threshold
--cases <dir>  --out <dir>                                defaults: evals/cases, evals/reports
```

The exit code is 0 when the gate passes, 1 when it fails, 2 for usage errors. A real run of all 47 cases costs
roughly 47 × (4k input + 1.5k output tokens) ≈ 0.19M input + 0.07M output tokens, i.e. about **$2.20 with
`claude-opus-5-5`** at list prices (less with prompt caching); the report shows the measured tokens and cost per case.

## Layout

```
evals/
  README.md
  TriggerCheck.Evals/              console app: scenario loader, harness, grader, report
  cases/
    incident-interpretation/       AI-2: 23 labelled scenarios (en + tr)
    slowdown-explanation/          AI-3: 5 labelled scenarios
    narrative-report/              AI-6: 11 monthly report scenarios (en + tr)
    settings-suggestions/          AI-5: 8 labelled scenarios (one per suggestion kind, en + tr)
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
  "canned": { "summary": "...", "hypotheses": [ { "cause": "...", "confidence": "high", "evidence": ["..."],
              "category": "tls_expired", "evidence_refs": ["validation_error", "error E1"] } ],
              "suggested_actions": [ ... ], "unknowns": [ ... ] }
}
```

AI-3 cases use `"feature": "SlowdownExplanation"`, an `anomaly` (the subject) and `"expect": { "primaryCause":
"slow_ttfb", "noOutageStatement": true }`. Add a case by copying one, changing the data and the label, and writing a
correct `canned` answer (the fake run must stay at 100 %).

AI-6 cases use `"feature": "NarrativeReport"`, a `narrative` block with the customer's monitors (SLA target, daily
response time `ms` / `previousMs`, `incidents` by day of the month, `previous: true` for the month before,
`certExpiresDaysAfterPeriod`, `slowdowns`) and `"expect": { "focusMonitors": ["Checkout API"] }`. The report month is
February 2026 (the month before the fixed clock), compared with January. The harness seeds the data and the production
`NarrativeReportProvider` computes every figure through the FEAT-10 report service; the `canned` answer may only use
numbers that appear in the resulting facts (see `reports/prompts/<case>.txt`).

```json
{
  "id": "narrative-sla-missed", "feature": "NarrativeReport", "language": "en",
  "expect": { "focusMonitors": ["Checkout API"] },
  "narrative": { "monitors": [ { "name": "Checkout API", "target": "https://shop.example.test/api/checkout", "slaTarget": 99.9,
                 "ms": 210, "previousMs": 200, "incidents": [ { "day": 10, "hour": 14, "minutes": 120, "cause": "HTTP 503" } ] } ] },
  "canned": { "headline": "...", "what_happened": [ { "text": "monitor A was down for 120 minutes ...", "monitors": ["monitor A"] } ],
              "what_changed": [], "what_to_watch": [] }
}
```

## Grading

Code-graded, no LLM judge and no keyword matching in pass / fail (`EvalGrader`). AI-2 and AI-3 hypotheses carry a
`category` (an enum in the output schema, `AiFeaturePrompts.IncidentCategories` / `SlowdownCategories`) and
`evidence_refs` (keys of input lines such as `check #1234`, `ttfb`, `validation_error`, `error E1`). Production drops
every hypothesis whose category is not in the enum or whose refs resolve to nothing that was sent
(`AiInterpretationValidator.ApplyContract`).

- **Primary cause**: the `category` of the highest-confidence hypothesis that survived validation must be one of the
  categories accepted for the case's label (`EvalGrader.Labels`). A top-confidence tie between different categories is
  a hedge and fails (marked **hedged** in the report). The cause text does not matter: "not a DNS problem; HTTP 503"
  with category `http_5xx` passes an `http_5xx` case and fails a DNS case.
- **Evidence**: every raw hypothesis has at least one `evidence_ref` that resolves to a key the production context
  provider sent (`AiPrompt.EvidenceKeys`, recorded by the harness; never parsed from the text, so untrusted text cannot
  create keys) and that fits its category (`AiEvidenceRules`: DNS / HTTP / connection causes need a check or its error
  block, TLS the certificate facts, a host-wide outage another monitor on the host, a slowdown the matching phase;
  generic keys such as `started_at` or `interval_seconds` ground nothing). At least one hypothesis must survive
  validation. Production applies the same rule and drops the rest.
- **Masking**: none of the case's `secrets`, the monitor names, the monitor's URL path / query and no e-mail address
  appear in the request actually sent (system prompt + user content).
- **Language**: the request asks for the case's language and the summary is written in it.
- **No outage statement** (AI-3): the summary says that there is no outage yet.
- **Numbers** (AI-5): every number, date and time in the model's text is backed by the typed facts of the input
  (the shared `NumberGrounding` engine with the strict policy, the same rule production applies: an ungrounded summary
  fails the call, other ungrounded text is dropped; untrusted error text grounds nothing). One invented number fails
  the feature's gate.

AI-5 cases describe the monitor's history (`checks`, and `incidents` with `repeat` / `everyMinutes`); the harness runs
the production `SettingsSuggestionJob` rules on it and explains the suggestion they make. The label is the suggestion
code (`raise_failure_threshold`, `raise_timeout`, `target_may_be_wrong`, `review_keyword`); a case whose history makes
the rules suggest nothing fails. Negative rule cases (normal noise) are unit tests, not eval cases.

The report also shows, as a diagnostic only, which cause categories the free text mentions ("text mentions").

AI-6 (`EvalGrader.GradeNarrative`) reuses the columns:

- **Primary cause** = focus: every `focusMonitors` entry is referenced by its label in `monitors` of a "what happened"
  or "what to watch" item of the validated narrative (labels, not text matching).
- **Evidence** = numbers and claims: the raw model output passes the production narrative contract (shared
  `NumberGrounding` engine on the provider's typed `GroundingFacts`: units, the monitor a statement is about,
  previous-period values, change directions, the 0 / 100 guard, no links or contacts, no SLA "met" for a missed target)
  **and** an independent grader check (`EvalGrader.WrongClaims`, not the production engine) against the case's
  `expect.monitorFacts` (uptime and SLA target per monitor computed by hand, SLA met / missed, incident count), and
  production accepted the narrative. A test feeds the review's hostile prompt-injection answer through this and
  expects it to fail both.
- **Masking**: no monitor name, URL path, planted secret or e-mail address in the request (monitors are sent as
  "monitor A", "monitor B", ...).
- **Language**: as for AI-2.

Labels that need judgement (for example `maintenance_aftermath`) are kept few; when a real run disagrees with a label,
look at `reports/prompts/<case>.txt` and the model's answer before changing either.

## Later

- Cases recorded from the local UAT test servers (ports 8765 / 8767 / 8799, docs/UAT-REPORT.md) and from 👎 feedback in
  `AiDecisions` (exported without customer data).
- Cases for AI-4, AI-5, AI-7 and AI-8 as those features land, each with its own folder and threshold.
- AI-6 weekly narratives use the same provider and contract; add weekly cases when the weekly view gets real use.
