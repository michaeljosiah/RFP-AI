# Changelog

Newest first. Dates are when the work landed; entries before 2026-07-04 predate version control
(the project moved to git + GitHub `RFP-AI` on 2026-07-04) and are reconstructed from the build log.

## 2026-07-07 — deterministic colour-cell enumeration (Excel DDQ extraction works)

- **Colour-coded spreadsheets now extract completely and deterministically.** An LLM does the small,
  reliable task — classify which fill colours mark answers (`DetectAnswerColoursAsync` + the
  `GridColours` prompt, fed a per-sheet histogram + samples + any workbook legend) — and then
  `ColourGridBuilder` enumerates in code: **one question per answer-coloured cell**, phrased from the
  row's question text + column header, schema synthesized 1:1. This closes the gap the pure-LLM path
  couldn't: LLMs won't exhaustively list hundreds of near-identical cells.
- **Result on the Allianz DDQ: 382/382 answer cells** (254 green manual + 128 yellow dropdown across
  columns I/J/K), READ ME correctly skipped (legend swatches filtered by a per-colour count floor),
  0 warnings, real DDQ question text per cell, differentiated by a short column-header suffix. Up
  from 1 → 12 → 24 across the earlier iterations.
- Plain (uncoloured) grids keep the LLM row-band-chunk enumeration; per-sheet the pipeline picks the
  path automatically. Grid progress is per-sheet. +3 tests (139 total).

## 2026-07-07 — colour-aware grid extraction (foundation) + questions-only grid output

- **Cell fill colour is now captured** (`GridCell.Fill`, normalized RRGGBB) by both spreadsheet
  extractors, surfaced in the grid payload (fill on every cell + a sheet-wide `fill_summary`
  histogram), and the grid prompt keys on it: answer cells = cells with an input/answer colour
  (empty OR pre-filled formula), excluding auto-generated/header/assessor colours; falls back to
  emptiness on plain grids. This is the primary answer signal on professional DDQ templates.
- **Grid mode now returns questions ONLY; the document_schema is rebuilt deterministically**
  (`GridSchema.Rebuild`) so the model spends its whole output budget on questions and a truncated
  response can never orphan a schema target. Eliminated the 100+ orphan-target warnings; chunk
  default lowered to 600. (+1 test; 137 total.)
- **Result on the Allianz DDQ: correct but incomplete.** Colour-awareness makes the model find the
  *right* cells with good phrasing (column I "Response"/"Status" answers, real question text, 0
  warnings) — but it found 24 of the ~382 answer cells (3 answer columns/row: I yellow dropdown +
  J/K green manual) and missed the green columns entirely. Root cause is now identification-complete
  but ENUMERATION-limited: gpt-4o samples a few near-identical answer cells rather than emitting one
  per coloured cell. Reliable completion needs deterministic enumeration (LLM picks the answer
  colours; code emits one question per coloured cell) or a stronger model — scoped, pending decision.

## 2026-07-07 — grid row-band chunking (fixes truncation; colour-coded DDQs still need more)

- **Large sheets are now split into row-band chunks** (`ExtractionOptions.GridChunkCells`, default
  1000), the Excel analogue of the text chunker: each chunk carries the sheet's header rows for
  column context, and answer candidates (empty_cells) come only from a chunk's own band so no cell
  is covered twice. This fixes the single-shot truncation that collapsed a 3948-cell sheet to a
  degenerate 1-question result. Grid progress + the serve UI bar are now per-chunk. (+1 test
  proving every answer cell is covered exactly once; 136 total.)
- **Empirical result on the colour-coded DDQ: chunking alone is NOT enough.** The run went 1 → 12
  questions, but they are largely the wrong cells (the orange assessor scoring columns, generic
  "TP related question" placeholders) with 30 orphan-target warnings. Root cause confirmed: the 255
  empty *answer* cells are indistinguishable from ~2,500 empty *spacer* cells without the fill-colour
  signal, so the model guesses. Chunking fixes truncation (identification is a separate problem);
  this template needs colour-aware answer detection (scoped, not built).

## 2026-07-07 — Excel render fix; grid-extraction limits documented

- **Render no longer crashes on documents with embedded images.** A branded Excel DDQ hit
  `ImagePropertiesResolver and JpegImageConverter cannot be both null` during xlsx→PDF export
  (Telerik needs a cross-platform image decoder). Added the `Telerik.Documents.ImageUtils` package
  and wire its resolver once in `TelerikRenderer`'s static ctor. This affects any image-bearing
  docx/xlsx/pdf in a vision/both run, not just Excel.
- **A spreadsheet render failure now degrades gracefully.** The grid leg is authoritative for
  Excel, so a vision-cross-check render failure warns ("Vision cross-check skipped…") and falls
  back to grid-only instead of sinking the run. (+1 test, 135 total.)
- **Known limitation (not yet fixed): grid extraction under-performs on large, colour-coded DDQ
  templates.** On a 188×21 DDQ (~382 answer cells marked by fill colour — green=manual,
  yellow=dropdown — of which 127 are pre-filled formulas), the single-shot grid call returned a
  degenerate 1-question result (output truncation on the oversized payload; orphan schema targets).
  Two root causes: (a) the per-sheet grid payload isn't chunked, so a large sheet overflows the
  model's output budget; (b) the answer signal is cell *emptiness*, but professional templates mark
  answers by *fill colour* and many answer cells are pre-filled. Fix (grid chunking + colour-aware
  answer detection) is scoped but not yet built. Simple/empty-cell grids are unaffected.

## 2026-07-04 — positional grid reconciliation (phase 4)

- **New reconciler phase: positional cell matching.** When the vision leg re-derives a grid but
  words the headers differently, every row+column key fails and the whole grid used to graft as
  duplicates. Grids are now paired across legs when one full AXIS of headers clearly agrees
  (max row/column Jaccard ≥ 0.6, one-to-one, document-order tiebreak) and their cells matched by
  (row index, column index) computed over the FULL grid. Three deliberate safety rails: axis
  agreement required, best-scoring partner only (no fallback guessing), and **identical grid
  dimensions required** — a grid the legs disagree about (extra total row, split table, the EQDP's
  6×4-vs-1×4 case) stays grafted for review, because silently absorbing a genuine vision-only cell
  is worse than keeping a duplicate. Replayed against the EQDP dual-leg run: the clean reworded
  grids absorb; all ambiguous ones are refused. Cross-leg duplication remains a `--strategy=both`
  hazard on table-heavy born-digital docs — prefer `--strategy=text` there (the low-match warning
  still fires). +5 guard-rail tests (134 total); all 129 pre-existing tests unchanged.

## 2026-07-04 — reconciliation-quality warning for low cross-leg match

- **Low reconciliation match rate now warns.** A `--strategy=both` run on the EQDP produced 575
  merged questions vs the trustworthy 379 from text-only: the vision leg re-derived the same grids
  with differently-labelled cells, so only 181 of ~378 reconciled and the rest (197+197) were
  double-counted (table_cells 304→476, data_entry_tables 13→22). The paraphrase fuzzy pass covers
  body questions, not grid cells, and was capped out anyway. The reconciler now emits a loud
  warning when the two legs agree on <60% of the smaller leg, pointing born-digital documents at
  `--strategy=text`. Diagnostic only — no counts change. (+2 tests.)

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
