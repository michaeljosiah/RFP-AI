# RfpExtractor (GitHub: RFP-AI)

Extracts every question from a **Word, PDF, or Excel** questionnaire into two linked JSON files —
a structure schema and an answerable question list with back-references — via a dual pipeline
(LLM vision + structured text; grid-first for Excel) with reconciliation, then a deterministic
**decomposition pass** that splits compound printed questions into atomic, retrieval-tagged parts.
LLM calls go through **Microsoft Agent Framework**; documents are rendered/parsed by a selectable
engine; the LLM is reached through a selectable provider.

Design docs: [`docs/spec-telerik.html`](docs/spec-telerik.html), [`docs/spec-libreoffice.html`](docs/spec-libreoffice.html).
**How Excel colour-coded DDQ extraction works:** [`docs/excel-extraction.md`](docs/excel-extraction.md).
History: [`CHANGELOG.md`](CHANGELOG.md). Agent grounding: [`AGENTS.md`](AGENTS.md).

> **Public repo hygiene:** real questionnaires, gateway URLs and keys are never committed.
> Machine-local values live in `src/RfpExtractor.Cli/appsettings.Local.json` (gitignored) or env
> vars; corpus documents live locally in `tests/RfpExtractor.Tests/TestData/` (gitignored).

---

## AGENT QUICKSTART (read this first)

Rules for running this app non-interactively:

- **Always run from the repository root** — the folder that contains `RfpExtractor.slnx`. Every command
  below assumes that working directory and uses paths relative to it. (`cd` into wherever you cloned/opened
  the repo first.)
- **Argument syntax is strict.** Value args MUST be `--key=value` (no spaces around `=`, quote the
  whole token if the value has spaces): `"--out=out\my results"`. Bare flags have no value: `--adapters-only`.
  An invalid value is an **error with a message**, never a silent default.
- **Paths in examples are placeholders.** `<PATH-TO>.docx` = your input document; `out` = a relative output
  folder created under the current directory. Substitute real paths for your machine.
- **The input file path is the one positional argument** (anything not starting with `--`).
- **To verify the app works WITHOUT any credentials or network, use `--adapters-only`** (see Step 3).
  A full run additionally needs an LLM key (Step 4).
- **Exit code 0 = success.** Non-zero = failure; the reason is printed to stderr.

### Step 1 — Verify prerequisites
```powershell
dotnet --version            # expect 10.x
docker --version            # ONLY needed for --engine=libreoffice
```
> **Telerik source (per-machine):** `nuget.config` points at the local DevCraft package feed
> `C:\Program Files (x86)\Progress\Telerik UI for ASP.NET Core <version>\dpl`. If DevCraft is a
> different version or install location on this machine, update that `telerik-local` path (or point it
> at your Telerik NuGet feed). Not needed if you only use `--engine=libreoffice`.

### Step 2 — Build and test (no credentials needed)
```powershell
# run from the repository root (the folder containing RfpExtractor.slnx)
dotnet build -c Release      # SUCCESS = "Build succeeded. 0 Warning(s) 0 Error(s)"
dotnet test  -c Release      # SUCCESS = "Passed! - Failed: 0, Passed: 125"
```
(The corpus tests over real questionnaires no-op unless the documents are present locally —
see `tests/RfpExtractor.Tests/TestData/README.md`.)

### Step 3 — Smoke test the engine (NO credentials, NO network) ✅ do this to confirm it runs
```powershell
# run from the repository root (the folder containing RfpExtractor.slnx)
dotnet run --project src/RfpExtractor.Cli -c Release -- "<PATH-TO>.docx" --engine=telerik --adapters-only "--out=out"
```
SUCCESS looks like:
```
[text] markdown = 8198 chars, tables (ground truth) = 1
[render] 3 page(s); wrote out\page-001.png (~540 KB)
```
For Excel input, you get `[grid] sheet '...': N cells (...)` instead of `[text]`.
(`--engine=libreoffice` here additionally needs Gotenberg running — see Step 5.)

