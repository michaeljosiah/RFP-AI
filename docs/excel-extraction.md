# How Excel questionnaire extraction works

This explains how RfpExtractor turns an Excel questionnaire (`.xlsx` / `.xlsm`) into the answerable
question list — in particular how it handles **colour-coded DDQ templates**, which are the norm in
enterprise procurement and defeat a naïve "one question per empty cell" approach.

## TL;DR — the core idea

> **Let the LLM do judgement, let code do enumeration.**

Finding *which cells are answers* on a professional template is a judgement call (colour legends,
auto-calculated cells, assessor-only areas) — the LLM is good at that. Emitting *one question for each
of several hundred near-identical answer cells* is bookkeeping — the LLM is bad at that (it samples a
few and stops), but code does it perfectly. So the pipeline splits the work along that line.

## Why Excel is harder than Word/PDF

A Word questionnaire is read linearly — each printed prompt is a question. An Excel questionnaire is a
2-D grid where the *answers* are blank (or drop-down) cells, and the interesting signal is **where the
respondent is meant to type**. Two things make that hard:

1. **Emptiness is a weak signal.** A real form has thousands of empty cells — spacers, layout gaps,
   an assessor scoring area — and only a fraction are real answer cells. On the Allianz DDQ: **2,847
   empty cells, but only ~255 are answers.**
2. **Professional templates encode answers by cell _colour_, not emptiness.** A legend says
   *"Green = fill manually, Yellow = pick from drop-down, Gray = auto-generated, Orange = assessor."*
   Many answer cells are **pre-filled** (a formula-driven default), so they aren't even empty.

Ignoring colour, an LLM handed the raw grid grabbed the wrong columns (the assessor scoring area) and
produced garbage. The fix is to read colour and make it the primary signal.

## The pipeline

```mermaid
flowchart TD
    A[".xlsx / .xlsm"] --> B["Grid extractor<br/>(Telerik / ClosedXML)<br/>every cell: address, text, isEmpty, FILL colour"]
    B --> C["Find workbook legend<br/>(scan for colour-key lines)"]
    C --> D{"For each sheet"}
    D --> E["Classify fill colours (1 LLM call)<br/>'which fills mark answer cells?'"]
    E --> F{"Answer colours found<br/>(≥4 cells each)?"}
    F -- "yes (colour-coded)" --> G["DETERMINISTIC enumeration<br/>one question per coloured cell<br/>phrased from row question + column header"]
    F -- "no (plain grid)" --> H["LLM row-band-chunk enumeration<br/>answer = empty cell under header / beside label"]
    G --> I["Schema synthesized 1:1 from questions"]
    H --> I
    I --> J["Stitch sheets → questions.json + report"]
```

Every sheet independently takes **one of two paths**, chosen by whether it is colour-coded.

### Path A — colour-coded sheet (deterministic)

Used when the colour classifier finds answer colours. This is the reliable, complete path.

1. **Classify colours (LLM, small + reliable).** `ILlmExtractor.DetectAnswerColoursAsync` is given a
   per-sheet profile — a fill histogram (`{fill, count, empty}`), a few sample values and sample row
   questions per colour, plus any legend lines found in the workbook — and returns just the fills that
   mark respondent answers, with an answer type each. It excludes auto-generated colours (cells almost
   all non-empty computed values), header/banner colours, and assessor-only areas. Output is tiny
   (2–4 colours), so it never truncates. See `Prompts.GridColours`.
   - A **count floor** (≥ 4 cells) then drops any colour that's really just a legend swatch.
2. **Enumerate in code (deterministic).** `ColourGridBuilder.Enumerate` walks the sheet and emits
   **one question per cell whose fill is an answer colour** — empty or pre-filled. Each question's text
   is built from:
   - the **row's question** = the longest non-answer text cell in that row; and
   - the **column header** = the nearest non-answer cell above in that column (a short first-line form
     goes into the question to tell sibling answer columns apart; the full header goes into
     `schema_ref.column`).
   - `binding` points back to the exact cell (`{kind:cell, sheet, address}`) for write-back.

