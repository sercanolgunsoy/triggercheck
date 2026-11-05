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
--feature IncidentInterpretation | SlowdownExplanation | QuestionAnswer   only one feature
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
    settings-suggestions/          AI-5: 8 labelled scenarios (one per suggestion kind, en + tr)
    question-answer/               AI-7: 8 Q&A scenarios (tool loop; en + tr, tenant isolation, injection, write request)
    monitor-setup/                 AI-9: 14 labelled requests (every monitor type, clarification, clamping, masking; en + tr)
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
- **Numbers** (AI-5): every number, date and time in the model's text appears in the input (`AiNumberGrounding`,
  the same rule production applies: an ungrounded summary fails the call, other ungrounded text is dropped). One
  invented number fails the feature's gate.

AI-5 cases describe the monitor's history (`checks`, and `incidents` with `repeat` / `everyMinutes`); the harness runs
the production `SettingsSuggestionJob` rules on it and explains the suggestion they make. The label is the suggestion
code (`raise_failure_threshold`, `raise_timeout`, `target_may_be_wrong`, `review_keyword`); a case whose history makes
the rules suggest nothing fails. Negative rule cases (normal noise) are unit tests, not eval cases.

The report also shows, as a diagnostic only, which cause categories the free text mentions ("text mentions").

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

## Later

- Cases recorded from the local UAT test servers (ports 8765 / 8767 / 8799, docs/UAT-REPORT.md) and from 👎 feedback in
  `AiDecisions` (exported without customer data).
- Cases for AI-4 to AI-8 as those features land, each with its own folder and threshold.
