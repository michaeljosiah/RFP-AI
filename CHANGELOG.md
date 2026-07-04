# Changelog

Newest first. Dates are when the work landed; entries before 2026-07-04 predate version control
(the project moved to git + GitHub `RFP-AI` on 2026-07-04) and are reconstructed from the build log.

## 2026-07-04 — EQDP run findings: table cells no longer decomposed

- **Table cells are atomic — never split, never sent to the LLM.** The EQDP run (a table-heavy
  questionnaire: 304 of 379 questions are cells) exposed the model splitting a *single* grid cell
  by the periods its headers mention — one "Portfolio Return" performance cell fanned into 7
  period-parts — inflating `answer_slots` by 34 spurious atomic slots (508 → 474). Cells now keep
  only the deterministic baseline retrieval tag and are excluded from the decomposition LLM calls
  entirely, cutting decompose cost several-fold on table-heavy documents. Body questions and
  document requests are unaffected. (+2 regression tests.)

## 2026-07-04 — public repo, orchestration refactor, resilient decomposition

- **Moved to git + public GitHub repo (`RFP-AI`).** Confidential material is excluded by
  construction: real questionnaires (`tests/**/TestData/*.docx`) and machine-local endpoints/keys
  (`appsettings.Local.json`) are gitignored; the committed `appsettings.json` carries placeholders
  only; corpus tests no-op when documents are absent so a fresh clone tests green.
- **`ExtractionService` now owns the whole flow** (extract → decompose → metrics) and
  `Wiring.CreateService` is the single composition point — the batch CLI and the serve UI are thin
  callers, and the class is the porting surface for embedding Core in another solution.
- **Decomposition hardened** (rename: `AgentRetrievalEnricher` → `QuestionDecomposer`;
  `--no-enrich` → `--no-decompose` with the old flag kept as a deprecated alias):
  - batches run **concurrently** under `--max-parallel` (~4× faster on multi-batch documents);
  - **retry ×3** with the same policy as the extraction legs — a transient gateway failure can no
    longer silently leave a batch un-split and deflate `answer_slots` between runs;
  - failures now surface as `reconciliation_report.json` warnings and per-batch progress events
    (new "Decompose — batches" bar in the serve UI).
- **`ModelProfile.For(model)`** replaces the loose `(temperature, nativeSchema, maxOutputTokens)`
  triple; **`StructuredAgent`** is the one shared build/stream/fallback/parse path for all three
  agents.
- CLI flag parsing is strict: an invalid `--granularity`/`--strategy`/numeric flag errors instead
  of silently defaulting (a `--granularity` typo previously produced *atomic* output).
- Dedupes: `ResultFinalizer` (warnings + invariant check + metrics), `OpenAIV1.Normalize` (was
  duplicated in Azure and GenCore wiring); removed the `Wiring.TemperatureFor` pass-through.
- Docs: README rewritten with a `questions.json` / `reconciliation_report.json` **field reference**
  (the downstream contract); AGENTS.md grounded; this changelog created.

## 2026-07 (early) — printed-level extraction + deterministic decomposition, granularity views

- Inverted the architecture after run-to-run atomic counts proved unstable (137 vs 75 on the same
  document): extraction now captures **one question per printed prompt** (stable ~N), and a
  separate **decomposition pass** splits compound prompts into atomic parts and tags each part for
  retrieval (`category`, `expected_format`, `units`, `requires_external_input`, `ai_comment`).
- Three output granularities from one canonical (hybrid) result: `hybrid` (printed + nested
  `parts[]`), `bundled` (printed + `sub_questions` strings), `atomic` (flat, one entry per ask).
- Guardrails from field findings: document requests are never split by period/fund ("attribution
  for 1, 3 and 5 years" is ONE upload); no splitting by reporting period; split "by client type
  and region" stays split. Fixed an over-split (170 → 135 atomic slots on the reference doc).
- Report gained `printed_questions` alongside `answer_slots` (atomic) so both framings are always
  visible; `audience` tagging separates applicant vs internal-only sections deterministically.
- UI: granularity picker, expandable question rows (verbatim, attributes, retrieval, parts),
  printed·atomic summary metric, "New file" restart button.

## 2026-06 (late) — providers, model capabilities, reconciliation v2, resilience, UI

- **Providers as `IChatClient`:** GenCore gateway (OpenAI SDK + `api-key` header handler), Azure
  OpenAI **v1 API** routed through the **Responses API** (chat-completions returns
  `api_not_supported` for gpt-5/o-series and Foundry-hosted models), api.openai.com, and Anthropic
  Claude via the official SDK.
- **Model capability handling** centralised in `ModelCapabilities`: GPT-5/o-series/Claude reject
  explicit temperature (omit); Claude's beta client mishandles native JSON-schema response format
  (→ schema-in-prompt); Claude's adaptive thinking shares the output budget (→ request the real
  128k/64k ceilings); thinking models get smaller text chunks (8k) so responses don't truncate.
- **Reconciliation v2** after real-document runs: one-to-one ordered matching (repeats are distinct
  slots), verbatim-first keys (cells by row+column → printed text → normalised text), optional LLM
  fuzzy pass for paraphrases, post-merge renumbering + schema grafting to keep the 1:1
  `answer_target` invariant; Y/N checkbox-bleed and table-cell verbatim cleanup; truncated-item
  stitching across pages.
- **Both engines** use the recursive Open XML walker for docx text (Telerik's markdown export
  flattens grids nested in layout tables — the text leg saw zero table cells on a reference doc).
- **GenCore timeout resilience:** every response streamed (defeats idle timeouts); no unbounded
  request (page-per-call, chunked text at block boundaries, sheet-per-call with cell cap); retry
  ×3 per call; failed units become warnings, not aborted runs; legs run concurrently under
  `--max-parallel`; compact JSON payloads.
- **`rfpx serve` monitoring UI:** launcher environment checks (engine, provider config, live LLM
  ping), SSE progress bars per leg, streaming question feed, result downloads + save-to-disk.

## 2026-06 — initial build

- Solution scaffold: Core / Telerik / LibreOffice / Cli + tests; dual-leg pipeline (vision +
  structured text, grid-first for Excel) with reconciliation and confidence scoring; invariant
  validator (every `answer_target` exactly once in schema and questions); Microsoft Agent
  Framework for all LLM calls with JSON-schema structured output.