### Step 4 — Full extraction (needs an LLM key)
Default provider is **GenCore** (enterprise gateway). Set the key + gateway URL, then run:
```powershell
# run from the repository root (the folder containing RfpExtractor.slnx)
$env:EnterpriseGenCoreApiKey = "<your-gencore-key>"
# gateway BaseUri: appsettings.Local.json or env var AzureOpenAIProxySettings__GenerativeCore__BaseUri
dotnet run --project src/RfpExtractor.Cli -c Release -- "<PATH-TO>.docx" --model=gpt-4o "--out=out"
```
SUCCESS = a summary like `Merged 74 questions (70 agreed, 4 to review) -> out` with a
`74 printed questions · 135 atomic slots` line, and four JSON files written (see Outputs).
NOTE: the GenCore endpoint is internal to the corporate network; a live run only works from a
connected machine. For a quick public test use `--provider=openai` or `--provider=claude`.

### Step 5 — (optional) OSS engine
```powershell
docker run -d -p 3000:3000 gotenberg/gotenberg:8      # start once; leave running
# run from the repository root (the folder containing RfpExtractor.slnx)
dotnet run --project src/RfpExtractor.Cli -c Release -- "<PATH-TO>.docx" --engine=libreoffice --model=gpt-4o "--out=out"
```

---

## Command reference

```
rfpx <file.docx|pdf|xlsx> [flags]
```

| Flag | Values | Default | Notes |
|---|---|---|---|
| `<file>` (positional) | path to `.docx` / `.pdf` / `.xlsx` | — | required; routed by extension |
| `--engine=` | `telerik` \| `libreoffice` | `telerik` | document renderer/parser |
| `--provider=` | `gencore` \| `azure` \| `openai` \| `claude` | `gencore` | LLM `IChatClient` |
| `--model=` | e.g. `gpt-4o`, `gpt-4.1`, `claude-sonnet-5` | provider default | vision + structured-output model |
| `--strategy=` | `both` \| `vision` \| `text` | `both` | `both` = dual pipeline + reconcile |
| `--granularity=` | `hybrid` \| `bundled` \| `atomic` | `hybrid` | shape of `questions.json` (see below) |
| `--dpi=` | integer | `200` | render resolution for the vision leg |
| `--max-parallel=` | integer | `4` | concurrent LLM calls (extraction legs AND decompose batches) |
| `--no-fuzzy` | (bare flag) | off | skip the LLM fuzzy-match pass in reconciliation (deterministic matching only) |
| `--no-decompose` | (bare flag) | off | skip the decomposition pass — printed-level questions, no `parts`, no retrieval tags (`--no-enrich` is a deprecated alias) |
| `--chunk-chars=` | integer | `24000` | text-leg chunk size; documents below this run as one text call — lower it to force parallel chunks |
| `--out=` | directory | `<input-dir>\extracted` | output folder (created if missing) |
| `--user-email=` | email | OS user | sent to GenCore as `user_email` |
| `--adapters-only` | (bare flag) | off | run engine only; NO LLM/credentials/network |

**Engine × provider are independent** — any engine works with any provider. Default is
`--engine=telerik --provider=gencore`.

### Output granularity (`--granularity`)

Extraction runs at the **printed-question level** (one entry per printed prompt — reliable ~N), then a
deterministic **decomposition pass** splits each compound prompt into atomic `parts` and tags each part
for retrieval. That reconciled list *is* the hybrid canonical form; `--granularity` chooses how
`questions.json` is *presented* from it (no re-extraction). Splitting is a focused per-question task
(batches run in parallel with the same retry ×3 policy as extraction, and a failed batch is REPORTED as
a warning rather than silently skipped), so the atomic breakdown is **consistent run-to-run**. Table
cells, document uploads and genuinely single questions have no parts and are identical in every mode.

| Mode | `questions.json` shape | Top-level count* | Use when |
|---|---|---|---|
| **`hybrid`** (default) | one entry per **printed question** (`verbatim` + bundled text) with the atomic breakdown nested in `parts[]`, each part keeping its own `retrieval` hint | ~printed | you want both — the original printed question *and* retrieval-ready atomic units |
| **`bundled`** | one entry per **printed question**; atomic asks listed as `sub_questions` strings (no per-part retrieval) | ~printed | matching the printed form / a validation baseline; you'll split for retrieval yourself downstream |
| **`atomic`** | flat — one entry per **distinct ask**, each its own `answer_target` + `retrieval` | ~atomic | feeding an answer-retrieval pipeline directly |

