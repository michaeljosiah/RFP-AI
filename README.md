# RfpExtractor

Extracts every question from a **Word, PDF, or Excel** questionnaire into two linked JSON files —
a structure schema and a flat, answerable question list with back-references — via a dual pipeline
(LLM vision + structured text; grid-first for Excel) with reconciliation and confidence scoring.
LLM calls go through **Microsoft Agent Framework**; documents are rendered/parsed by a selectable
engine; the LLM is reached through a selectable provider.

Design docs: [`docs/spec-telerik.html`](docs/spec-telerik.html), [`docs/spec-libreoffice.html`](docs/spec-libreoffice.html).

---

## AGENT QUICKSTART (read this first)

Rules for running this app non-interactively:

- **Always run from the repository root** — the folder that contains `RfpExtractor.sln`. Every command
  below assumes that working directory and uses paths relative to it. (`cd` into wherever you cloned/opened
  the repo first.)
- **Argument syntax is strict.** Value args MUST be `--key=value` (no spaces around `=`, quote the
  whole token if the value has spaces): `"--out=out\my results"`. Bare flags have no value: `--adapters-only`.
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
# run from the repository root (the folder containing RfpExtractor.sln)
dotnet build -c Release      # SUCCESS = "Build succeeded. 0 Warning(s) 0 Error(s)"
dotnet test  -c Release      # SUCCESS = "Passed!  - Failed: 0, Passed: 32"
```

### Step 3 — Smoke test the engine (NO credentials, NO network) ✅ do this to confirm it runs
```powershell
# run from the repository root (the folder containing RfpExtractor.sln)
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
Default provider is **GenCore** (M&G gateway). Set the key, then run:
```powershell
# run from the repository root (the folder containing RfpExtractor.sln)
$env:EnterpriseGenCoreApiKey = "<your-gencore-key>"
dotnet run --project src/RfpExtractor.Cli -c Release -- "<PATH-TO>.docx" --model=gpt-4o "--out=out"
```
SUCCESS = a line like `Merged 42 questions (38 agreed, 4 to review) -> out` and four JSON files
written (see Outputs). NOTE: GenCore's endpoint is internal to the M&G network; a live run only works
from a connected machine.

