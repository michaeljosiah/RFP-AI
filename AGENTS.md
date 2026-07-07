# AGENTS.md

Grounding for AI agents (and new humans) working in this repository. The README covers *running*
the tool; this file covers *changing* it safely.

## Project

**RfpExtractor** (GitHub: `RFP-AI`) extracts every answerable question from RFP/DDQ questionnaires
(docx/pdf/xlsx) into linked JSON: a document schema + a question list with retrieval tags. .NET 10,
C# records, xUnit. All LLM calls go through Microsoft Agent Framework over `IChatClient`, so any
provider plugs in.

**Flow (owned end-to-end by `ExtractionService` — hosts never re-assemble the steps):**

1. **Extract** two independent legs concurrently — vision (page PNGs) + structured text (markdown
   chunks); Excel is grid-first — each unit retried ×3, streamed, bounded in size. Excel has its own
   two-path grid design (deterministic colour-cell enumeration vs LLM chunks) — see
   [`docs/excel-extraction.md`](docs/excel-extraction.md).
2. **Reconcile** legs one-to-one (verbatim-first keys; optional LLM fuzzy pass), renumber
   `answer_target`s, graft secondary-only items. Result is PRINTED-level: one question per printed
   prompt.
3. **Decompose** (`QuestionDecomposer`) each applicant-facing printed question into atomic
   `parts[]` + retrieval tags — parallel batches, retry ×3, failures surfaced as report warnings
   with the deterministic baseline kept.
4. **Present**: `GranularityView.Apply` renders hybrid (canonical) / bundled / atomic
   `questions.json`; the report shows `printed_questions` AND `answer_slots` (atomic).

## Map

| Project | Contents | Dependencies |
|---|---|---|
| `src/RfpExtractor.Core` | models, prompts, pipelines, reconciler, decomposer, `ExtractionService` | ONLY `Microsoft.Agents.AI` + `Microsoft.Extensions.AI` — keep it that way |
| `src/RfpExtractor.Telerik` | commercial engine adapters (render, spreadsheet) | Telerik DPL (local NuGet feed, see `nuget.config`) |
| `src/RfpExtractor.LibreOffice` | OSS adapters (Gotenberg render, Open XML text — used by BOTH engines, ClosedXML) | MIT/MPL packages |
| `src/RfpExtractor.Cli` | `rfpx` console + `serve` UI; provider wiring (`Wiring`, GenCore handler, `OpenAIV1`) | OpenAI/Azure/Anthropic SDKs, ASP.NET |
| `tests/RfpExtractor.Tests` | 125 tests, all offline/no-credential; corpus tests no-op without local documents | — |

Composition happens in exactly one place per host: `Wiring.CreateService(engine, provider, model,
config)`. If you need the stack, call that — do not new-up pipelines inline.

## Conventions

- **Records + `with`**; lists are mutated only through documented in-place passes
  (`AudienceTagger.Tag`, `DecomposeAsync`). JSON is snake_case via `Json.Json.Options`
  (indented, human-facing) / `Json.Json.Compact` (LLM payloads — indentation is token waste).
- **Model quirks live ONLY in `ModelCapabilities`** (temperature rejection, schema-in-prompt for
  Claude, output-token ceilings, thinking-model chunk caps) and reach call sites as a
  `ModelProfile`. Never special-case a model name elsewhere.
- **Every LLM agent** is built/run through `StructuredAgent` (schema mode, streaming, non-streaming
  fallback, fence-stripping, throw-on-empty). New LLM passes must reuse it and take a
  `ModelProfile`.
- **Every best-effort LLM call** goes through `Resilience` (retry ×3, warning on final failure,
  never abort the run). Silent catch-and-continue is a defect — surface a warning.
- Comments explain **why** (field findings are cited in doc comments — keep doing this).
- No DI container; manual composition. No logging framework; progress via `OnProgress` strings
  parsed by `ServeCommand.ProgressParser` — if you add a progress message format, update the
  parser regexes and the `LEG_LABELS` map in `WebUi.cs`.

## Invariants (breaking these breaks the product)

1. **1:1 answer_target**: every `answer_target` appears exactly once in `document_schema` and once
   in `questions` (`InvariantValidator` warns on violations; keep it green).
2. **Printed-level extraction**: the extraction prompts demand ONE question per printed prompt —
   splitting happens ONLY in the decomposer. Don't "improve" prompts to pre-split; that was the
   source of the 137-vs-75 run-to-run instability this architecture fixed.
3. **Counts have meanings**: `printed_questions` = printed prompts; `answer_slots` = atomic total
   after decomposition; `applicant_slots`/`internal_slots` = audience split. UI and CLI print
   printed·atomic side by side — keep both visible.
4. **Determinism guards in the decomposer**: document requests/uploads are never split; splitting
   by reporting period/time horizon is forbidden; when in doubt keep ONE part. These rules live in
   `Prompts.Decompose` AND as code guards in `QuestionDecomposer` — change them together.
5. **Granularity is presentation-only**: `GranularityView.Apply` must never re-extract or invent
   content; hybrid is the canonical form.

## Confidentiality (public repo)

- **Never commit**: real questionnaires (TestData docs are gitignored), API keys, or
  internal gateway URLs/endpoints. Machine-local values go in
  `src/RfpExtractor.Cli/appsettings.Local.json` (gitignored, auto-loaded) or env vars.
- Before pushing, `git grep` staged content for internal hostnames if you touched config or docs.

## Build / test / verify

```powershell
dotnet build -c Release     # must be 0 warnings
dotnet test  -c Release     # must be 125+ passed, 0 failed (corpus tests need local docs to be active)
dotnet run --project src/RfpExtractor.Cli -c Release -- "<doc>.docx" --adapters-only "--out=out"   # offline smoke
```

A full LLM run needs a provider key (see README Configuration). The serve UI
(`rfpx serve --provider=openai` etc.) is the fastest way to eyeball extraction quality; use the
expandable question rows to inspect parts/retrieval tags.

When you change extraction/decomposition behaviour, judge it on TWO yardsticks: the printed count
(should be stable run-to-run) and the atomic count (should only move when splitting rules move).
Unexplained `answer_slots` drift usually means a decompose batch failed — check report warnings.
