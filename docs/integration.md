# Integrating RfpExtractor into your .NET project

A practical guide for a .NET developer embedding this extraction engine in their own solution —
what to reference, what to wire, what the contracts are, and how to consume the results. Every code
sample below is verified against the current API.

## The shape of the thing

```
your app
  ├── RfpExtractor.Core            ← the engine (THE porting surface)
  │     depends ONLY on Microsoft.Agents.AI + Microsoft.Extensions.AI
  │     no file writes, no console, no config reads — path in, objects out
  ├── an IChatClient               ← any LLM provider (OpenAI / Azure / Anthropic / your gateway)
  └── engine adapters              ← 3 small interfaces; reuse ours or bring your own
        RfpExtractor.Telerik      (commercial: Telerik DPL)
        RfpExtractor.LibreOffice  (fully OSS: Gotenberg + Open XML + ClosedXML)
```

`ExtractionService` owns the whole flow (extract → reconcile → decompose → report); hosts never
re-assemble pipeline steps. It is **stateless and thread-safe** — build it once, call
`RunAsync` per document, including concurrently.

## 1. Reference the code

**Option A — project reference** (recommended while the API is moving):

```xml
<ProjectReference Include="..\RFP-AI\src\RfpExtractor.Core\RfpExtractor.Core.csproj" />
<!-- plus ONE engine, unless you implement your own adapters (§4): -->
<ProjectReference Include="..\RFP-AI\src\RfpExtractor.LibreOffice\RfpExtractor.LibreOffice.csproj" />
```

**Option B — NuGet packages** (for a private feed):

```powershell
dotnet pack src/RfpExtractor.Core -c Release          # → RfpExtractor.Core.1.0.0.nupkg
dotnet pack src/RfpExtractor.LibreOffice -c Release   # all deps restore from nuget.org
dotnet pack src/RfpExtractor.Telerik -c Release       # consumers ALSO need the Telerik feed + licence
```

All three libraries ship XML docs, so the extensive `///` API commentary appears in IntelliSense.

Notes:
- Target **.NET 10** (`net10.0`).
- `RfpExtractor.Telerik` needs the Telerik DevCraft package feed (see `nuget.config`) and a licence
  at `%AppData%\Telerik\telerik-license.txt`; **the licence check runs in your entry assembly**, so
  your host executable must also reference `Telerik.Licensing`.