\* The reconciliation report always shows **both** `printed_questions` and `answer_slots` (atomic total,
counted after decomposition), so the two framings are visible regardless of mode. `document_schema.json`
is the printed-level schema.

### Monitoring UI — `rfpx serve`

Hosts a local real-time ingestion monitor (self-contained, no CDNs, binds to localhost only):

```powershell
dotnet run --project src/RfpExtractor.Cli -c Release -- serve                       # opens your browser
dotnet run --project src/RfpExtractor.Cli -c Release -- serve --port=5177 --no-browser
```

- **Animated launcher** runs environment checks before entry: runtime, document engine
  (Telerik licence present / Gotenberg health), **LLM configuration**, and a **live LLM
  connectivity ping** through the configured provider (a real completion round-trip with latency).
- Drop a `.docx` / `.pdf` / `.xlsx`, pick **engine / provider / model / strategy / granularity**,
  then watch the pipeline live over Server-Sent Events: per-leg progress bars (vision pages, text
  chunks, grid sheets, **decompose batches**), a streaming activity log (retries/failures
  highlighted), and **questions appearing in real time** as each page/chunk/sheet completes.
- Extracted questions are **expandable** — click a row to see the verbatim source, attributes,
  retrieval tags and nested atomic parts.
- On completion: summary stats (total / agreed / need-review / printed·atomic / seconds), one-click
  downloads of the four result JSONs, **and "Save to disk"** — writes all four files server-side to
  an editable folder (default `Documents\rfpx\<file>-<jobid>\`).
- `serve` accepts `--engine=` / `--provider=` / `--model=` as UI defaults — e.g. quick local test
  without GenCore:

```powershell
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/RfpExtractor.Cli -c Release -- serve --provider=openai
```

---

## Configuration reference

| Setting | Where | Required when |
|---|---|---|
| `EnterpriseGenCoreApiKey` | env var **or** `appsettings.Local.json` | `--provider=gencore` |
| GenCore BaseUri | `appsettings.Local.json` → `AzureOpenAIProxySettings:GenerativeCore:BaseUri` (or the equivalent env var) | `--provider=gencore` — internal gateway URL, deliberately not committed |
| `GenCore:ApplicationName` / `:ClassifierVersion` | `appsettings.json` | defaults `smartdocs` / `v1.1` |
| `AZURE_OPENAI_ENDPOINT` | env var (or `AzureOpenAIEndpoint` in `appsettings.Local.json`) | `--provider=azure` — Azure OpenAI **v1 API**, e.g. `https://<resource>.openai.azure.com/openai/v1` |
| `AZURE_OPENAI_API_KEY` | env var (or `AzureOpenAIApiKey` in `appsettings.Local.json`) | `--provider=azure` — optional; omit to use Entra ID (`az login` + the *Cognitive Services OpenAI User* role) instead |
| `OPENAI_API_KEY` | env var (or `OpenAIApiKey` in `appsettings.Local.json`) | `--provider=openai` — quick local testing against api.openai.com |
| `ANTHROPIC_API_KEY` | env var (or `AnthropicApiKey` in `appsettings.Local.json`) | `--provider=claude` — direct api.anthropic.com (default model `claude-sonnet-5`) |
| Telerik licence | `%AppData%\Telerik\telerik-license.txt` | `--engine=telerik` (auto-discovered) |
| Gotenberg URL | env var `GOTENBERG_URL` (default `http://localhost:3000`) | `--engine=libreoffice` |

`appsettings.json` (committed) holds only non-sensitive defaults; `appsettings.Local.json`
(gitignored, auto-loaded when present) holds machine/network-specific values.

---

## Outputs (written to `--out`)

| File | Contents |
|---|---|
| `document_schema.json` | structure/map: sections, items, tables, `answer_target`s (printed level) |
| `questions.json` | the answerable question list at the chosen `--granularity` (nested `parts[]` in hybrid); each entry links back via `answer_target` / `schema_ref` |
| `review_queue.json` | questions found by only one leg (`needs_review = true`) |
| `reconciliation_report.json` | counts + warnings — see the field reference below |

`--adapters-only` instead writes `page-001.png` and prints diagnostics (no JSON, no LLM).

### `questions.json` field reference (the downstream contract)

Top-level entry (one per printed question in `hybrid`/`bundled`, one per atomic ask in `atomic`):

| Field | Meaning |
|---|---|
| `question_id` / `answer_target` | stable ids; `answer_target` is the slot a fill-back tool writes into (1:1 with the schema) |
| `question_text` | normalised, self-contained question |
| `verbatim_source` | the printed prompt exactly as it appears in the document |
| `sub_questions` | bundled mode only: the atomic asks as plain strings |
| `parts[]` | hybrid mode, compound questions only: the atomic breakdown (below) |
| `answer_type` | `text` \| `long_text` \| `number` \| `currency` \| `percentage` \| `date` \| `yes_no` \| `document_upload` |
| `section_path`, `schema_ref`, `binding` | where it lives in the document (binding = write-back cell/control when known) |
| `source` | `body` \| `table_cell` \| `document_request` |
| `found_by`, `confidence`, `needs_review` | reconciliation provenance |
| `audience` | `applicant` (responder answers) \| `internal` (receiving-firm-only section) |
| `retrieval` | retrieval tags (below) — on the question itself when it is a single ask, on each part when compound |

`parts[]` entries: `part_id` (`Q008.2`), `answer_target` (`AT-0008-2`), `question_text`,
`answer_type`, `retrieval`.

`retrieval` tags (added by the decomposition pass; absent with `--no-decompose`). Body questions and
document requests get LLM-derived tags; **data-entry table cells are atomic and skip the LLM**, so
they carry only the deterministic baseline (`expected_format` from the cell type, `category: other`,
`requires_external_input: true`) — no per-cell `units`/`ai_comment`:

| Field | Meaning |
|---|---|
| `category` | one of `firm_profile`, `team`, `investment_process`, `performance`, `risk`, `esg`, `operations`, `compliance`, `fees`, `client_service`, `other` |
| `expected_format` | shape of answer to look for: `narrative` \| `short_text` \| `value` \| `boolean` \| `date` \| `document` \| `table` |
| `units` | expected unit for numeric answers (`"S$ million"`, `"%"`, `"years"`), else absent |
| `requires_external_input` | `true` when answering needs the firm's own records/data/SME judgement (not answerable from the questionnaire alone) |
| `ai_comment` | short actionable note for the answering team (e.g. "Requires audited AUM figures from Finance"), else absent |

### `reconciliation_report.json` key counts

| Field | Meaning |
|---|---|
| `printed_questions` | printed prompts (the human "how many questions" count; = top-level entries in hybrid/bundled) |
| `answer_slots` | **atomic** total after decomposition (= entries in atomic mode) |
| `applicant_slots` / `internal_slots` | printed-level split by audience (internal = receiving-firm-only sections) |
| `body_questions` / `table_cells` / `document_requests` / `data_entry_tables` | by-source breakdown (cells counted individually; grids also counted as tables) |
| `unique_question_texts` | distinct wording after deduping cross-section repeats |
| `primary/secondary/merged/agreed/…` | leg reconciliation stats; `warnings` includes retried-and-failed pages/chunks/batches |

---

## Using the Core library from another solution

`RfpExtractor.Core` depends only on `Microsoft.Agents.AI` + `Microsoft.Extensions.AI` — no engine,
host or provider SDKs — and `ExtractionService` owns the whole flow. To embed it:

```csharp
IChatClient chat = /* any provider: OpenAI SDK, Azure v1, Anthropic, your gateway */;
var profile = ModelProfile.For(modelName);          // temperature / schema-mode / token budget quirks

var llm        = new AgentLlmExtractor(chat, profile);
var reconciler = new Reconciler(new AgentFuzzyMatcher(chat, profile));
var router     = new PipelineRouter(
    new DocumentPipeline(renderer, textExtractor, llm, reconciler),     // your IDocumentRenderer / IStructuredTextExtractor
    new SpreadsheetPipeline(sheetExtractor, renderer, llm, reconciler));
var service    = new ExtractionService(router, new QuestionDecomposer(chat, profile));

var result    = await service.RunAsync(path, new ExtractionOptions(MaxParallel: 4));
var questions = GranularityView.Apply(result.Merged, Granularity.Atomic).Questions;
```

Engine adapters (`RfpExtractor.Telerik` / `RfpExtractor.LibreOffice`) and provider wiring
(`Wiring.CreateChatClient` incl. the GenCore header handler and Azure v1 Responses-API routing) are
reference implementations to lift as needed.

---

## Troubleshooting (error → fix)

| Message / symptom | Cause & fix |
|---|---|
| `GenCore API key missing…` / `GenCore BaseUri missing…` | Set `EnterpriseGenCoreApiKey` and the gateway BaseUri (Step 4 / appsettings.Local.json). |
| `Set AZURE_OPENAI_ENDPOINT` | You passed `--provider=azure`; set the env var + `az login`, or use another provider. |
| `Gotenberg returned … Is the container running?` | Start Gotenberg (Step 5) or use `--engine=telerik`. |
| Output/args ignored, wrong out dir | Arg used a space instead of `=`. Use `--out=C:\path` (quote if it has spaces). |
| Red "trial" banner / watermark in output | Telerik trial key. Install the purchased key at `%AppData%\Telerik\telerik-license.txt`. |
| `Unknown --provider` / `--engine` / `--granularity` / `--strategy` | Typo; valid values are in the Command reference (invalid values error, never silently default). |
| `decompose batch N/M failed after 3 attempts` warning | That batch kept printed-level questions (no parts); `answer_slots` may undercount — re-run if atomic completeness matters. |
| Exit code non-zero | Read stderr; it names the exact problem. |

---

## Repo layout

```
src/RfpExtractor.Core         engine/provider-agnostic: models, prompts, pipelines, reconciler,
                              QuestionDecomposer, ExtractionService (THE entry point + porting surface)
src/RfpExtractor.Telerik      Telerik DPL adapters (renderer / spreadsheet)
src/RfpExtractor.LibreOffice  OSS adapters (Gotenberg renderer / Open XML SDK text / ClosedXML spreadsheet)
src/RfpExtractor.Cli          rfpx console app + serve UI: --engine + --provider config, IChatClient wiring
tests/RfpExtractor.Tests      xUnit (125): invariants, reconciliation, stitching, chunking, resilience,
                              decomposition, granularity views, model capabilities (+ optional local corpus)
```

| Engine | Render | Text | Excel | Licensing |
|---|---|---|---|---|
| `telerik` | Telerik DPL → Skia | Open XML SDK* | RadSpreadProcessing | DevCraft (commercial) |
| `libreoffice` | Gotenberg → Docnet.Core → SkiaSharp | Open XML SDK | ClosedXML | MIT/MPL/BSD (free) |

\* both engines use the recursive Open XML walker for docx text — it preserves data-entry grids
nested inside layout tables, which Telerik's markdown export flattens.

| Provider | Connects via |
|---|---|
| `gencore` | OpenAI SDK → OpenAI-v1-compatible gateway `{BaseUri}/openai/v1`, `api-key` header + `model_engine`/`user_email`/`application_name`/`classifier_version` |
| `azure` | OpenAI SDK → Azure OpenAI **v1 API** `{resource}/openai/v1` via the **Responses API** (chat-completions returns `api_not_supported` for reasoning/Foundry models) — API key or Entra ID (`BearerTokenPolicy` + `DefaultAzureCredential`). `--model=` is the **deployment name**. |
| `openai` | OpenAI SDK → api.openai.com |
| `claude` | Anthropic SDK (`AnthropicClient.AsIChatClient`) → api.anthropic.com — schema-in-prompt structured output, 128k output budget (see `ModelCapabilities`) |

---

## Status

Builds clean on .NET 10 (**0 warnings**); **125 tests pass**. Extraction is printed-level with a
deterministic, retried, parallel decomposition pass; reconciliation is one-to-one/verbatim-first with
an optional LLM fuzzy pass; every LLM response is streamed, chunked and retried ×3 for gateway
resilience. Four providers, two engines, three output granularities, real-time monitoring UI.
See [`CHANGELOG.md`](CHANGELOG.md) for the full build history and field findings.
