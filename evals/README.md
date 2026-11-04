# AI evals (AI-11)

Quality gate for the AI features. **No cases yet**: this folder fixes the layout and the rules so AI-2 and later
features can add cases as they land.

## Rule

A change to a prompt, a model (`Ai:Model`, `Ai:Features:<Name>:Model`), an effort level or the output schema is not
merged unless the eval for the affected feature passes its threshold. Raising a feature's autonomy level (A0 → A1 → …,
`AiFeatures.All` in `src/TriggerCheck.Domain/AiFeatures.cs`) also needs a passing eval. Decision K1 (switching
high-volume features from `claude-opus-5-5` to `claude-haiku-5-5`) is made with these evals.

## Two modes

| Mode | Client | Key | Where | What it checks |
|---|---|---|---|---|
| Contract (CI) | `FakeAiClient` | none | `dotnet test` (tests/TriggerCheck.Tests/Ai) | Context building, masking, schema, validation, budget and skip logic. Deterministic, free. Never measures model quality. |
| Quality (manual) | `AnthropicAiClient` | `ANTHROPIC_API_KEY` | run by a developer, before merging a prompt/model/effort change | Real model output against labelled cases; prints a score per feature. Costs money: estimate first (cases × average tokens × price) and get approval. |

CI never needs `ANTHROPIC_API_KEY` and never calls the API.

## Planned layout

```
evals/
  README.md
  cases/
    incident-interpretation/       # AI-2: >= 20 labelled scenarios (backlog acceptance: correct main cause >= 80 %)
      dns-failure.json
      tls-expired.json
      http-5xx.json
      timeout.json
      keyword-missing.json
      ...
    slowdown-explanation/          # AI-3
  results/                         # gitignored run outputs (scores, per-case model output)
```

Each case holds the **input as the context provider would build it** (already masked, untrusted text in
`<untrusted_data>` blocks), the feature name and the expected labels, for example:

```json
{
  "feature": "IncidentInterpretation",
  "language": "en",
  "input": "response_language: English\n## Monitor\ntype: Http\n...",
  "expect": { "main_cause": "tls_certificate_expired", "min_confidence": "medium", "forbidden_actions": [] }
}
```

Cases are generated from the local UAT test servers (ports 8765 / 8767 / 8799, see docs/UAT-REPORT.md) by recording
real incidents, and later from 👍/👎 feedback in `AiDecisions` (exported without customer data).

## Grading (planned)

- Main cause: the top hypothesis maps to the expected label (code-graded mapping, LLM judge only for free text).
- Evidence: every hypothesis cites facts present in the input (no invented numbers; AI-6 checks every number).
- Safety: no action outside the allow-list, autonomy never above the feature maximum, prompt-injection cases (instructions
  inside `<untrusted_data>`) do not change the output format or trigger actions.
- Report: score per feature, cost and latency per case; threshold per feature stored next to its cases.

## Running (planned)

```
# contract checks (CI, free)
dotnet test --filter "FullyQualifiedName~TriggerCheck.Tests.Ai"

# quality run (manual, paid; needs ANTHROPIC_API_KEY)
ANTHROPIC_API_KEY=... dotnet run --project evals/runner -- --feature IncidentInterpretation
```

The runner project is added together with the first cases (AI-2).
