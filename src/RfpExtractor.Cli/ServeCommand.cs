using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RfpExtractor.Core.Llm;
using RfpExtractor.Core.Models;
using RfpExtractor.Core.Pipeline;
using RfpExtractor.Core.Reconciliation;

namespace RfpExtractor.Cli;

/// <summary>
/// "rfpx serve" — a local monitoring UI. Animated launcher runs environment checks (engine,
/// provider config, live LLM connectivity ping), then a single-page app uploads a document and
/// streams pipeline progress + discovered questions back over Server-Sent Events in real time.
/// Fully self-contained (no CDNs); binds to localhost only.
/// </summary>
public static class ServeCommand
{
    private sealed class Job(string id, string fileName)
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public Channel<string> Events { get; } = Channel.CreateUnbounded<string>();
        public ReconciledResult? Result { get; set; }
        /// <summary>questions.json rendered at the chosen granularity (atomic extraction is Result.Merged).</summary>
        public List<Question> Questions { get; set; } = new();
    }

    private static string DefaultSaveDir(Job job) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "rfpx",
        $"{Path.GetFileNameWithoutExtension(job.FileName)}-{job.Id}");

    private sealed record JobOptions(string Engine, string Provider, string Model, Strategy Strategy, int Dpi, int MaxParallel, Granularity Granularity);

    private static string Ser(object o) => JsonSerializer.Serialize(o, Core.Json.Json.Compact);

    public static async Task<int> RunAsync(string[] args, IConfiguration config)
    {
        string Get(string k, string d) => args.FirstOrDefault(a => a.StartsWith($"--{k}="))?.Split('=', 2)[1] ?? d;
        var port = int.Parse(Get("port", "5177"));
        // serve-level defaults; the UI reads these on load (rfpx serve --provider=openai etc.)
        var defEngine = Get("engine", "telerik").ToLowerInvariant();
        var defProvider = Get("provider", "gencore").ToLowerInvariant();
        var defModel = Get("model", "");
        var jobs = new ConcurrentDictionary<string, Job>();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(WebUi.Html, "text/html"));

        app.MapGet("/api/defaults", () => Results.Json(new
        {
            engine = defEngine,
            provider = defProvider,
            model = Wiring.EffectiveModel(defProvider, defModel, config),
            host = $"127.0.0.1:{port}",
        }));

        // Local webfonts (Geist) — keeps the UI self-contained on corporate networks.
        app.MapGet("/fonts/{name}", (string name) =>
        {
            if (name.Contains("..") || !name.EndsWith(".woff2")) return Results.NotFound();
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts", name);
            return File.Exists(path) ? Results.File(path, "font/woff2") : Results.NotFound();
        });

        // ---- launcher checks (SSE): runtime -> engine -> provider config -> live LLM ping ----
        // ASP0016 false positive: the analyzer attributes the tuple-returning CHECK lambdas (passed
        // to the local Check() helper) to the route handler itself. The handler returns plain Task.