- `RfpExtractor.LibreOffice` is licence-free but rendering calls a
  [Gotenberg](https://gotenberg.dev) container (`docker run -d -p 3000:3000 gotenberg/gotenberg:8`).
- The CLI project (`RfpExtractor.Cli`) is a **host**, not a library — don't reference it. Lift code
  from it (§5) instead.

## 2. Quick start — smallest working integration

OSS engine + plain OpenAI. Packages needed in your host: `OpenAI` and `Microsoft.Extensions.AI.OpenAI`
(for `.AsIChatClient()`).

```csharp
using Microsoft.Extensions.AI;
using OpenAI;
using RfpExtractor.Core;
using RfpExtractor.Core.Llm;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;
using RfpExtractor.Core.Reconciliation;
using RfpExtractor.LibreOffice;

// 1. An IChatClient — any provider (§5 for Azure / Anthropic / enterprise gateways).
IChatClient chat = new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY")!)
    .GetChatClient("gpt-4o")
    .AsIChatClient();

// 2. Model quirks (temperature support, schema mode, output budget) resolved ONCE from the name.
var profile = ModelProfile.For("gpt-4o");

// 3. Engine adapters — the OSS set here; swap for Telerik or your own (§4).
var http     = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var renderer = new LibreOfficeRenderer(http, "http://localhost:3000");   // Gotenberg
var text     = new OpenXmlTextExtractor();
var sheet    = new ClosedXmlSpreadsheetExtractor();

// 4. Compose — this block is the whole architecture.
var llm        = new AgentLlmExtractor(chat, profile);
var reconciler = new Reconciler(new AgentFuzzyMatcher(chat, profile));
var router     = new PipelineRouter(
    new DocumentPipeline(renderer, text, llm, reconciler),
    new SpreadsheetPipeline(sheet, renderer, llm, reconciler));
var service    = new ExtractionService(router, new QuestionDecomposer(chat, profile));

// 5. Run (docx / pdf / xlsx routed by extension) and consume.
var result = await service.RunAsync(@"C:\docs\questionnaire.xlsx",
    new ExtractionOptions(Strategy.Text, MaxParallel: 4,
        OnProgress: msg => Console.WriteLine(msg)));

foreach (var q in result.Merged.Questions)
    Console.WriteLine($"{q.QuestionId}  [{q.SectionPath}]  {q.QuestionText}  -> {q.Binding?.Address}");
```

Tips that save a first-timer an afternoon:
- **Excel:** use `Strategy.Text` — the cell grid is authoritative; vision adds little.
- **Word/PDF:** use `Strategy.Both` (the default) — the two legs catch each other's misses.
- If you skip the decomposer (`new ExtractionService(router)` and/or `Decompose = false`), you get
  printed-level questions with no atomic `parts` — fine for many use cases, zero extra LLM calls.

## 3. Consuming the result

`RunAsync` returns a `ReconciledResult`:

| Property | What it is |
|---|---|
| `Merged` | the canonical `ExtractionResult` — `DocumentSchema` + `Questions` (hybrid form: printed questions with atomic `parts[]` nested) |
| `ReviewQueue` | questions found by only one leg (`needs_review = true`) — route these to a human |
| `Report` | counts (`PrintedQuestions`, `AnswerSlots`, by-source) + `Warnings` (**read these** — retries that ultimately failed, coverage-guard flags, truncation notices land here, never exceptions) |

**Granularity** is presentation-only. The canonical form is hybrid; re-shape without re-extracting:

```csharp
var atomic = GranularityView.Apply(result.Merged, Granularity.Atomic);   // one entry per ask
```

**Serialization contract:** the documented snake_case JSON shape (`question_id`, `answer_target`,
`schema_ref`…) comes from the bundled serializer options — use them, or your field names will differ
from every doc and downstream consumer:

```csharp
using RfpExtractor.Core.Json;

File.WriteAllText("questions.json",
    System.Text.Json.JsonSerializer.Serialize(result.Merged.Questions, Json.Options));
// Json.Options = snake_case, indented, enums as strings, nulls omitted. Json.Compact = same, unindented.
```

The four files the CLI writes, if you want parity: `document_schema.json` (`Merged.DocumentSchema`),
`questions.json` (the granularity view's `.Questions`), `review_queue.json` (`ReviewQueue`),
`reconciliation_report.json` (`Report`).

**The invariant you can build on:** every `answer_target` appears exactly once in the schema and once
in the question list — a write-back tool can join on it blindly. `binding` (when present) is the
physical write-back location (`{kind: "cell", sheet, address}` for Excel).

## 4. Bringing your own document engine

Implement three small interfaces from `RfpExtractor.Core.Abstractions` (all `Task`-returning,
`CancellationToken`-accepting; a path comes in, plain records come out):

| Interface | One method | Contract |
|---|---|---|
| `IDocumentRenderer` | `RenderToImagesAsync(path, dpi, ct)` | one PNG per page, `PageNumber` **1-based** |
| `IStructuredTextExtractor` | `ExtractAsync(path, ct)` | markdown + table census; **empty markdown = "no text layer"** (scanned PDF) and the pipeline goes vision-only |
| `ISpreadsheetExtractor` | `ExtractAsync(path, ct)` | every used cell of every sheet as a `GridCell` |

The `GridCell` contract matters — the Excel paths key on it:

- `Address` is A1 (`"E5"`); `Row`/`Column` are **0-based**.
- `Text` is the **display/evaluated** value, trimmed (not the formula).
- `Fill` is the answer-cell signal: `null` for no fill / white / the page-background theme slot;
  `"RRGGBB"` for a literal colour; a stable opaque key like `"theme-Accent1"` for theme colours.
  Consistency matters more than the exact format — the colour classifier treats it as an opaque key,
  but **dropping theme fills silently breaks colour-coded DDQ detection** (a real field bug: answer
  cells highlighted with Excel's default palette looked unhighlighted). Copy the normalization from
  `TelerikSpreadsheetExtractor.FillHex` / `ClosedXmlSpreadsheetExtractor.FillHex`.

The reference adapters are deliberately small (60–200 lines each) — read them before writing your own.

## 5. Wiring an IChatClient (providers)

Everything LLM flows through one `Microsoft.Extensions.AI.IChatClient`, so any provider plugs in.
Working reference code for four providers lives in
[`src/RfpExtractor.Cli/Wiring.cs`](../src/RfpExtractor.Cli/Wiring.cs) (`CreateChatClient`) — lift what
you need:

| Provider | Idiom | Gotcha |
|---|---|---|
| OpenAI | `new OpenAIClient(key).GetChatClient(model).AsIChatClient()` | — |
| Azure OpenAI | plain OpenAI SDK pointed at `https://<resource>.openai.azure.com/openai/v1`, then `azure.GetResponsesClient().AsIChatClient(deployment)` | use the **Responses API** (chat-completions returns `api_not_supported` for reasoning/Foundry models); `model` = **deployment name**; supports API key or Entra ID |
| Anthropic | `new AnthropicClient { ApiKey = key }.AsIChatClient(model)` | schema-in-prompt structured output — handled automatically via `ModelProfile` |
| Enterprise gateway (GenCore-style) | OpenAI SDK + a `DelegatingHandler` injecting `api-key` / audit headers at `{BaseUri}/openai/v1` | copy [`GenCoreChatClient.cs`](../src/RfpExtractor.Cli/GenCore/GenCoreChatClient.cs) — the pattern fits any OpenAI-compatible proxy |

Two rules that keep providers interchangeable:

1. **Always pass `ModelProfile.For(modelName)`** to `AgentLlmExtractor`, `AgentFuzzyMatcher` and
   `QuestionDecomposer`. It resolves per-model quirks (temperature rejection, native JSON-schema vs
   schema-in-prompt, output-token ceilings) in one place. Never special-case a model name elsewhere.
2. **Don't add resilience around `RunAsync`** — retry ×3 with backoff, response streaming (defeats
   gateway idle timeouts), and warning-not-crash degradation are already inside, per LLM call.

## 6. Options that matter (`ExtractionOptions`)

| Option | Default | Use |
|---|---|---|
| `Strategy` | `Both` | `Text` for Excel; `Vision` alone only for scanned PDFs |
| `MaxParallel` | 4 | concurrent LLM calls — tune to your provider's rate limit |
| `Decompose` | `true` | `false` = printed-level only, no atomic parts, fewer LLM calls |
| `FuzzyReconcile` | `true` | `false` skips the one extra LLM call that pairs paraphrased duplicates |
| `TextChunkChars` | 24 000 | text-leg chunk size (`ModelCapabilities.TextChunkCharsFor(model, …)` caps it for thinking models) |
| `GridChunkCells` | 600 | Excel plain-grid chunking; the deterministic colour/table paths ignore it |
| `OnProgress` | — | `Action<string>` — human-readable stage messages for logs/UI |
| `OnPartialResult` | — | fires per completed unit (page / chunk / sheet) with that unit's questions — this is how the bundled `serve` UI streams results live |
| `RetryDelay` | 2 s | base backoff (attempt N waits N×); set `TimeSpan.Zero` in tests |

## 7. Excel diagnostics from code

The two credential-free tools the CLI exposes are plain Core calls — useful in your own admin
screens or logs:

```csharp
using RfpExtractor.Core.Diagnostics;

var wb = await sheet.ExtractAsync(path, ct);                 // your ISpreadsheetExtractor
string dump = GridDump.Render(wb, Path.GetFileName(path));   // the resolved grid, one row per line
int lowerBound = SpreadsheetPipeline.CountAnswerableRows(wb.Sheets[0]);   // coverage-guard estimate
```

How the three-path Excel design works (colour → table → guarded LLM) is documented in
[`excel-extraction.md`](excel-extraction.md).

## 8. Integration checklist

- [ ] .NET 10; reference `RfpExtractor.Core` + one engine (or your own adapters per §4)
- [ ] Telerik engine only: DevCraft feed + licence file + `Telerik.Licensing` in the **entry** assembly
- [ ] LibreOffice engine only: Gotenberg container reachable (only needed for rendering/vision)
- [ ] Build an `IChatClient`; create `ModelProfile.For(model)`; pass it to all three LLM components
- [ ] Compose exactly as §2 step 4 (or copy `Wiring.CreateService`)
- [ ] Serialize outputs with `Json.Options` (snake_case contract)
- [ ] Surface `Report.Warnings` somewhere a human sees them
- [ ] Route `ReviewQueue` to a human
- [ ] Keys/endpoints from env vars or your own secret store — never hardcoded

## Support surface

| You want | Look at |
|---|---|
| the composition recipe | `src/RfpExtractor.Cli/Wiring.cs` (`CreateService` — 15 lines) |
| a full host with progress UI | `src/RfpExtractor.Cli/ServeCommand.cs` + `WebUi.cs` |
| gateway header injection | `src/RfpExtractor.Cli/GenCore/GenCoreChatClient.cs` |
| output field meanings | README → "`questions.json` field reference" |
| Excel extraction internals | `docs/excel-extraction.md` |
| change-safety rules & invariants | `AGENTS.md` |
