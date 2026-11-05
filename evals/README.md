# AI evals (AI-11)

Quality gate for the AI features. The harness runs labelled, synthetic scenarios through the **production path**:
each case is stored in a scratch SQLite database, the feature's registered context provider builds the model input
(the same masking, untrusted-data blocks and prompt as in production), `AiInterpreter` makes the call (schema, budget,
validation) and the grader scores the exact request and the raw model output.

## The gate

- A change to a **prompt** (`src/TriggerCheck.Ai/Execution/AiPrompts.cs`, `src/TriggerCheck.Ai/Features/AiFeaturePrompts.cs`,
  `src/TriggerCheck.Ai/Features/Correlation/CorrelationPrompts.cs`, `src/TriggerCheck.Ai/Narrative/NarrativeOutput.cs` for AI-6, `src/TriggerCheck.Ai/Features/Postmortem/PostmortemOutput.cs` for AI-8, `src/TriggerCheck.Ai/Insights/InsightSummary.cs` for AI-12),
  a context provider, the output schema, a **model** (`Ai:Model`, `Ai:Features:<Name>:Model`) or an **effort** level
  is merged only after a real run (below) passes for the affected feature: primary-cause accuracy, evidence rate and
  language rate each **≥ 80 %**, **zero** masking failures, and for AI-3 the "no outage yet" statement in ≥ 80 % of
  cases; for AI-6 the focus-monitor rate and the **number grounding** rate (the "Primary cause" and "Evidence"
  columns, see below) each ≥ 80 %; for AI-8 the cause, grounded-output and language rates each ≥ 80 % and **no** invented number. Paste the summary table of `evals/reports/last-report.md` into the pull request.
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
--feature IncidentInterpretation | SlowdownExplanation | CorrelationGrouping | QuestionAnswer | MonitorSetupAssistant | NarrativeReport | PostmortemDraft | PlatformInsights | ...   only one feature
--case tls                                                only cases whose id contains "tls"
--model claude-haiku-5-5  --effort medium                 override Ai:Model / Ai:Effort for the run
--threshold 0.8                                           gate threshold
--cases <dir>  --out <dir>                                defaults: evals/cases, evals/reports
```

The exit code is 0 when the gate passes, 1 when it fails, 2 for usage errors. A real run of the 28 AI-2 / AI-3 cases costs
roughly 28 × (4k input + 1.5k output tokens) ≈ 0.11M input + 0.04M output tokens, i.e. about **$1.30 with
`claude-opus-5-5`** at list prices (less with prompt caching); the 13 AI-4 cases add roughly 13 × (2k input + 1k
output tokens), about $0.35. The report shows the measured tokens and cost per case.
The 11 AI-6 cases add roughly 11 × (3k input + 1k output tokens), about $0.35; the 10 AI-8 cases roughly 10 × (2k input + 1k output
tokens), about $0.30 (more with `Ai:Features:PostmortemDraft:Effort` = `medium`).

## Layout

```
evals/
  README.md
  TriggerCheck.Evals/              console app: scenario loader, harness, grader, report
  cases/
    incident-interpretation/       AI-2: 23 labelled scenarios (en + tr)
    slowdown-explanation/          AI-3: 5 labelled scenarios
    narrative-report/              AI-6: 11 monthly report scenarios (en + tr)
    correlation-grouping/          AI-4: 13 labelled scenarios (candidate verdicts, en + tr)
    settings-suggestions/          AI-5: 8 labelled scenarios (one per suggestion kind, en + tr)
    question-answer/               AI-7: 8 Q&A scenarios (tool loop; en + tr, tenant isolation, injection, write request)
    monitor-setup/                 AI-9: 14 labelled requests (every monitor type, clarification, clamping, masking; en + tr)
    postmortem-draft/              AI-8: 10 resolved incidents (main causes, host outage, maintenance, prompt injection; en + tr)
    superadmin-insights/           AI-12: 7 platform scenarios (noise, plan limits, AI quota / SMS, abandoned, masking; en + tr)
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
`NarrativeReportProvider` computes every figure through the FEAT-10 report service and offers it as a placeholder key
(`A.uptime`, `total.incidents`, `change.downtime`, ... see `reports/prompts/<case>.txt`). The `canned` answer, like the
model's, contains **no numbers at all**: it writes `{{A.downtime}}` and the server renders the value.

