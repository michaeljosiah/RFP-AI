using Microsoft.Extensions.AI;
using RfpExtractor.Cli;
using RfpExtractor.Core.Abstractions;
using RfpExtractor.Core.Json;
using RfpExtractor.Core.Llm;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;
using RfpExtractor.Core.Reconciliation;

var positional = args.FirstOrDefault(a => !a.StartsWith("--"));
string Get(string k, string d) => args.FirstOrDefault(a => a.StartsWith($"--{k}="))?.Split('=', 2)[1] ?? d;

// ---- "rfpx serve" -> local monitoring UI ----
if (string.Equals(positional, "serve", StringComparison.OrdinalIgnoreCase))
    return await ServeCommand.RunAsync(args, Wiring.BuildConfig());

var file = positional;
if (file is null)
{
    Console.WriteLine("Usage: rfpx <file.docx|pdf|xlsx> [--engine=telerik|libreoffice] [--provider=gencore|azure|openai|claude]");
    Console.WriteLine("            [--model=gpt-4o] [--strategy=both|vision|text] [--granularity=hybrid|bundled|atomic]");
    Console.WriteLine("            [--dpi=200] [--max-parallel=4] [--out=DIR] [--chunk-chars=24000] [--no-fuzzy] [--adapters-only]");
    Console.WriteLine("       rfpx serve [--port=5177] [--provider=...] [--no-browser]   # real-time monitoring UI");
    return 1;
}
if (!File.Exists(file))
{
    Console.Error.WriteLine($"Input file not found: {file}");
    return 1;
}

var engine = Get("engine", "telerik").ToLowerInvariant();
var provider = Get("provider", "gencore").ToLowerInvariant();
var strategy = Enum.Parse<Strategy>(Get("strategy", "both"), ignoreCase: true);
var dpi = int.Parse(Get("dpi", "200"));
var maxParallel = int.Parse(Get("max-parallel", "4"));   // tune to the GenCore rate limit
var chunkChars = int.Parse(Get("chunk-chars", "24000")); // text-leg chunk size (smaller = more parallel chunks)
var outDir = Get("out", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "extracted"));
Directory.CreateDirectory(outDir);

var config = Wiring.BuildConfig();