### Step 5 — (optional) OSS engine
```powershell
docker run -d -p 3000:3000 gotenberg/gotenberg:8      # start once; leave running
# run from the repository root (the folder containing RfpExtractor.sln)
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
| `--max-parallel=` | integer | `4` | concurrent LLM calls — tune to your GenCore rate quota |
| `--no-fuzzy` | (bare flag) | off | skip the LLM fuzzy-match pass in reconciliation (deterministic matching only) |
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
`questions.json` is *presented* from it (no re-extraction). Splitting is a focused per-question task, so
the atomic breakdown is **consistent run-to-run** rather than depending on the extraction model choosing
to split. Table cells, document uploads and genuinely single questions have no parts and are identical in
every mode.

| Mode | `questions.json` shape | Top-level count* | Use when |
|---|---|---|---|
| **`hybrid`** (default) | one entry per **printed question** (`verbatim` + bundled text) with the atomic breakdown nested in `parts[]`, each part keeping its own `retrieval` hint | ~printed | you want both — the original printed question *and* retrieval-ready atomic units |
| **`bundled`** | one entry per **printed question**; atomic asks listed as `sub_questions` strings (no per-part retrieval) | ~printed | matching the printed form / a validation baseline; you'll split for retrieval yourself downstream |
| **`atomic`** | flat — one entry per **distinct ask**, each its own `answer_target` + `retrieval` | ~atomic | feeding an answer-retrieval pipeline directly |

\* The reconciliation report always shows **both** `printed_questions` and `answer_slots` (atomic total,
counted after decomposition), so the two framings are visible regardless of mode. `document_schema.json`
is the printed-level schema. `--no-enrich` skips decomposition (printed-level questions, no parts).

### Monitoring UI — `rfpx serve`

Hosts a local real-time ingestion monitor (self-contained, no CDNs, binds to localhost only):

```powershell
dotnet run --project src/RfpExtractor.Cli -c Release -- serve                       # opens your browser
dotnet run --project src/RfpExtractor.Cli -c Release -- serve --port=5177 --no-browser
```

- **Animated launcher** runs environment checks before entry: runtime, document engine
  (Telerik licence present / Gotenberg health), **LLM configuration**, and a **live LLM
  connectivity ping** through the configured provider (a real completion round-trip with latency).
- Drop a `.docx` / `.pdf` / `.xlsx`, pick engine/provider/model/strategy, then watch the pipeline
  live over Server-Sent Events: per-leg progress bars (vision pages, text chunks, grid sheets), a
  streaming activity log (retries/failures highlighted), and **questions appearing in real time**
  as each page/chunk/sheet completes.
- On completion: summary stats (total / agreed / need-review / seconds), one-click downloads of the
  four result JSONs, **and "Save to disk"** — writes all four files server-side to an editable folder
  (default `Documents\rfpx\<file>-<jobid>\`).
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
| `EnterpriseGenCoreApiKey` | env var **or** `src/RfpExtractor.Cli/appsettings.json` | `--provider=gencore` |
| GenCore BaseUri / model list | `appsettings.json` → `AzureOpenAIProxySettings:GenerativeCore:{BaseUri,DefaultModel}` | shipped with defaults |
| `GenCore:ApplicationName` / `:ClassifierVersion` | `appsettings.json` | defaults `smartdocs` / `v1.1` |
| `AZURE_OPENAI_ENDPOINT` | env var (or `AzureOpenAIEndpoint` in appsettings) | `--provider=azure` — Azure OpenAI **v1 API**, e.g. `https://<resource>.openai.azure.com/openai/v1` |
| `AZURE_OPENAI_API_KEY` | env var (or `AzureOpenAIApiKey` in appsettings) | `--provider=azure` — optional; omit to use Entra ID (`az login` + the *Cognitive Services OpenAI User* role) instead |
| `OPENAI_API_KEY` | env var (or `OpenAIApiKey` in appsettings) | `--provider=openai` — quick local testing against api.openai.com |
| `ANTHROPIC_API_KEY` | env var (or `AnthropicApiKey` in appsettings) | `--provider=claude` — direct api.anthropic.com (default model `claude-sonnet-5`) |
| Telerik licence | `%AppData%\Telerik\telerik-license.txt` | `--engine=telerik` (auto-discovered) |
| Gotenberg URL | env var `GOTENBERG_URL` (default `http://localhost:3000`) | `--engine=libreoffice` |

---

## Outputs (written to `--out`)

| File | Contents |
|---|---|
| `document_schema.json` | structure/map: sections, items, tables, `answer_target`s |
| `questions.json` | flat answerable list; each links back via `answer_target` / `schema_ref` |
| `review_queue.json` | questions found by only one leg (`needs_review = true`) |
| `reconciliation_report.json` | counts + warnings. Two framings side by side: `answer_slots` (total slots to fill) vs `unique_question_texts` (deduped across sections), plus a by-source breakdown (`body_questions` / `table_cells` / `data_entry_tables` / `document_requests`) |

`--adapters-only` instead writes `page-001.png` and prints diagnostics (no JSON, no LLM).

---

## Troubleshooting (error → fix)

| Message / symptom | Cause & fix |
|---|---|
| `GenCore API key missing…` | Set `EnterpriseGenCoreApiKey` (Step 4). |
| `Set AZURE_OPENAI_ENDPOINT` | You passed `--provider=azure`; set the env var + `az login`, or use `--provider=gencore`. |
| `Gotenberg returned … Is the container running?` | Start Gotenberg (Step 5) or use `--engine=telerik`. |
| Output/args ignored, wrong out dir | Arg used a space instead of `=`. Use `--out=C:\path` (quote if it has spaces). |
| Red "trial" banner / watermark in output | Telerik trial key. Install the purchased key at `%AppData%\Telerik\telerik-license.txt`. |
| `Unknown --provider` / `Unknown --engine` | Typo; valid values are in the Command reference. |
| Exit code non-zero | Read stderr; it names the exact problem. |

---

## Repo layout