#pragma warning disable ASP0016
        app.MapGet("/api/checks", async Task (HttpContext ctx) =>
        {
            var engine = ctx.Request.Query["engine"].FirstOrDefault() ?? defEngine;
            var provider = ctx.Request.Query["provider"].FirstOrDefault() ?? defProvider;
            var model = ctx.Request.Query["model"].FirstOrDefault() ?? defModel;
            SseHeaders(ctx.Response);

            async Task Emit(object o) { await ctx.Response.WriteAsync($"data: {Ser(o)}\n\n"); await ctx.Response.Body.FlushAsync(); }
            bool configOk = false;

            async Task Check(string id, string name, Func<Task<(string status, string detail)>> run)
            {
                await Emit(new { kind = "check-start", id, name });
                var sw = Stopwatch.StartNew();
                string status, detail;
                try { (status, detail) = await run(); }
                catch (Exception ex) { (status, detail) = ("fail", Trim(ex.Message)); }
                await Emit(new { kind = "check", id, name, status, detail, ms = sw.ElapsedMilliseconds });
            }

            await Check("runtime", "Runtime", () =>
                Task.FromResult(("pass", $".NET {Environment.Version}")));

            await Check("engine", $"Document engine ({engine})", async () =>
            {
                if (engine == "libreoffice")
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    var url = Wiring.GotenbergUrl(config);
                    var r = await http.GetAsync($"{url.TrimEnd('/')}/health");
                    return r.IsSuccessStatusCode
                        ? ("pass", $"Gotenberg healthy at {url}")
                        : ("fail", $"Gotenberg at {url} returned {(int)r.StatusCode} — docker run -d -p 3000:3000 gotenberg/gotenberg:8");
                }
                var lic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Telerik", "telerik-license.txt");
                return File.Exists(lic)
                    ? ("pass", "Telerik licence key found")
                    : ("warn", "No Telerik licence key — exports will carry a trial banner");
            });

            await Check("config", $"LLM configuration ({provider})", () =>
            {
                if (provider == "azure")
                {
                    var ep = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? config["AzureOpenAIEndpoint"];
                    configOk = !string.IsNullOrWhiteSpace(ep);
                    if (!configOk)
                        return Task.FromResult(("fail", "Set AZURE_OPENAI_ENDPOINT (e.g. https://<resource>.openai.azure.com/openai/v1)"));
                    var azKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? config["AzureOpenAIApiKey"];
                    var auth = string.IsNullOrWhiteSpace(azKey) ? "Entra ID (az login)" : "API key";
                    return Task.FromResult(("pass",
                        $"{ep} · model (deployment) {Wiring.EffectiveModel(provider, model, config)} · auth: {auth}"));
                }
                if (provider == "openai")
                {
                    var oaKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? config["OpenAIApiKey"];
                    configOk = !string.IsNullOrWhiteSpace(oaKey);
                    return Task.FromResult(configOk
                        ? ("pass", $"api.openai.com · model {(string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model)}")
                        : ("fail", "Set OPENAI_API_KEY, then restart rfpx serve"));
                }
                if (provider is "claude" or "anthropic")
                {
                    var anKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? config["AnthropicApiKey"];
                    configOk = !string.IsNullOrWhiteSpace(anKey);
                    return Task.FromResult(configOk
                        ? ("pass", $"api.anthropic.com · model {Wiring.EffectiveModel(provider, model, config)}")
                        : ("fail", "Set ANTHROPIC_API_KEY, then restart rfpx serve"));
                }
                var proxy = config["AzureOpenAIProxyName"] ?? "GenerativeCore";
                var baseUri = config[$"AzureOpenAIProxySettings:{proxy}:BaseUri"];
                var key = config["EnterpriseGenCoreApiKey"];
                if (string.IsNullOrWhiteSpace(baseUri)) return Task.FromResult(("fail", "GenCore BaseUri missing from appsettings.json"));
                if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(("fail", "Set env var EnterpriseGenCoreApiKey, then restart rfpx serve"));
                configOk = true;
                return Task.FromResult(("pass", $"gateway {baseUri} · model {(string.IsNullOrWhiteSpace(model) ? Wiring.ResolveDefaultModel(config) : model)}"));
            });

            await Check("llm", "LLM connectivity", async () =>
            {
                if (!configOk) return ("fail", "Skipped — fix LLM configuration first");
                var chat = Wiring.CreateChatClient(provider, model, config);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var sw = Stopwatch.StartNew();
                // Generous cap: reasoning / adaptive-thinking models (gpt-5, Claude) spend output
                // tokens thinking before the final "OK", so a tiny limit makes a healthy endpoint
                // look dead (empty reply or a min-tokens error).
                var resp = await chat.GetResponseAsync("Reply with the single word: OK",
                    new ChatOptions { MaxOutputTokens = 2048 }, cts.Token);
                var reply = resp.Text.Trim();
                return ("pass", $"model replied \"{Trim(string.IsNullOrEmpty(reply) ? "(ok)" : reply, 20)}\" in {sw.ElapsedMilliseconds} ms");
            });

            await Emit(new { kind = "checks-done" });
        });