IDocumentRenderer renderer;
IStructuredTextExtractor textExtractor;
ISpreadsheetExtractor spreadsheetExtractor;
try
{
    (renderer, textExtractor, spreadsheetExtractor) = Wiring.CreateEngine(engine, config);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// --- diagnostic: exercise the engine adapters only (no LLM / no credentials needed) ---
if (args.Contains("--adapters-only"))
{
    Console.WriteLine($"[engine] {engine}");
    var ext = Path.GetExtension(file).ToLowerInvariant();
    if (ext is ".xlsx" or ".xlsm" or ".xls")
    {
        var wb = await spreadsheetExtractor.ExtractAsync(file, CancellationToken.None);
        foreach (var s in wb.Sheets)
        {
            var nonEmpty = s.Cells.Count(c => !c.IsEmpty);
            Console.WriteLine($"[grid] sheet '{s.Name}': {s.Cells.Count} cells ({nonEmpty} non-empty, {s.Cells.Count - nonEmpty} empty/answer)");
            foreach (var c in s.Cells.Where(c => !c.IsEmpty).Take(6))
                Console.WriteLine($"        {c.Address} = \"{c.Text}\"");
        }
    }
    else
    {
        var sd = await textExtractor.ExtractAsync(file, CancellationToken.None);
        Console.WriteLine($"[text] markdown = {sd.Markdown.Length} chars, tables (ground truth) = {sd.Tables.Count}");
        var mdPath = Path.Combine(outDir, "markdown.md");
        File.WriteAllText(mdPath, sd.Markdown);
        Console.WriteLine($"[text] full markdown written to {mdPath}");
        Console.WriteLine("[text] markdown preview:");
        Console.WriteLine(sd.Markdown.Length > 600 ? sd.Markdown[..600] + " ..." : sd.Markdown);
    }

    var imgs = await renderer.RenderToImagesAsync(file, dpi, CancellationToken.None);
    var p1 = Path.Combine(outDir, "page-001.png");
    File.WriteAllBytes(p1, imgs[0].PngBytes);
    Console.WriteLine($"[render] {imgs.Count} page(s); wrote {p1} ({imgs[0].PngBytes.Length} bytes)");
    return 0;
}

// --- model provider -> IChatClient (GenCore gateway default, Azure OpenAI alternative) ---
IChatClient chat;
try
{
    var userEmail = Get("user-email", "");
    chat = Wiring.CreateChatClient(provider, Get("model", ""), config,
        string.IsNullOrWhiteSpace(userEmail) ? null : userEmail);
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var effModel = Wiring.EffectiveModel(provider, Get("model", ""), config);
var temperature = Wiring.TemperatureFor(effModel);
var nativeSchema = RfpExtractor.Core.Llm.ModelCapabilities.SupportsNativeJsonSchema(effModel);
var maxTokens = RfpExtractor.Core.Llm.ModelCapabilities.MaxOutputTokensFor(effModel);
ILlmExtractor llm = new AgentLlmExtractor(chat, temperature, nativeSchema, maxTokens);
IReconciler reconciler = new Reconciler(new AgentFuzzyMatcher(chat, temperature, nativeSchema, maxTokens));
var doc = new DocumentPipeline(renderer, textExtractor, llm, reconciler);
var sheet = new SpreadsheetPipeline(spreadsheetExtractor, renderer, llm, reconciler);
var router = new PipelineRouter(doc, sheet);

Console.WriteLine($"Extracting {Path.GetFileName(file)} (engine={engine}, provider={provider}, strategy={strategy}, dpi={dpi}, max-parallel={maxParallel}) ...");
var options = new ExtractionOptions(strategy, dpi, maxParallel,
    RfpExtractor.Core.Llm.ModelCapabilities.TextChunkCharsFor(effModel, chunkChars),
    OnProgress: msg => Console.WriteLine($"  {msg}"))    // long runs must not look hung
{ FuzzyReconcile = !args.Contains("--no-fuzzy") };
var result = await router.RunAsync(file, options, CancellationToken.None);

// Decompose printed questions into atomic parts + tag each for retrieval (category/format/units/
// external-input/comment). Deterministic per-question pass. Best-effort; --no-enrich skips it (leaving
// printed-level questions with no parts). Then AnswerSlots reflects the atomic total.
if (!args.Contains("--no-enrich"))
{
    Console.WriteLine("  decomposing questions into atomic parts + tagging...");
    await new AgentRetrievalEnricher(chat, temperature, nativeSchema, maxTokens).EnrichAsync(result.Merged, CancellationToken.None);
    result.Report.AnswerSlots = GranularityView.AtomicCount(result.Merged.Questions);
}

// Output granularity for questions.json: hybrid (default) | bundled | atomic. The extraction is
// always atomic internally; this is a deterministic presentation (see GranularityView).
Enum.TryParse<Granularity>(Get("granularity", "hybrid"), ignoreCase: true, out var granularity);
var view = GranularityView.Apply(result.Merged, granularity);

void Write(string name, object o) =>
    File.WriteAllText(Path.Combine(outDir, name), System.Text.Json.JsonSerializer.Serialize(o, Json.Options));

Write("document_schema.json", result.Merged.DocumentSchema);
Write("questions.json", view.Questions);
Write("review_queue.json", result.ReviewQueue);
Write("reconciliation_report.json", result.Report);

var rep = result.Report;
Console.WriteLine($"Merged {rep.MergedCount} questions " +
    $"({rep.AgreedCount} agreed, {result.ReviewQueue.Count} to review) -> {outDir}");
Console.WriteLine($"  granularity: {granularity.ToString().ToLowerInvariant()} -> {view.Questions.Count} entries" +
    $"  ·  {rep.PrintedQuestions} printed questions  ·  {rep.AnswerSlots} atomic slots");
Console.WriteLine($"  by source: {rep.BodyQuestions} body · {rep.TableCells} table cells ({rep.DataEntryTables} grids) · {rep.DocumentRequests} document uploads");
if (rep.Warnings.Count > 0)
    Console.WriteLine($"Warnings: {rep.Warnings.Count} (see reconciliation_report.json)");
return 0;