```json
{
  "id": "narrative-sla-missed", "feature": "NarrativeReport", "language": "en",
  "expect": { "focusMonitors": ["Checkout API"] },
  "narrative": { "monitors": [ { "name": "Checkout API", "target": "https://shop.example.test/api/checkout", "slaTarget": 99.9,
                 "ms": 210, "previousMs": 200, "incidents": [ { "day": 10, "hour": 14, "minutes": 120, "cause": "HTTP 503" } ] } ] },
  "canned": { "headline": "...", "what_happened": [ { "text": "monitor A was down for {{A.downtime}} ...", "monitors": ["monitor A"] } ],
              "what_changed": [], "what_to_watch": [] }
}
```

AI-4 cases use `"feature": "CorrelationGrouping"`, a `group` with the customer's incidents around the outage
(`name`, `type`, `target`, `startedMinutesAgo`, `cause`, optional `resolvedMinutesAgo` / `port`) and
`"expect": { "primaryCause": "candidate_verdicts", "keep": [names], "leaveOut": [names] }`. The production rules
(`IncidentCorrelationService.ProposeAsync`) form the group, so the labels the model sees follow from the data:
`incident A` is the earliest incident, then the rule members, then the candidates, each ordered by start time.

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

- **AI-4** (`EvalCorrelation`): "primary cause" is the candidate verdicts: the candidates the validated output
  recommends (`CorrelationPrompts.Recommended`, the same rule that pre-selects them in the UI: a `shared_cause`
  hypothesis of at least medium confidence citing `incident X`, not outweighed by an `independent` / `unclear` one)
  must be exactly `keep`, and none of `leaveOut`. Evidence: every hypothesis cites an `incident X` / `error X` key that
  was sent. Masking: no monitor name, URL path / query, planted secret or e-mail address in the request. The A3
  promotion of AI-4 additionally needs > 90 % acceptance of the A1 proposals over four weeks (K3).

The report also shows, as a diagnostic only, which cause categories the free text mentions ("text mentions").

AI-6 (`EvalGrader.GradeNarrative`) reuses the columns:

- **Primary cause** = focus: every `focusMonitors` entry is referenced by its label in `monitors` of a "what happened"
  or "what to watch" item of the validated narrative (labels, not text matching).
- **Evidence** = no numbers and correct facts: the raw model output passes the production narrative contract
  (`NarrativeOutputContract`: no digits or number words, only known placeholders bound to the monitor named before
  them, no SLA verdict or quantity in words, no links or contacts) **and** an independent grader check
  (`EvalGrader.WrongClaims`, not the production contract): no digit outside placeholders, a text about one monitor uses
  only its placeholders, and the values the provider computed equal the case's hand-computed `expect.monitorFacts`
  (uptime, SLA target, SLA result, incidents). A test feeds the review's hostile prompt-injection answer through this
  and expects it to fail both.
- **Masking**: no monitor name, URL path, planted secret or e-mail address in the request (monitors are sent as
  "monitor A", "monitor B", ...).
- **Language**: as for AI-2.