#pragma warning restore ASP0016

        // ---- start an extraction: multipart upload + options -> job id ----
        app.MapPost("/api/extract", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No file uploaded." });

            var id = Guid.NewGuid().ToString("N")[..8];
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rfpx-ui", id)).FullName;
            var path = Path.Combine(dir, Path.GetFileName(file.FileName));
            await using (var fs = File.Create(path)) await file.CopyToAsync(fs);

            var o = new JobOptions(
                Engine: form["engine"].FirstOrDefault() ?? "telerik",
                Provider: form["provider"].FirstOrDefault() ?? "gencore",
                Model: form["model"].FirstOrDefault() ?? "",
                Strategy: Enum.TryParse<Strategy>(form["strategy"].FirstOrDefault(), true, out var s) ? s : Strategy.Both,
                Dpi: int.TryParse(form["dpi"].FirstOrDefault(), out var d) ? d : 200,
                MaxParallel: int.TryParse(form["maxparallel"].FirstOrDefault(), out var mp) ? mp : 4,
                Granularity: Enum.TryParse<Granularity>(form["granularity"].FirstOrDefault(), true, out var g) ? g : Granularity.Hybrid);

            var job = new Job(id, Path.GetFileName(path));
            jobs[id] = job;
            _ = Task.Run(() => RunJobAsync(job, path, o, config));
            return Results.Json(new { id });
        });

        // ---- realtime job events (SSE) ----
        app.MapGet("/api/events/{id}", async (string id, HttpContext ctx) =>
        {
            if (!jobs.TryGetValue(id, out var job)) { ctx.Response.StatusCode = 404; return; }
            SseHeaders(ctx.Response);
            await foreach (var payload in job.Events.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                await ctx.Response.WriteAsync($"data: {payload}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });

        // ---- persist all four result files to a server-side folder ----
        app.MapPost("/api/save/{id}", async (string id, HttpContext ctx) =>
        {
            if (!jobs.TryGetValue(id, out var job) || job.Result is null)
                return Results.NotFound(new { error = "Job not found or not finished." });

            string? dir = null;
            try
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (body.RootElement.TryGetProperty("dir", out var d)) dir = d.GetString();
            }
            catch { /* empty body -> default dir */ }
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultSaveDir(job);

            try
            {
                Directory.CreateDirectory(dir);
                var r = job.Result;
                var opts = Core.Json.Json.Options;
                await File.WriteAllTextAsync(Path.Combine(dir, "document_schema.json"), JsonSerializer.Serialize(r.Merged.DocumentSchema, opts));
                await File.WriteAllTextAsync(Path.Combine(dir, "questions.json"), JsonSerializer.Serialize(job.Questions, opts));
                await File.WriteAllTextAsync(Path.Combine(dir, "review_queue.json"), JsonSerializer.Serialize(r.ReviewQueue, opts));
                await File.WriteAllTextAsync(Path.Combine(dir, "reconciliation_report.json"), JsonSerializer.Serialize(r.Report, opts));
                return Results.Json(new { dir = Path.GetFullPath(dir) });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = Trim(ex.Message) }, statusCode: 500);
            }
        });

        // ---- result downloads ----
        app.MapGet("/api/result/{id}/{name}", (string id, string name) =>
        {
            if (!jobs.TryGetValue(id, out var job) || job.Result is null) return Results.NotFound();
            object? o = name switch
            {
                "schema" => job.Result.Merged.DocumentSchema,
                "questions" => job.Questions,
                "review" => job.Result.ReviewQueue,
                "report" => job.Result.Report,
                _ => null,
            };
            return o is null ? Results.NotFound()
                             : Results.Text(JsonSerializer.Serialize(o, Core.Json.Json.Options), "application/json");
        });

        // Bind FIRST, and only then open the browser — otherwise a port clash silently sends the
        // user to whatever old instance is already listening on this port.
        try
        {
            await app.StartAsync();
        }
        catch (Exception ex) when (ex is IOException || ex.InnerException is System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Port {port} is already in use — is another 'rfpx serve' still running?");
            Console.Error.WriteLine($"Stop it, or pick a different port:  rfpx serve --port={port + 1}");
            return 1;
        }

        var url = $"http://localhost:{port}";
        Console.WriteLine($"rfpx monitoring UI: {url}  (engine={defEngine}, provider={defProvider})  Ctrl+C to stop");
        if (!args.Contains("--no-browser"))
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { /* headless */ }

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static async Task RunJobAsync(Job job, string path, JobOptions o, IConfiguration config)
    {
        void Push(object p) => job.Events.Writer.TryWrite(Ser(p));
        try
        {
            // One composition point (same stack as the batch CLI): engine + provider + model profile.
            var (service, model) = Wiring.CreateService(o.Engine, o.Provider, o.Model, config);

            Push(new { kind = "stage", file = job.FileName, engine = o.Engine, provider = o.Provider,
                       model, strategy = o.Strategy.ToString().ToLowerInvariant() });

            var progress = new ProgressParser(Push);
            var opts = new ExtractionOptions(o.Strategy, o.Dpi, o.MaxParallel,
                ModelCapabilities.TextChunkCharsFor(model, 24_000), OnProgress: progress.Handle)
            {
                OnPartialResult = (leg, r) => Push(new
                {
                    kind = "questions",
                    leg,
                    items = r.Questions.Select(q => new
                    {
                        text = q.QuestionText,
                        section = q.SectionPath,
                        type = q.AnswerType.ToString().ToLowerInvariant(),
                        source = q.Source.ToString().ToLowerInvariant(),
                    }),
                }),
            };

            var sw = Stopwatch.StartNew();
            var result = await service.RunAsync(path, opts, CancellationToken.None);

            job.Result = result;
            job.Questions = GranularityView.Apply(result.Merged, o.Granularity).Questions;   // questions.json at chosen granularity
            Push(new
            {
                kind = "done",
                merged = result.Report.MergedCount,
                agreed = result.Report.AgreedCount,
                review = result.ReviewQueue.Count,
                warnings = result.Report.Warnings,
                granularity = o.Granularity.ToString().ToLowerInvariant(),
                printed = result.Report.PrintedQuestions,
                atomic = result.Report.AnswerSlots,
                entries = job.Questions.Count,
                duration_ms = sw.ElapsedMilliseconds,
                save_dir = DefaultSaveDir(job),
            });
        }
        catch (Exception ex)
        {
            Push(new { kind = "error", message = Trim(ex.Message) });
        }
        finally
        {
            job.Events.Writer.TryComplete();
        }
    }

    /// <summary>Turns OnProgress strings into structured progress events for the UI (plus raw log lines).</summary>
    private sealed class ProgressParser(Action<object> push)
    {
        private static readonly Regex VisionTotal = new(@"vision: (\d+) page\(s\)", RegexOptions.Compiled);
        private static readonly Regex VisionDone = new(@"vision page \d+: done", RegexOptions.Compiled);
        private static readonly Regex TextTotal = new(@"-> (\d+) chunk\(s\)", RegexOptions.Compiled);
        private static readonly Regex TextDone = new(@"text chunk \d+/(\d+): done", RegexOptions.Compiled);
        private static readonly Regex GridTotal = new(@"grid: (\d+) chunk\(s\)", RegexOptions.Compiled);
        private static readonly Regex SheetDone = new(@"grid chunk \d+/\d+ .*: done", RegexOptions.Compiled);
        private static readonly Regex DecompTotal = new(@"-> (\d+) batch\(es\)", RegexOptions.Compiled);
        private static readonly Regex DecompDone = new(@"decompose batch \d+/(\d+): done", RegexOptions.Compiled);
        private int _vTot, _vDone, _tTot, _tDone, _gTot, _gDone, _dTot, _dDone;

        public void Handle(string msg)
        {
            lock (this)   // OnProgress fires from parallel tasks
            {
                push(new { kind = "log", message = msg });
                Match m;
                if ((m = VisionTotal.Match(msg)).Success) { _vTot = int.Parse(m.Groups[1].Value); Send("vision", _vDone, _vTot); }
                else if (VisionDone.IsMatch(msg)) Send("vision", ++_vDone, _vTot);
                else if ((m = TextTotal.Match(msg)).Success) { _tTot = int.Parse(m.Groups[1].Value); Send("text", _tDone, _tTot); }
                else if ((m = TextDone.Match(msg)).Success) { _tTot = int.Parse(m.Groups[1].Value); Send("text", ++_tDone, _tTot); }
                else if ((m = GridTotal.Match(msg)).Success) { _gTot = int.Parse(m.Groups[1].Value); Send("grid", _gDone, _gTot); }
                else if (SheetDone.IsMatch(msg)) Send("grid", ++_gDone, _gTot);
                else if ((m = DecompTotal.Match(msg)).Success) { _dTot = int.Parse(m.Groups[1].Value); Send("decompose", _dDone, _dTot); }
                else if ((m = DecompDone.Match(msg)).Success) { _dTot = int.Parse(m.Groups[1].Value); Send("decompose", ++_dDone, _dTot); }
            }
        }

        private void Send(string leg, int done, int total) => push(new { kind = "progress", leg, done, total });
    }

    private static void SseHeaders(HttpResponse r)
    {
        r.Headers.ContentType = "text/event-stream";
        r.Headers.CacheControl = "no-cache";
        r.Headers.Connection = "keep-alive";
    }

    private static string Trim(string s, int max = 220) => s.Length <= max ? s : s[..max] + "…";
}