```
src/RfpExtractor.Core         engine-agnostic: models, prompts, pipelines, reconciler, MAF (AgentLlmExtractor)
src/RfpExtractor.Telerik      Telerik DPL adapters (renderer / text / spreadsheet)
src/RfpExtractor.LibreOffice  OSS adapters (Gotenberg renderer / Open XML SDK text / ClosedXML spreadsheet)
src/RfpExtractor.Cli          rfpx console app: --engine + --provider, config + IChatClient wiring
tests/RfpExtractor.Tests      xUnit: invariant, reconciliation, page-stitching
```

| Engine | Render | Text | Excel | Licensing |
|---|---|---|---|---|
| `telerik` | Telerik DPL → Skia | Telerik Markdown | RadSpreadProcessing | DevCraft (commercial) |
| `libreoffice` | Gotenberg → Docnet.Core → SkiaSharp | Open XML SDK | ClosedXML | MIT/MPL/BSD (free) |

| Provider | Connects via |
|---|---|
| `gencore` | OpenAI SDK → GenCore gateway `{BaseUri}/openai/v1`, `api-key` header + `model_engine`/`user_email`/`application_name=smartdocs`/`classifier_version=v1.1` (mirrors `AP.Nexus.Agents`) |
| `azure` | OpenAI SDK (`ChatClient` + `OpenAIClientOptions.Endpoint`) → Azure OpenAI **v1 API** `{resource}/openai/v1` — API key (`ApiKeyCredential`) if `AZURE_OPENAI_API_KEY` is set, else Entra ID (`BearerTokenPolicy` + `DefaultAzureCredential`). `--model=` is the **deployment name**, not the underlying model. |
| `openai` | OpenAI SDK → api.openai.com |
| `claude` | Anthropic SDK (`AnthropicClient.AsIChatClient`) → api.anthropic.com |

---

## Status

Builds clean on .NET 10 (0 warnings); **21 tests pass** — Core unit tests (invariant, reconciliation,
page-stitching, markdown chunking, retry/partial-failure, payload capping) plus a **document corpus**
(`tests/RfpExtractor.Tests/TestData/`) that runs the real Telerik adapters over real questionnaires
(`FUND DUE DILIGENCE.docx`, `1. EQDP RFP Questionnaire.docx`) asserting clean markdown, table detection, and
PNG rendering. Add a document by dropping it in `TestData/` and adding a row to `Corpus` in
`DocumentCorpusTests.cs`. Both engines also verified end-to-end via `--adapters-only`.

**Reconciliation v2 (rebuilt after a real M&G questionnaire run):** one-to-one ordered matching
(repeated questions across sections are distinct answer slots — never collapsed), **verbatim-first**
match keys (cells by row+column, then printed text, then normalized question text), an **LLM
fuzzy-match pass** for paraphrase duplicates (default on; `--no-fuzzy` disables), and post-merge
target renumbering + schema grafting so the 1:1 `answer_target` invariant holds on merged output.
Also: **both engines now use the recursive Open XML SDK text extractor for docx** — Telerik's
markdown export flattens data-entry grids nested inside layout tables (the text leg saw zero table
cells on the M&G doc); the recursive walker renders them as real markdown tables (1 → 3 ground-truth
tables on that document).

**GenCore timeout resilience (built in, for 30–50 page documents):** every LLM response is **streamed**
(defeats gateway idle timeouts); no single request is unbounded — the text leg is **chunked** at block
boundaries (~24k chars/call, tables never split), vision is page-per-call, Excel is sheet-per-call with a
4,000-cell cap; each call **retries ×3** and a permanently failed page/chunk lands in
`reconciliation_report.json` as a warning instead of aborting the run; vision + text legs run
**concurrently** under `--max-parallel`; LLM payloads use compact JSON. See spec-telerik.html §9.1. GenCore provider wired and config-verified. Build-time
deviations from the original spec (now reflected in the specs): `Microsoft.Extensions.AI` is 10.x;
`ChatClientAgentOptions` has no `Instructions` (use `ChatOptions.Instructions`); `Telerik.Licensing`
must be referenced by the CLI project; GenCore is an OpenAI-v1 gateway (OpenAI SDK + `api-key` header),
not `AzureOpenAIClient`.