AI-9 cases (`"feature": "MonitorSetupAssistant"`) have a `request` (the text typed into the assistant box), an
optional `planMinIntervalSeconds` (default 15) and `"expect": { "primaryCause": "setup_form" | "setup_clarify",
"fields": { "type": "Keyword", "target": "...", "intervalSeconds": 60, ... }, "notes": ["IntervalRaisedToPlanMinimum"] }`.
The harness sends the request through the production `MonitorSetupAssistant` and grades the **final form** (after the
server restored the user's links / quoted text and clamped the values), so "primary cause" in the report means
"outcome and every labelled field match". Masking fails when a planted secret, a link path / query or an e-mail address
reaches the model; language checks the explanation (or the clarification question). Evidence is not graded (no
hypotheses). `canned` is the model's JSON answer using the placeholders `[U1]` / `[T1]` as the model sees them.

Labels that need judgement (for example `maintenance_aftermath`) are kept few; when a real run disagrees with a label,
look at `reports/prompts/<case>.txt` and the model's answer before changing either.

## AI-8 (postmortem / status page draft)

Cases (`"feature": "PostmortemDraft"`) use the AI-2 format with a **resolved** incident (`resolvedMinutesAgo`) and an
AI-2 label (`"expect": { "primaryCause": "http_5xx" }`, optional `notInAnswer`). The production
`PostmortemDraftProvider` computes the timeline and impact from the seeded data and offers them only as placeholder keys
(`incident.duration`, `incident.started_at`, `incident.error`, `maintenance.ended_at`, `{{service}}` for the public
name, ... see `reports/prompts/<case>.txt`); the `canned` answer, like the model's, writes no numbers:

```json
"canned": { "likely_cause": { "category": "http_5xx", "confidence": "high", "text": "The checks failed with {{incident.error}} for {{incident.duration}}." },
            "customer_message": "{{service}} was unavailable from {{incident.started_at}} to {{incident.resolved_at}}. It is resolved.",
            "unknowns": [] }
```

Graded by `EvalPostmortem`: **primary cause** = the validated `likely_cause.category` is accepted for the label;
**evidence** = grounded, publishable output (a customer message survived, the production contract removed no sentence,
every placeholder was offered, the rendered message contains neither the monitored host nor `notInAnswer` / secrets);
**numbers** = an independent check that no raw text has a digit outside placeholders (one invented number fails the
gate); **masking** and **language** (of the customer message) as for AI-2. A test feeds a hostile answer (invented
figures, an injected refund link, a wrong cause) through all cases and expects the gate to fail.

## AI-7 (questions in natural language)

Q&A cases (`"feature": "QuestionAnswer"`, label `qa_grounded_answer`) describe the customer's monitors with their
checks and incidents (`qaMonitors`), optionally another tenant's monitors (`otherTenant`), the `question`, and in fake
mode the scripted model turns (`cannedTurns`: `{"tools": [{"name": ..., "input": {...}}]}` or `{"answer": "..."}`;
placeholders `"{{monitor_id:Name}}"`, `"{{incident_id:Name#1}}"` become ids, `{{monitor:Name}}` /
`{{incident:Name#1}}` reference tokens). The question runs through the production `QuestionAnswerService` (prompt,
tools, caps, budget); a real run lets the model choose its own tool calls. The report columns mean:

- **Primary cause**: the answer cites every record in `expect.references` and used every tool in `expect.tools`.
- **Evidence**: every record the answer cites was returned by a tool in that conversation.
- **Masking**: no monitor name (own or the other tenant's), URL path / query, planted secret, e-mail address or other
  tenant's host appears in what was sent, and the answer contains none of `secrets` / `expect.notInAnswer`
  (e.g. a link planted in an error text).
- **Language** and **Numbers** (every number of the answer is in the tool results or the question) as above.

A real run of the 8 cases makes 2-4 calls each with a larger prompt (tools + results), roughly 8 × 3 × 4k input +
8 × 3 × 0.5k output tokens, about **$0.65 with `claude-opus-5-5`** at list prices (less with caching).

## AI-12 (SuperAdmin platform insights)

Cases (`"feature": "PlatformInsights"`, label `insights_summary`) describe the platform: `insights.customers`, each on
its own plan (`maxMonitors`, `minIntervalSeconds`, `maxStatusPages`, `maxRegions`, `allowsSmsVoice`, `aiCallLimit`,
`isActive`) with usage (`statusPages`, `smsPeak`, `aiCalls`, `acknowledgedDaysAgo`) and `monitors` (`status`,
`statusDays`, `intervalSeconds`, `remoteRegions`, `incidents`, `flaps`, `alerts`, `repeat`), plus
`"expect": { "focusCustomers": [names] }`. Each case gets its own scratch database (the signals are platform-wide); the
production `InsightRules` compute the signals and the production `InsightSummaryService` makes the call (labels,
prompt, contract, platform budget). Customers are labelled `C1, C2, ...` in signal order (noise, then plan-limit
pressure, then abandoned; within a signal most significant first) and monitors `M1, M2, ...`; see
`reports/prompts/<case>.txt`. The `canned` answer is `{"headline": ..., "points": [...]}` with `[[C1]]` labels and
`{{C1.noise.incidents}}` placeholders, no numbers. Columns:

- **Primary cause** = focus: every `focusCustomers` entry is referenced by its `[[label]]` in the validated summary.
- **Evidence** = grounded: the raw output passes the production contract with nothing removed **and** an independent
  grader check (`InsightsEval.WrongClaims`): no digit outside placeholders / labels, no placeholder that was not
  offered, no label that was not sent or that points to a record that does not exist (no invented numbers or customers).
- **Masking**: no customer or monitor name, target host, contact e-mail or planted secret in the request.
- **Language** as above.

A real run of the 7 cases is about 7 × (1.5k input + 0.5k output tokens), roughly **$0.10 with `claude-opus-5-5`**.

## Later

- Cases recorded from the local UAT test servers (ports 8765 / 8767 / 8799, docs/UAT-REPORT.md) and from 👎 feedback in
  `AiDecisions` (exported without customer data).
- AI-6 weekly narratives use the same provider and contract; add weekly cases when the weekly view gets real use.