Because code does the enumeration, the count is **guaranteed and identical every run**.

### Path B — plain / uncoloured sheet (LLM)

Used when no answer colours are found (a normal grid: AUM table, headcount projection, etc.).

- The sheet is split into **row-band chunks** (`GridChunkCells`, default 600) so no single response can
  overflow the model's output budget, then the LLM enumerates answer cells from each chunk. With no
  fills, the grid prompt's fallback applies: *answer cells are the empty cells under a column header /
  beside a row label*. Each chunk carries the sheet's header rows for context, and answer candidates
  come only from a chunk's own band so no cell is covered twice.

This handles typical grids well. Its limit is the LLM's: a *very large uncoloured* answer grid could be
under-enumerated (chunking mitigates it). Colour-coded sheets avoid this entirely via Path A.

## Supporting mechanics (shared by both paths)

- **Fill capture.** `GridCell.Fill` (normalized `RRGGBB`) is read by both spreadsheet extractors —
  `TelerikSpreadsheetExtractor` (`PatternFill.PatternColor`) and `ClosedXmlSpreadsheetExtractor`
  (`Style.Fill.BackgroundColor`). White / no-fill → null.
- **Questions-only + synthesized schema.** The grid model returns **questions only**; the
  `document_schema` is rebuilt deterministically from them (`GridSchema.Rebuild`) — one `data_entry`
  table per sheet, one cell per question. This halves the model's output (it doesn't serialize a
  redundant schema) and makes the 1:1 `answer_target` invariant hold **by construction**, so a
  truncated response can never orphan a schema target.
- **Render is decoupled.** For `--strategy=text` (recommended for Excel) the grid leg is authoritative
  and nothing is rendered. Under `--strategy=both`, a vision cross-check renders the workbook to images;
  a render failure (e.g. an embedded logo Telerik can't rasterize) **degrades to grid-only with a
  warning** rather than crashing. Image decoding is wired via `Telerik.Documents.ImageUtils`.

## Worked example — the Allianz DDQ

A colour-coded IS due-diligence questionnaire, 188 rows × 21 columns.

| Fill | Cells | Role (from legend + stats) | Extracted? |
|---|---:|---|---|
| `E2EFDA` green | 254 | manual answer (all empty) — columns J, K | ✅ answer |
| `FFFFCC` yellow | 128 | drop-down answer (pre-filled formula) — column I | ✅ answer |
| `EAEAEA` gray | 575 | auto-generated (computed values) | ⛔ excluded |
| `003781` blue | 41 | section headers | ⛔ excluded |

**Result: 382 / 382 answer cells** (254 + 128), the READ ME instructions tab correctly skipped, 0
warnings, real DDQ question text per cell (each row's 3 answer columns differentiated by an
`— Response` / `— Response Details` / `— Additional comment` suffix). The pure-LLM approach reached only
1 → 12 → 24 before deterministic enumeration closed the gap.

## Extending to a new template

- **Different colours / legend wording:** nothing to change — the classifier reads the histogram +
  legend at runtime, so any colour scheme works as long as answer cells are visually distinct.
- **A colour-coded sheet the classifier misreads:** inspect it free with
  `rfpx <file>.xlsx --adapters-only` — the grid diagnostic prints the fill histogram
  (`fills: #E2EFDA×254(empty 254), …`). If a decorative colour is being taken as an answer, raise the
  count floor or tighten `Prompts.GridColours`.
- **Plain grids:** no action — they use Path B automatically.

## Recommended usage

- Use **`--strategy=text`** for Excel (grid-only; the grid is authoritative and vision adds little).
- Start any new file with **`--adapters-only`** to see the sheet shape and fill histogram before
  spending tokens.
