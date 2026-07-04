namespace RfpExtractor.Cli;

/// <summary>
/// The monitoring UI, implementing the "RFP Extractor Monitor" design (SpecOne / shadcn-neutral
/// tokens, Geist type, #D96F35 accent) 1:1 — excluding the prototype scenario picker and restart
/// button. Three screens: launcher (splash + preflight), new ingestion (configure), workbench
/// (running/complete console). Fully wired to the real backend: /api/defaults, /api/checks (SSE),
/// /api/extract, /api/events/{id} (SSE), /api/result/{id}/{name}, /api/save/{id}. Self-contained —
/// fonts served locally from /fonts, no CDNs.
/// </summary>
internal static class WebUi
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>c. RFP Extractor</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Ctext x='2' y='48' font-family='Arial' font-weight='800' font-size='52' fill='%2314161F'%3Ec%3C/text%3E%3Crect x='44' y='34' width='14' height='14' fill='%23D96F35'/%3E%3C/svg%3E">
<style>
@font-face { font-family:'Geist'; font-style:normal; font-weight:100 900; font-display:swap;
  src:url('/fonts/geist-latin.woff2') format('woff2'); }
@font-face { font-family:'Geist Mono'; font-style:normal; font-weight:100 900; font-display:swap;
  src:url('/fonts/geist-mono-latin.woff2') format('woff2'); }

:root {
  --background:#FFFFFF; --foreground:#0A0A0A; --card:#FFFFFF;
  --border:#E5E5E5; --input:#E5E5E5; --muted:#F5F5F5; --muted-foreground:#737373;
  --secondary:#F5F5F5; --primary:#171717;
  --success:#16A34A; --success-bg:#F0FDF4; --warning:#D97706; --warning-bg:#FFFBEB;
  --destructive:#E7000B; --danger-bg:#FEF2F2;
  --neutral-50:#FAFAFA; --neutral-100:#F5F5F5; --neutral-200:#E5E5E5; --neutral-300:#D4D4D4;
  --neutral-400:#A1A1A1; --neutral-500:#737373; --neutral-600:#525252; --neutral-700:#404040;
  --line-soft:var(--neutral-100); --text-secondary:var(--neutral-700);
  --radius:0.625rem; --radius-md:calc(var(--radius) - 2px); --radius-lg:var(--radius); --radius-xl:calc(var(--radius) + 4px);
  --shadow-xs:0 1px 2px 0 rgba(0,0,0,0.05);
  --font-sans:'Geist', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif;
  --font-mono:'Geist Mono', ui-monospace, SFMono-Regular, Menlo, monospace;
  --ease-out:cubic-bezier(0.16,1,0.3,1); --ease-standard:cubic-bezier(0.4,0,0.2,1); --dur-fast:150ms;
  --transition-control:color var(--dur-fast) var(--ease-standard), background-color var(--dur-fast) var(--ease-standard), border-color var(--dur-fast) var(--ease-standard), box-shadow var(--dur-fast) var(--ease-standard);
}
* { box-sizing:border-box; }
html, body { margin:0; padding:0; height:100%; background:#FFFFFF; }
button { font-family:var(--font-sans); }
.hidden { display:none !important; }

@keyframes rfpx-c-in { from { opacity:0; transform:translateY(8px); filter:blur(8px); } to { opacity:1; transform:translateY(0); filter:blur(0); } }
@keyframes rfpx-dot-in { 0% { opacity:0; transform:translateY(-30px) scale(0.4); } 62% { opacity:1; transform:translateY(3px) scale(1.14); } 100% { opacity:1; transform:translateY(0) scale(1); } }
@keyframes rfpx-dot-blink { 0%, 100% { opacity:1; } 50% { opacity:0.25; } }
@keyframes rfpx-splash-out { to { opacity:0; transform:translateY(-18px); } }
@keyframes rfpx-row-in { from { opacity:0; transform:translateY(4px); } to { opacity:1; transform:translateY(0); } }
@keyframes rfpx-spin { to { transform:rotate(360deg); } }

.btn { display:inline-flex; align-items:center; justify-content:center; gap:6px; font-weight:500;
  border-radius:var(--radius-md); border:1px solid transparent; cursor:pointer; white-space:nowrap;
  flex:none; transition:var(--transition-control); }
.btn:disabled { opacity:0.5; pointer-events:none; }
.btn-sm { height:32px; padding:0 12px; font-size:13px; }
.btn-lg { height:40px; padding:0 20px; font-size:14px; }
.btn-accent { background:#D96F35; color:#FFFFFF; }
.btn-accent:hover { background:#C9622C; }
.btn-outline { background:var(--background); color:var(--foreground); border-color:var(--border); box-shadow:var(--shadow-xs); }
.btn-outline:hover { background:var(--neutral-50); }
.btn-ghost { background:transparent; color:var(--foreground); }
.btn-ghost:hover { background:var(--muted); }

select, input[type=text] { outline:none; }
.field-label { font-size:11px; text-transform:uppercase; letter-spacing:0.06em; color:var(--muted-foreground); }
.field-select { height:34px; border:1px solid var(--input); border-radius:var(--radius-md); padding:0 8px;
  font-size:13px; font-family:var(--font-sans); background:var(--background); color:var(--foreground); }
.spinner { width:12px; height:12px; border:2px solid var(--neutral-200); border-top-color:#D96F35;
  border-radius:50%; animation:rfpx-spin 0.7s linear infinite; display:inline-block; }
.pill { font-family:var(--font-mono); font-size:11px; padding:2px 9px; border-radius:999px; flex:none; margin-top:2px; }
.icon-btn { width:24px; height:24px; display:inline-flex; align-items:center; justify-content:center;
  background:transparent; border:none; border-radius:6px; cursor:pointer; padding:0; flex:none; }
</style>
</head>
<body>
<div style="height:100vh; display:flex; flex-direction:column; background:var(--background); color:var(--foreground); font-family:var(--font-sans); overflow:hidden;">

  <!-- Top bar (brand chrome; scenario picker + restart excluded by request) -->
  <div style="height:48px; flex:none; display:flex; align-items:center; gap:20px; padding:0 16px; border-bottom:1px solid var(--border); background:var(--background); z-index:60;">
    <div style="display:flex; align-items:flex-end; gap:3px;">
      <span style="font-weight:800; font-size:19px; line-height:0.8; letter-spacing:-0.02em; color:#14161F;">c</span>
      <span style="width:5px; height:5px; background:#D96F35; margin-bottom:1px;"></span>
    </div>
    <span style="font-size:13px; font-weight:500;">RFP Extractor</span>
    <span style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground);">monitoring UI</span>
    <div style="flex:1;"></div>
    <button id="newFileBtn" class="btn btn-outline btn-sm hidden" onclick="newIngestion()" title="Abandon this run and start a new ingestion">
      <span style="width:8px; height:8px; background:#D96F35; flex:none;"></span>
      New file
    </button>
  </div>

  <div style="position:relative; flex:1; min-height:0; display:flex; flex-direction:column;">

    <!-- ============ Splash ============ -->
    <div id="splash" style="position:absolute; inset:0; z-index:50; display:flex; align-items:center; justify-content:center; background:var(--background);">
      <div style="display:flex; align-items:flex-end; gap:12px;">
        <span style="font-weight:800; font-size:112px; line-height:0.74; letter-spacing:-0.03em; color:#14161F; animation:rfpx-c-in 0.65s var(--ease-out) both;">c</span>
        <span style="display:inline-block; animation:rfpx-dot-in 0.55s cubic-bezier(0.34,1.56,0.64,1) 0.5s both;">
          <span style="display:block; width:25px; height:25px; background:#D96F35; animation:rfpx-dot-blink 0.55s var(--ease-out) 1.15s 1;"></span>
        </span>
      </div>
    </div>

    <!-- ============ Preflight ============ -->
    <div id="preflight" class="hidden" style="flex:1; display:flex; align-items:center; justify-content:center; background:var(--background); overflow:auto;">
      <div style="width:620px; max-width:92%;">
        <div style="display:flex; align-items:flex-end; gap:4px; margin-bottom:18px; animation:rfpx-row-in 0.3s var(--ease-out) both;">
          <span style="font-weight:800; font-size:26px; line-height:0.78; letter-spacing:-0.02em; color:#14161F;">c</span>
          <span style="width:6px; height:6px; background:#D96F35; margin-bottom:1px;"></span>
          <span id="pfHost" style="font-family:var(--font-mono); font-size:12px; color:var(--muted-foreground); margin-left:10px;">rfpx serve</span>
        </div>
        <div style="background:var(--card); border:1px solid var(--border); border-radius:var(--radius-xl); box-shadow:var(--shadow-xs); overflow:hidden; animation:rfpx-row-in 0.3s var(--ease-out) 0.05s both;">
          <div style="padding:16px 20px 12px; display:flex; align-items:baseline; justify-content:space-between; gap:12px; flex-wrap:wrap;">
            <span style="font-size:15px; font-weight:600; letter-spacing:-0.01em;">Preflight checks</span>
            <span id="pfConfig" style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground);"></span>
          </div>
          <div id="pfRows"></div>
          <div id="pfPassed" class="hidden" style="display:flex; align-items:center; gap:8px; padding:13px 20px; border-top:1px solid var(--line-soft); background:var(--neutral-50);">
            <span style="width:8px; height:8px; border-radius:999px; background:var(--success);"></span>
            <span style="font-size:13px; color:var(--text-secondary);">All checks passed &mdash; continuing to configuration&hellip;</span>
          </div>
          <div id="pfFailed" class="hidden" style="padding:13px 20px; border-top:1px solid var(--line-soft); background:var(--neutral-50); display:flex; align-items:center; gap:10px; flex-wrap:wrap;">
            <span style="width:8px; height:8px; border-radius:999px; background:var(--destructive);"></span>
            <span id="pfFailedMsg" style="font-size:13px; color:var(--text-secondary); flex:1; min-width:200px;">One check failed. Fix the environment and retry, or proceed manually.</span>
            <button class="btn btn-outline btn-sm" onclick="retryChecks()">Retry checks</button>
            <button class="btn btn-ghost btn-sm" onclick="proceedAnyway()">Proceed anyway</button>
          </div>
        </div>
        <div style="font-size:12px; line-height:1.55; color:var(--muted-foreground); margin-top:14px; animation:rfpx-row-in 0.3s var(--ease-out) 0.1s both;">Connectivity performs a live request to the configured provider &mdash; it does not merely confirm that configuration values exist.</div>
      </div>
    </div>

    <!-- ============ Configure ============ -->
    <div id="configure" class="hidden" style="flex:1; overflow:auto; background:var(--neutral-50);">
      <div style="max-width:980px; margin:0 auto; padding:44px 24px 40px;">
        <div style="display:flex; align-items:flex-end; justify-content:space-between; gap:16px; flex-wrap:wrap; margin-bottom:22px;">
          <div>
            <h1 style="margin:0; font-size:22px; font-weight:600; letter-spacing:-0.02em;">New ingestion</h1>
            <div style="font-size:13px; color:var(--muted-foreground); margin-top:4px;">Configuration defaults come from the server's startup flags. One document, one run.</div>
          </div>
          <span id="pfChip" style="display:inline-flex; align-items:center; gap:7px; font-family:var(--font-mono); font-size:11.5px; color:var(--muted-foreground); background:var(--background); border:1px solid var(--border); padding:4px 11px; border-radius:999px;">
            <span id="pfChipDot" style="width:7px; height:7px; border-radius:999px; background:var(--success);"></span>
            <span id="pfChipLabel">4 preflight checks passed</span>
          </span>
        </div>

        <div style="display:grid; grid-template-columns:1fr 1fr; gap:16px; align-items:start;">
          <!-- Input document -->
          <div style="background:var(--card); border:1px solid var(--border); border-radius:var(--radius-xl); box-shadow:var(--shadow-xs); overflow:hidden;">
            <div style="padding:16px 18px 10px;">
              <div style="font-size:14px; font-weight:600;">Input document</div>
              <div style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground); margin-top:3px;">.docx &middot; .pdf &middot; .xlsx / .xlsm / .xls</div>
            </div>
            <div style="display:flex; flex-direction:column; gap:8px; padding:6px 14px 14px;">
              <input id="fileInput" type="file" accept=".docx,.pdf,.xlsx,.xlsm,.xls" style="display:none;">
              <div id="dropzone" style="display:flex; flex-direction:column; justify-content:center; min-height:118px; border:1px dashed var(--neutral-300); border-radius:var(--radius-md); padding:14px 16px; cursor:pointer; background:var(--neutral-50); transition:var(--transition-control);">
                <div id="dzEmpty" style="display:flex; flex-direction:column; align-items:center; gap:6px; text-align:center;">
                  <span style="width:10px; height:10px; background:#D96F35;"></span>
                  <span style="font-size:13px; font-weight:500;">Drop a questionnaire here, or click to browse</span>
                  <span style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground);">.docx &middot; .pdf &middot; .xlsx / .xlsm / .xls</span>
                </div>
                <div id="dzFile" class="hidden">
                  <div style="display:flex; align-items:center; gap:10px; min-width:0;">
                    <span style="width:8px; height:8px; background:#D96F35; flex:none;"></span>
                    <div style="flex:1; min-width:0;">
                      <div id="dzName" style="font-size:13px; font-weight:500; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;"></div>
                      <div id="dzMeta" style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground); margin-top:2px;"></div>
                    </div>
                    <button onclick="clearFile(event)" style="height:26px; padding:0 10px; font-size:11.5px; font-weight:500; background:var(--background); color:var(--foreground); border:1px solid var(--border); border-radius:6px; cursor:pointer; flex:none; white-space:nowrap;">Remove</button>
                  </div>
                  <div style="font-size:11.5px; color:var(--muted-foreground); margin-top:10px;">Drop another file, or click to replace.</div>
                </div>
              </div>
              <div id="fileError" class="hidden" style="font-size:12px; color:var(--destructive);"></div>
              <div style="font-size:12px; color:var(--muted-foreground); padding:2px 4px 0;">Ingestion cannot start until a valid document is provided. The upload stays on this machine and is discarded unless saved.</div>
            </div>
          </div>

          <!-- Run configuration -->
          <div style="background:var(--card); border:1px solid var(--border); border-radius:var(--radius-xl); box-shadow:var(--shadow-xs);">
            <div style="padding:16px 18px 10px;">
              <div style="font-size:14px; font-weight:600;">Run configuration</div>
              <div style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground); margin-top:3px;">defaults: server startup settings</div>
            </div>
            <div style="display:grid; grid-template-columns:1fr 1fr; gap:12px 14px; padding:6px 18px 18px;">
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">Document engine</span>
                <select id="cfgEngine" class="field-select">
                  <option value="telerik">telerik</option>
                  <option value="libreoffice">conversion-service</option>
                </select>
              </label>
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">LLM provider</span>
                <select id="cfgProvider" class="field-select">
                  <option value="gencore">enterprise-gateway</option>
                  <option value="azure">azure-openai</option>
                  <option value="openai">openai</option>
                  <option value="claude">anthropic-claude</option>
                </select>
              </label>
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">Model</span>
                <input id="cfgModel" type="text" value="gpt-4o" style="height:34px; border:1px solid var(--input); border-radius:var(--radius-md); padding:0 10px; font-size:13px; font-family:var(--font-mono); background:var(--background); color:var(--foreground);">
              </label>
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">Extraction strategy</span>
                <select id="cfgStrategy" class="field-select">
                  <option value="both">dual-leg + reconcile</option>
                  <option value="vision">vision only</option>
                  <option value="text">text only</option>
                </select>
              </label>
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">Granularity</span>
                <select id="cfgGranularity" class="field-select" title="hybrid: printed questions with atomic parts nested · bundled: printed questions only · atomic: one entry per distinct ask">
                  <option value="hybrid">hybrid (printed + atomic parts)</option>
                  <option value="bundled">bundled (printed only)</option>
                  <option value="atomic">atomic (one per ask)</option>
                </select>
              </label>
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">Render resolution</span>
                <select id="cfgResolution" class="field-select">
                  <option value="1.5">1.5&times;</option>
                  <option value="2.0" selected>2.0&times;</option>
                  <option value="3.0">3.0&times;</option>
                </select>
              </label>
              <label style="display:flex; flex-direction:column; gap:5px;">
                <span class="field-label">Concurrency</span>
                <select id="cfgConcurrency" class="field-select">
                  <option value="2">2</option>
                  <option value="4" selected>4</option>
                  <option value="8">8</option>
                </select>
              </label>
            </div>
          </div>
        </div>

        <div style="display:flex; align-items:center; gap:16px; margin-top:20px;">
          <button id="startBtn" class="btn btn-accent btn-lg" disabled onclick="startRun()">Start ingestion</button>
          <span style="font-family:var(--font-mono); font-size:11.5px; color:var(--muted-foreground);">same pipeline as the CLI &mdash; engines, reconciliation and resilience are identical</span>
        </div>
      </div>
    </div>

    <!-- ============ Console: running + complete ============ -->
    <div id="console" class="hidden" style="flex:1; min-height:0; display:flex; flex-direction:column; background:var(--neutral-50);">

      <div id="fatalBox" class="hidden" style="margin:12px 16px 0; flex:none; border:1px solid var(--destructive); background:var(--danger-bg); border-radius:var(--radius-lg); padding:12px 16px; display:flex; gap:12px; align-items:flex-start; animation:rfpx-row-in 0.25s var(--ease-out) both;">
        <span style="width:8px; height:8px; border-radius:999px; background:var(--destructive); flex:none; margin-top:5px;"></span>
        <div style="flex:1; min-width:0;">
          <div id="fatalTitle" style="font-size:13.5px; font-weight:600; color:var(--destructive);"></div>
          <div id="fatalDetail" style="font-family:var(--font-mono); font-size:12px; color:var(--text-secondary); margin-top:3px; line-height:1.5;"></div>
          <div id="fatalNote" style="font-size:12.5px; color:var(--muted-foreground); margin-top:5px;"></div>
        </div>
        <button class="btn btn-outline btn-sm" onclick="backToConfig()">Back to configuration</button>
      </div>

      <!-- Job header strip -->
      <div style="flex:none; display:flex; align-items:center; gap:8px; flex-wrap:wrap; padding:12px 16px 10px;">
        <span id="hdrFile" style="font-size:14px; font-weight:600; letter-spacing:-0.01em;"></span>
        <span id="hdrChips" style="display:contents;"></span>
        <div style="flex:1;"></div>
        <span id="hdrQCount" style="font-size:20px; font-weight:600; letter-spacing:-0.02em; color:#D96F35;">0</span>
        <span style="font-size:12px; color:var(--muted-foreground);">questions</span>
        <span id="hdrInternal" class="hidden" style="font-family:var(--font-mono); font-size:11px; color:var(--warning); background:var(--warning-bg); border:1px solid #FDE68A; padding:2px 8px; border-radius:999px;" title="Internal-only section — not answered by the responder"></span>
        <span id="hdrElapsed" style="font-family:var(--font-mono); font-size:12px; color:var(--muted-foreground); margin-left:8px;">0.0 s</span>
        <span style="display:inline-flex; align-items:center; gap:7px; margin-left:8px;">
          <span id="runDot" style="width:8px; height:8px; border-radius:999px; background:#D96F35; animation:rfpx-dot-blink 1.4s var(--ease-out) infinite;"></span>
          <span id="runStatus" style="font-family:var(--font-mono); font-size:11.5px; color:var(--muted-foreground);">running</span>
        </span>
      </div>

      <!-- Workbench grid -->
      <div style="flex:1; min-height:0; display:grid; gap:12px; padding:0 16px 16px; grid-template-columns:minmax(340px,1fr) minmax(320px,440px); grid-template-rows:minmax(0,1.5fr) minmax(150px,1fr); grid-template-areas:'main side' 'main log';">

        <!-- Side: progress / summary -->
        <div style="grid-area:side; background:var(--card); border:1px solid var(--border); border-radius:var(--radius-xl); box-shadow:var(--shadow-xs); display:flex; flex-direction:column; min-height:0; min-width:0; overflow:hidden;">
          <div id="progressView" style="display:contents;">
            <div style="flex:none; padding:13px 16px 9px; font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--muted-foreground);">Progress</div>
            <div id="legList" style="flex:1; min-height:0; overflow:auto; display:flex; flex-direction:column; gap:14px; padding:2px 16px 16px;"></div>
          </div>
          <div id="summaryView" class="hidden" style="display:none; flex-direction:column; min-height:0; flex:1;">
            <div style="flex:none; padding:9px 16px 5px; display:flex; align-items:center; gap:8px;">
              <span style="width:7px; height:7px; border-radius:999px; background:var(--success); flex:none;"></span>
              <span style="font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--muted-foreground);">Run complete</span>
            </div>
            <div style="flex:1; min-height:0; overflow:auto; padding:0 16px 8px; display:flex; flex-direction:column; gap:6px;">
              <div id="metricRow" style="display:flex; flex-wrap:wrap; gap:3px 18px;"></div>
              <div id="warnBox" class="hidden" style="border:1px solid #FDE68A; background:var(--warning-bg); border-radius:var(--radius-md); padding:3px 10px; min-width:0;"></div>
              <div>
                <div style="font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--muted-foreground); margin-bottom:3px;">Artifacts</div>
                <div id="artifactGrid" style="display:grid; grid-template-columns:1fr 1fr; gap:4px;"></div>
              </div>
              <div>
                <div style="display:flex; align-items:center; gap:10px; margin-bottom:3px; min-width:0;">
                  <span style="font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--muted-foreground); flex:none;">Save all to disk</span>
                  <span id="savedChip" class="hidden" style="display:inline-flex; align-items:center; gap:6px; min-width:0;">
                    <span style="width:6px; height:6px; border-radius:999px; background:var(--success); flex:none;"></span>
                    <span id="savedTo" style="font-family:var(--font-mono); font-size:11px; color:var(--text-secondary); white-space:nowrap; overflow:hidden; text-overflow:ellipsis;"></span>
                  </span>
                  <span id="saveError" class="hidden" style="font-size:11px; color:var(--destructive); white-space:nowrap; overflow:hidden; text-overflow:ellipsis;"></span>
                </div>
                <div style="display:flex; gap:8px;">
                  <input id="savePath" type="text" style="flex:1; min-width:0; height:28px; border:1px solid var(--input); border-radius:var(--radius-md); padding:0 10px; font-size:11.5px; font-family:var(--font-mono); background:var(--background); color:var(--foreground);">
                  <button id="saveBtn" class="btn btn-accent btn-sm" onclick="saveAll()">Save</button>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- Main: questions / JSON viewer -->
        <div style="grid-area:main; background:var(--card); border:1px solid var(--border); border-radius:var(--radius-xl); box-shadow:var(--shadow-xs); display:flex; flex-direction:column; min-height:0; min-width:0; overflow:hidden;">
          <div id="questionsView" style="display:contents;">
            <div style="flex:none; display:flex; align-items:center; gap:10px; padding:13px 16px 9px;">
              <span style="font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--muted-foreground);">Questions</span>
              <span id="qBadge" style="font-family:var(--font-mono); font-size:11px; background:var(--secondary); padding:1px 8px; border-radius:999px;">0</span>
              <span id="streamingChip" style="display:inline-flex; align-items:center; gap:6px; font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground);">
                <span style="width:6px; height:6px; border-radius:999px; background:#D96F35; animation:rfpx-dot-blink 1.4s var(--ease-out) infinite;"></span>
                streaming
              </span>
            </div>
            <div id="qScroll" style="flex:1; min-height:0; overflow:auto;">
              <div id="qEmpty" style="padding:22px 16px; font-size:12.5px; color:var(--muted-foreground);">Questions stream in as pages, chunks and sheets complete &mdash; before the run finishes.</div>
              <div id="qList"></div>
            </div>
          </div>
          <div id="viewerView" class="hidden" style="display:none; flex-direction:column; min-height:0; flex:1;">
            <div style="flex:none; display:flex; align-items:center; gap:10px; padding:10px 16px 9px; border-bottom:1px solid var(--line-soft);">
              <button onclick="closeViewer()" style="height:26px; padding:0 10px; font-size:11.5px; font-weight:500; background:var(--background); color:var(--foreground); border:1px solid var(--border); border-radius:6px; cursor:pointer; flex:none; white-space:nowrap;">&lsaquo; Back to questions</button>
              <span id="viewerFile" style="font-family:var(--font-mono); font-size:12px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;"></span>
              <span id="viewerSize" style="font-family:var(--font-mono); font-size:11px; color:var(--muted-foreground); flex:none;"></span>
            </div>
            <div id="viewerBody" style="flex:1; min-height:0; overflow:auto; padding:12px 16px;"></div>
          </div>
        </div>

        <!-- Log -->
        <div style="grid-area:log; background:var(--card); border:1px solid var(--border); border-radius:var(--radius-xl); box-shadow:var(--shadow-xs); display:flex; flex-direction:column; min-height:0; min-width:0; overflow:hidden;">
          <div style="flex:none; display:flex; align-items:center; gap:10px; padding:13px 16px 9px;">
            <span style="font-size:11px; text-transform:uppercase; letter-spacing:0.07em; color:var(--muted-foreground);">Activity</span>
            <span id="logBadge" style="font-family:var(--font-mono); font-size:11px; background:var(--secondary); padding:1px 8px; border-radius:999px;">0</span>
          </div>
          <div id="logScroll" style="flex:1; min-height:0; overflow:auto; padding:2px 16px 14px; display:flex; flex-direction:column; gap:4px;"></div>
        </div>

      </div>
    </div>

  </div>
</div>

<script>
'use strict';
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;' }[c]));
const ORANGE = '#D96F35';
const ACCEPTED = { docx:'Word', pdf:'PDF', xlsx:'Excel', xlsm:'Excel', xls:'Excel' };
const PROVIDER_LABELS = { gencore:'enterprise-gateway', azure:'azure-openai', openai:'openai', claude:'anthropic-claude' };
const ENGINE_LABELS = { telerik:'telerik', libreoffice:'conversion-service' };
const ARTIFACTS = [
  { file:'document-schema.json',       api:'schema' },
  { file:'questions.json',             api:'questions' },
  { file:'review-queue.json',          api:'review' },
  { file:'reconciliation-report.json', api:'report' },
];

let defaults = { engine:'telerik', provider:'gencore', model:'', host:'' };
let checkCount = 0, failedCount = 0, proceededWithFailures = false;
let file = null, jobId = null, startedAt = 0, tickId = null, phaseDone = false, currentEs = null;
let legs = {}, qTotal = 0, logCount = 0;
let artifactData = {}, artifactSizes = {}, downloaded = {}, viewing = null;

/* ================= boot: splash -> preflight ================= */
function show(screen) {
  ['preflight','configure','console'].forEach(s => $(s).classList.toggle('hidden', s !== screen));
  $('newFileBtn').classList.toggle('hidden', screen !== 'console');
}
fetch('/api/defaults').then(r => r.json()).then(d => { defaults = d; applyDefaults(); }).catch(() => {});
function applyDefaults() {
  $('pfHost').textContent = 'rfpx serve' + (defaults.host ? ' · ' + defaults.host : '');
  $('pfConfig').textContent = 'engine: ' + (ENGINE_LABELS[defaults.engine] || defaults.engine)
    + ' · provider: ' + (PROVIDER_LABELS[defaults.provider] || defaults.provider);
  if (defaults.engine) $('cfgEngine').value = defaults.engine;
  if (defaults.provider) $('cfgProvider').value = defaults.provider;
  if (defaults.model) $('cfgModel').value = defaults.model;
}
setTimeout(() => { $('splash').style.animation = 'rfpx-splash-out 0.45s var(--ease-out) both'; }, 1750);
setTimeout(() => { $('splash').classList.add('hidden'); show('preflight'); runChecks(); }, 2200);

/* ================= preflight (real /api/checks SSE) ================= */
const PILL_SKINS = {
  running:'color:var(--muted-foreground);background:var(--muted)',
  pass:'color:var(--success);background:var(--success-bg)',
  warn:'color:var(--warning);background:var(--warning-bg)',
  fail:'color:var(--destructive);background:var(--danger-bg)',
  skipped:'color:var(--muted-foreground);background:var(--muted)',
};
const DOT_COLORS = { pass:'var(--success)', warn:'var(--warning)', fail:'var(--destructive)', skipped:'var(--neutral-300)' };

function checkRow(id, name) {
  const row = document.createElement('div');
  row.id = 'chk-' + id;
  row.style.cssText = 'display:flex;align-items:flex-start;gap:12px;padding:12px 20px;border-top:1px solid var(--line-soft);animation:rfpx-row-in 0.25s var(--ease-out) both;';
  row.innerHTML =
    '<span style="width:16px;display:inline-flex;justify-content:center;padding-top:3px;flex:none;"><span class="spinner"></span></span>' +
    '<div style="flex:1;min-width:0;"><div class="nm" style="font-size:13.5px;font-weight:500;"></div>' +
    '<div class="dt" style="font-size:12.5px;line-height:1.5;color:var(--muted-foreground);margin-top:2px;">checking…</div></div>' +
    '<span class="pill" style="' + PILL_SKINS.running + '">running</span>';
  row.querySelector('.nm').textContent = name;
  $('pfRows').appendChild(row);
}
function resolveCheck(d) {
  const row = $('chk-' + d.id); if (!row) return;
  let outcome = d.status;
  if (outcome === 'fail' && /^skipped/i.test(d.detail || '')) outcome = 'skipped';
  row.children[0].innerHTML = '<span style="width:8px;height:8px;margin-top:2px;border-radius:999px;background:' + (DOT_COLORS[outcome] || 'var(--neutral-300)') + ';"></span>';
  row.querySelector('.dt').textContent = d.detail || '';
  const pill = row.querySelector('.pill');
  pill.style.cssText = ''; pill.className = 'pill'; pill.setAttribute('style', PILL_SKINS[outcome] || PILL_SKINS.running);
  pill.classList.add('pill'); pill.textContent = outcome;
  checkCount++;
  if (outcome === 'fail') failedCount++;
}
function runChecks() {
  checkCount = 0; failedCount = 0;
  $('pfRows').innerHTML = ''; $('pfPassed').classList.add('hidden'); $('pfFailed').classList.add('hidden');
  const q = new URLSearchParams({ engine: defaults.engine || 'telerik', provider: defaults.provider || 'gencore', model: defaults.model || '' });
  const es = new EventSource('/api/checks?' + q);
  es.onmessage = (e) => {
    const d = JSON.parse(e.data);
    if (d.kind === 'check-start') checkRow(d.id, d.name);
    else if (d.kind === 'check') resolveCheck(d);
    else if (d.kind === 'checks-done') {
      es.close();
      if (failedCount === 0) {
        $('pfPassed').classList.remove('hidden');
        setTimeout(() => enterConfigure(false), 1000);
      } else {
        $('pfFailedMsg').textContent = (failedCount === 1 ? 'One check failed.' : failedCount + ' checks failed.') + ' Fix the environment and retry, or proceed manually.';
        $('pfFailed').classList.remove('hidden');
      }
    }
  };
  es.onerror = () => { es.close(); $('pfFailed').classList.remove('hidden'); };
}
function retryChecks() { runChecks(); }
function proceedAnyway() { enterConfigure(true); }
function enterConfigure(withFailures) {
  proceededWithFailures = withFailures;
  const chip = $('pfChip');
  if (withFailures) {
    chip.style.color = 'var(--warning)'; chip.style.background = 'var(--warning-bg)'; chip.style.borderColor = '#FDE68A';
    $('pfChipDot').style.background = 'var(--warning)';
    $('pfChipLabel').textContent = 'proceeded with ' + failedCount + ' failed check' + (failedCount === 1 ? '' : 's');
  } else {
    $('pfChipLabel').textContent = checkCount + ' preflight checks passed';
  }
  show('configure');
}

/* ================= configure: file selection ================= */
const dz = $('dropzone');
dz.onclick = () => $('fileInput').click();
dz.ondragover = (e) => { e.preventDefault(); dz.style.borderColor = ORANGE; dz.style.background = 'color-mix(in srgb, ' + ORANGE + ' 7%, white)'; };
dz.ondragleave = () => { dz.style.borderColor = 'var(--neutral-300)'; dz.style.background = 'var(--neutral-50)'; };
dz.ondrop = (e) => { e.preventDefault(); dz.ondragleave(); handleFiles(e.dataTransfer.files); };
$('fileInput').onchange = (e) => { handleFiles(e.target.files); e.target.value = ''; };

// keep the model field sensible when switching provider to/from Claude
$('cfgProvider').onchange = () => {
  const p = $('cfgProvider').value;
  const m = $('cfgModel').value.trim().toLowerCase();
  if (p === 'claude' && !m.startsWith('claude')) $('cfgModel').value = 'claude-sonnet-5';
  else if (p !== 'claude' && m.startsWith('claude')) $('cfgModel').value = 'gpt-4o';
};

function fileErrorMsg(msg) {
  const el = $('fileError');
  if (msg) { el.textContent = msg; el.classList.remove('hidden'); } else el.classList.add('hidden');
}
function handleFiles(list) {
  const f = list && list[0];
  if (!f) { fileErrorMsg('No file received — provide a document.'); return; }
  const m = f.name.match(/\.([^.]+)$/);
  const ext = m ? m[1].toLowerCase() : '';
  const kind = ACCEPTED[ext];
  if (!kind) { file = null; renderFile(); fileErrorMsg('Unsupported type ' + (ext ? '.' + ext : '(no extension)') + ' — accepted: .docx, .pdf, .xlsx, .xlsm, .xls'); return; }
  if (f.size === 0) { file = null; renderFile(); fileErrorMsg('The file is empty (0 bytes) — uploads with no content are rejected.'); return; }
  const size = f.size >= 1048576 ? (f.size / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(f.size / 1024)) + ' KB';
  const grid = kind === 'Excel';
  file = { raw: f, name: f.name, size, kind, pipeline: grid ? 'grid' : 'document',
           legsLabel: grid ? 'grid-first · exact cell bindings' : 'document pipeline · vision + text legs' };
  fileErrorMsg(null); renderFile();
}
function renderFile() {
  $('dzEmpty').classList.toggle('hidden', !!file);
  $('dzFile').classList.toggle('hidden', !file);
  if (file) { $('dzName').textContent = file.name; $('dzMeta').textContent = file.kind + ' · ' + file.size + ' · ' + file.legsLabel; }
  $('startBtn').disabled = !file;
}
function clearFile(e) { e.stopPropagation(); file = null; fileErrorMsg(null); renderFile(); }

/* ================= run ================= */
function cfg() {
  return {
    engine: $('cfgEngine').value, provider: $('cfgProvider').value,
    model: $('cfgModel').value.trim(), strategy: $('cfgStrategy').value,
    granularity: $('cfgGranularity').value,
    resolution: $('cfgResolution').value, concurrency: $('cfgConcurrency').value,
    dpi: Math.round(72 * parseFloat($('cfgResolution').value)),
  };
}
async function startRun() {
  if (!file) return;
  const c = cfg();
  const fd = new FormData();
  fd.append('file', file.raw);
  fd.append('engine', c.engine); fd.append('provider', c.provider); fd.append('model', c.model);
  fd.append('strategy', c.strategy); fd.append('dpi', String(c.dpi)); fd.append('maxparallel', c.concurrency);
  fd.append('granularity', c.granularity);
  const r = await fetch('/api/extract', { method: 'POST', body: fd });
  if (!r.ok) { fileErrorMsg('Upload failed: ' + (await r.text())); return; }
  jobId = (await r.json()).id;

  // reset run state
  legs = {}; qTotal = 0; logCount = 0; artifactData = {}; artifactSizes = {}; downloaded = {}; viewing = null; phaseDone = false;
  $('legList').innerHTML = ''; $('qList').innerHTML = ''; $('logScroll').innerHTML = '';
  $('qEmpty').classList.remove('hidden'); $('fatalBox').classList.add('hidden');
  $('hdrInternal').classList.add('hidden');
  $('progressView').style.display = 'contents'; $('summaryView').style.display = 'none'; $('summaryView').classList.add('hidden');
  closeViewer();
  setQCount(0); setLogCount(0);
  $('hdrFile').textContent = file.name;
  setRunState('running');

  // legs by pipeline + strategy (totals arrive with the first progress events)
  if (file.pipeline === 'grid') legBar('grid', 'Grid — sheets');
  else {
    if (c.strategy !== 'text') legBar('vision', 'Vision — pages');
    if (c.strategy !== 'vision') legBar('text', 'Text — chunks');
  }

  startedAt = Date.now();
  tickId = setInterval(() => { if (!phaseDone) $('hdrElapsed').textContent = ((Date.now() - startedAt) / 1000).toFixed(1) + ' s'; }, 100);
  pushLog('info', 'job ' + jobId + ' accepted — ' + file.name + ' (' + file.size + ')');
  show('console');

  const es = new EventSource('/api/events/' + jobId);
  currentEs = es;
  es.onmessage = (e) => handleEvent(JSON.parse(e.data), es);
  es.onerror = () => es.close();
}
/* start over — abandon the current run (if any) and return to New ingestion with a fresh picker */
function newIngestion() {
  if (currentEs) { try { currentEs.close(); } catch (e) {} currentEs = null; }
  if (tickId) { clearInterval(tickId); tickId = null; }
  phaseDone = true; jobId = null;
  file = null; fileErrorMsg(null); renderFile();
  $('fatalBox').classList.add('hidden');
  show('configure');
}
function setRunState(state) {
  const dot = $('runDot');
  if (state === 'running') { dot.style.background = ORANGE; dot.style.animation = 'rfpx-dot-blink 1.4s var(--ease-out) infinite'; }
  else if (state === 'complete') { dot.style.background = 'var(--success)'; dot.style.animation = 'none'; }
  else { dot.style.background = 'var(--destructive)'; dot.style.animation = 'none'; }
  $('runStatus').textContent = state === 'error' ? 'aborted' : state;
  $('streamingChip').style.display = state === 'running' ? 'inline-flex' : 'none';
}
function setQCount(n) { qTotal = n; $('hdrQCount').textContent = n; $('qBadge').textContent = n; }
function setLogCount(n) { logCount = n; $('logBadge').textContent = n; }

function handleEvent(d, es) {
  if (d.kind === 'stage') {
    const c = cfg();
    const chips = ['job: ' + jobId, 'engine: ' + (ENGINE_LABELS[d.engine] || d.engine),
      'provider: ' + (PROVIDER_LABELS[d.provider] || d.provider), 'model: ' + d.model,
      'strategy: ' + (d.strategy === 'both' ? 'dual-leg' : d.strategy),
      'render: ' + c.resolution + '×', 'concurrency: ' + c.concurrency];
    $('hdrChips').innerHTML = '';
    chips.forEach(t => {
      const s = document.createElement('span');
      s.style.cssText = 'font-family:var(--font-mono);font-size:11px;color:var(--muted-foreground);background:var(--background);border:1px solid var(--border);padding:2px 8px;border-radius:999px;';
      s.textContent = t; $('hdrChips').appendChild(s);
    });
  } else if (d.kind === 'log') {
    const level = /failed after|FAILED/.test(d.message) ? 'error' : /attempt|retrying/.test(d.message) ? 'warn' : 'info';
    pushLog(level, d.message);
  } else if (d.kind === 'progress') {
    updateLeg(d.leg, d.done, d.total);
  } else if (d.kind === 'questions') {
    for (const q of d.items) addQuestion(d.leg, q);
  } else if (d.kind === 'done') {
    es.close(); finishRun(d);
  } else if (d.kind === 'error') {
    es.close(); fatal(d.message);
  }
}

/* legs */
function legBar(id, label) {
  const el = document.createElement('div');
  el.id = 'leg-' + id;
  el.innerHTML =
    '<div style="display:flex;justify-content:space-between;align-items:baseline;gap:8px;">' +
    '<span style="font-size:12.5px;font-weight:500;">' + label + '</span>' +
    '<span class="ct" style="font-family:var(--font-mono);font-size:11.5px;color:var(--muted-foreground);">0 / —</span></div>' +
    '<div style="height:6px;border-radius:999px;background:var(--neutral-100);margin-top:7px;overflow:hidden;">' +
    '<div class="bar" style="height:100%;border-radius:999px;background:' + ORANGE + ';width:0%;transition:width 0.3s var(--ease-out);"></div></div>';
  $('legList').appendChild(el);
  legs[id] = el;
  return el;
}
function updateLeg(id, done, total) {
  const el = legs[id] || legBar(id, id === 'vision' ? 'Vision — pages' : id === 'text' ? 'Text — chunks' : 'Grid — sheets');
  el.querySelector('.ct').textContent = done + ' / ' + (total > 0 ? total : '—');
  if (total > 0) el.querySelector('.bar').style.width = Math.round(100 * done / total) + '%';
}

/* questions */
function questionRow(id, text, meta, statusColor, statusLabel, internal) {
  const row = document.createElement('div');
  row.style.cssText = 'display:flex;gap:12px;padding:10px 16px;border-top:1px solid var(--line-soft);animation:rfpx-row-in 0.25s var(--ease-out) both;' +
    (internal ? 'background:var(--warning-bg);' : '');
  const internalChip = internal
    ? '<span title="Internal-only section — not answered by the responder" style="font-family:var(--font-mono);font-size:10px;color:var(--warning);border:1px solid #FDE68A;border-radius:999px;padding:1px 6px;flex:none;">internal</span>'
    : '';
  row.innerHTML =
    '<span style="font-family:var(--font-mono);font-size:11px;color:var(--neutral-400);flex:none;width:30px;padding-top:2px;">' + id + '</span>' +
    '<div style="flex:1;min-width:0;"><div class="tx" style="font-size:13px;line-height:1.45;"></div>' +
    '<div class="mt" style="font-family:var(--font-mono);font-size:11px;color:var(--muted-foreground);margin-top:3px;"></div></div>' +
    '<span style="display:inline-flex;align-items:center;gap:6px;flex:none;padding-top:2px;">' + internalChip +
    '<span style="width:7px;height:7px;border-radius:999px;background:' + statusColor + ';"></span>' +
    '<span style="font-family:var(--font-mono);font-size:11px;color:var(--muted-foreground);">' + statusLabel + '</span></span>';
  row.querySelector('.tx').textContent = text;
  row.querySelector('.mt').textContent = meta;
  return row;
}

/* ---- expandable final-question rows (click to reveal parts + attributes) ---- */
function kvRow(k, v) {
  if (v == null || v === '') return '';
  return '<div style="display:flex;gap:10px;margin-top:5px;">' +
    '<span style="font-family:var(--font-mono);font-size:10.5px;color:var(--muted-foreground);flex:none;width:96px;">' + k + '</span>' +
    '<span style="font-size:12px;line-height:1.5;color:var(--text-secondary);min-width:0;word-break:break-word;">' + esc(v) + '</span></div>';
}
function retrievalRows(rq) {
  if (!rq) return '';
  const bits = [];
  if (rq.category) bits.push(rq.category);
  if (rq.expected_format) bits.push(rq.expected_format);
  if (rq.units) bits.push(rq.units);
  bits.push(rq.requires_external_input ? 'needs external input' : 'self-contained');
  let s = kvRow('retrieval', bits.join(' · '));
  if (rq.ai_comment) s += kvRow('ai note', rq.ai_comment);
  return s;
}
function questionDetail(q) {
  let h = '';
  if (q.verbatim_source && q.verbatim_source !== q.question_text) h += kvRow('verbatim', q.verbatim_source);
  const attrs = [q.section_path, q.answer_type, q.source,
    q.found_by ? (q.found_by === 'both' ? 'vision+text' : q.found_by + ' only') : '',
    q.confidence ? q.confidence + ' confidence' : '', q.audience].filter(Boolean);
  h += kvRow('attributes', attrs.join(' · '));
  h += retrievalRows(q.retrieval);

  const parts = (q.parts && q.parts.length) ? q.parts : null;
  const subs = (q.sub_questions && q.sub_questions.length) ? q.sub_questions : null;
  if (parts) {
    h += '<div style="margin-top:10px;font-family:var(--font-mono);font-size:10px;text-transform:uppercase;letter-spacing:0.06em;color:var(--muted-foreground);">atomic parts · ' + parts.length + '</div>';
    parts.forEach(p => {
      const pm = [p.part_id, p.answer_type, p.retrieval && p.retrieval.category, p.retrieval && p.retrieval.units].filter(Boolean);
      h += '<div style="border-left:2px solid var(--border);padding:3px 0 3px 10px;margin-top:6px;">' +
        '<div style="font-size:12.5px;line-height:1.45;">' + esc(p.question_text) + '</div>' +
        '<div style="font-family:var(--font-mono);font-size:10.5px;color:var(--muted-foreground);margin-top:2px;">' + pm.join(' · ') + '</div>' +
        (p.retrieval && p.retrieval.ai_comment ? '<div style="font-size:11.5px;color:var(--text-secondary);margin-top:2px;">⚑ ' + esc(p.retrieval.ai_comment) + '</div>' : '') +
        '</div>';
    });
  } else if (subs) {
    h += '<div style="margin-top:10px;font-family:var(--font-mono);font-size:10px;text-transform:uppercase;letter-spacing:0.06em;color:var(--muted-foreground);">sub-questions · ' + subs.length + '</div>';
    subs.forEach((sq, k) => {
      h += '<div style="border-left:2px solid var(--border);padding:3px 0 3px 10px;margin-top:6px;font-size:12.5px;line-height:1.45;">' + (k + 1) + '. ' + esc(sq) + '</div>';
    });
  }
  return h;
}
function appendExpandable(q, id) {
  const bound = q.binding && q.binding.kind === 'cell';
  const agreed = q.found_by === 'both';
  const internal = q.audience === 'internal';
  const rq = q.retrieval;
  const parts = (q.parts && q.parts.length) ? q.parts : null;
  const subs = (q.sub_questions && q.sub_questions.length) ? q.sub_questions : null;
  const nested = parts ? parts.length : (subs ? subs.length : 0);
  const meta = (bound
    ? 'grid · ' + (q.binding.sheet ? q.binding.sheet + '!' + q.binding.address : q.binding.address || '') + ' · ' + (q.answer_type || '')
    : (q.found_by === 'both' ? 'vision+text' : (q.found_by || '') + ' only') + ' · ' + (q.section_path || '') + ' · ' + (q.answer_type || ''))
    + (nested ? ' · ' + nested + (parts ? ' parts' : ' sub-qs') : '')
    + (rq && rq.category ? ' · ' + rq.category : '')
    + (rq && rq.ai_comment ? ' · ⚑ note' : '');

  const head = questionRow(id, q.question_text, meta,
    bound || agreed ? 'var(--success)' : 'var(--warning)', bound ? 'bound' : agreed ? 'agreed' : 'review', internal);
  head.style.animation = 'none';
  head.style.cursor = 'pointer';
  const cv = document.createElement('span');
  cv.textContent = '▸';
  cv.style.cssText = 'font-size:10px;color:var(--neutral-400);flex:none;width:10px;padding-top:3px;transition:transform 0.15s var(--ease-standard);';
  head.insertBefore(cv, head.firstChild);

  const body = document.createElement('div');
  body.className = 'hidden';
  body.style.cssText = 'padding:2px 16px 14px 58px; border-top:1px dashed var(--line-soft); background:var(--neutral-50);';
  body.innerHTML = questionDetail(q);

  head.onclick = () => { const open = body.classList.toggle('hidden') === false; cv.style.transform = open ? 'rotate(90deg)' : 'none'; };

  const wrap = document.createElement('div');
  wrap.appendChild(head); wrap.appendChild(body);
  $('qList').appendChild(wrap);
}
function addQuestion(leg, q) {
  $('qEmpty').classList.add('hidden');
  const n = qTotal + 1;
  const id = 'Q' + String(n).padStart(2, '0');
  const grid = leg === 'grid';
  const bind = q.binding && q.binding.sheet ? q.binding.sheet + '!' + q.binding.address : null;
  const meta = grid ? 'grid · ' + (bind || q.section || '') + ' · ' + (q.type || '')
                    : leg + ' · ' + (q.section || '') + ' · ' + (q.type || '');
  const row = questionRow(id, q.text, meta,
    grid ? 'var(--success)' : 'var(--neutral-300)', grid ? 'bound' : 'found');
  appendWithScroll($('qList'), $('qScroll'), row);
  setQCount(n);
}
function appendWithScroll(list, scroller, el) {
  const nearBottom = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 160;
  list.appendChild(el);
  if (nearBottom) scroller.scrollTop = scroller.scrollHeight;
}

/* log */
const LOG_COLORS = { info:'var(--neutral-700)', warn:'var(--warning)', error:'var(--destructive)' };
function pushLog(level, msg) {
  const row = document.createElement('div');
  row.style.cssText = 'display:flex;gap:10px;font-family:var(--font-mono);font-size:11.5px;line-height:1.5;';
  const t = startedAt ? ((Date.now() - startedAt) / 1000).toFixed(1) : '0.0';
  row.innerHTML = '<span style="color:var(--neutral-400);flex:none;width:42px;text-align:right;">' + t + 's</span>' +
                  '<span class="m" style="color:' + (LOG_COLORS[level] || LOG_COLORS.info) + ';word-break:break-word;min-width:0;"></span>';
  row.querySelector('.m').textContent = msg;
  appendWithScroll($('logScroll'), $('logScroll'), row);
  setLogCount(logCount + 1);
}

/* fatal */
function fatal(message) {
  phaseDone = true;
  if (tickId) { clearInterval(tickId); tickId = null; }
  pushLog('error', 'fatal: ' + message);
  pushLog('error', 'fatal: run aborted — partial results retained in memory');
  $('fatalTitle').textContent = 'Fatal error — run aborted';
  $('fatalDetail').textContent = message;
  $('fatalNote').textContent = 'The interface remains available. Partial questions and the activity log are retained; reconfigure and start again.';
  $('fatalBox').classList.remove('hidden');
  setRunState('error');
}
function backToConfig() { $('fatalBox').classList.add('hidden'); show('configure'); }

/* ================= complete ================= */
async function finishRun(d) {
  phaseDone = true;
  if (tickId) { clearInterval(tickId); tickId = null; }
  const elapsed = (d.duration_ms / 1000).toFixed(1) + ' s';
  $('hdrElapsed').textContent = elapsed;
  pushLog('info', 'done: ' + d.merged + ' questions in ' + elapsed);
  setRunState('complete');

  // fetch the real artifacts (contents drive the viewer, sizes, and the final question list)
  await Promise.all(ARTIFACTS.map(async (a, i) => {
    try {
      const r = await fetch('/api/result/' + jobId + '/' + a.api);
      const text = await r.text();
      artifactData[i] = text;
      const kb = Math.max(1, Math.round(new Blob([text]).size / 1024));
      artifactSizes[i] = kb + ' KB';
    } catch { artifactData[i] = '{}'; artifactSizes[i] = '—'; }
  }));

  // final reconciled question list (statuses: bound / agreed / review; internal rows flagged)
  let internalCount = 0, totalCount = d.merged;
  try {
    const qs = JSON.parse(artifactData[1]);
    totalCount = qs.length;
    internalCount = qs.filter(q => q.audience === 'internal').length;
    $('qList').innerHTML = '';
    $('qEmpty').classList.toggle('hidden', qs.length > 0);
    qs.forEach((q, i) => appendExpandable(q, 'Q' + String(i + 1).padStart(2, '0')));
    setQCount(qs.length);
  } catch { /* keep streamed list */ }

  // header: distinguish applicant-facing from internal-only
  const applicantCount = totalCount - internalCount;
  const hdrInt = $('hdrInternal');
  if (internalCount > 0) { hdrInt.textContent = '+' + internalCount + ' internal'; hdrInt.classList.remove('hidden'); }
  else hdrInt.classList.add('hidden');

  // summary metrics — applicant vs internal, plus the printed·atomic count split by granularity
  const metrics = [
    { value: String(applicantCount), label: 'applicant', title: 'Applicant-facing entries the responder fills' },
    { value: String(internalCount),  label: 'internal',  title: 'Internal-only sections (e.g. BGFML) — not answered by the responder' },
    { value: (d.granularity || '') + ' ' + (d.printed || totalCount) + '·' + (d.atomic || totalCount), label: 'printed·atomic',
      title: 'Granularity: ' + (d.granularity || '') + ' — ' + (d.printed || totalCount) + ' printed questions, ' + (d.atomic || totalCount) + ' atomic asks' },
    { value: String(d.agreed),       label: 'agreed',    title: 'Agreed — found by multiple legs' },
    { value: String(d.review),       label: 'review',    title: 'Needs review — single-source' },
    { value: elapsed,                label: 'elapsed',   title: 'Elapsed time' },
  ];
  $('metricRow').innerHTML = '';
  metrics.forEach(m => {
    const el = document.createElement('div');
    el.style.cssText = 'display:flex;align-items:baseline;gap:6px;min-width:0;';
    el.title = m.title;
    el.innerHTML = '<span style="font-size:15px;font-weight:600;letter-spacing:-0.02em;">' + m.value + '</span>' +
                   '<span style="font-size:11px;color:var(--muted-foreground);white-space:nowrap;">' + m.label + '</span>';
    $('metricRow').appendChild(el);
  });

  // warnings
  const wb = $('warnBox');
  wb.innerHTML = '';
  if (d.warnings && d.warnings.length) {
    wb.classList.remove('hidden');
    d.warnings.forEach(w => {
      const el = document.createElement('div');
      el.style.cssText = 'font-size:11.5px;line-height:1.5;color:var(--text-secondary);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;';
      el.title = w; el.textContent = w;
      wb.appendChild(el);
    });
  } else wb.classList.add('hidden');

  renderArtifacts();
  $('savePath').value = d.save_dir || '';
  $('savedChip').classList.add('hidden'); $('saveError').classList.add('hidden');

  $('progressView').style.display = 'none';
  $('summaryView').classList.remove('hidden');
  $('summaryView').style.display = 'flex';
}

const EYE_SVG = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"></path><circle cx="12" cy="12" r="3"></circle></svg>';
const DL_SVG = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><path d="m7 10 5 5 5-5"></path><path d="M12 15V3"></path></svg>';
const CHECK_SVG = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"></path></svg>';

function renderArtifacts() {
  const grid = $('artifactGrid');
  grid.innerHTML = '';
  ARTIFACTS.forEach((a, i) => {
    const active = viewing === i;
    const row = document.createElement('div');
    row.title = a.file + ' · ' + (artifactSizes[i] || '');
    row.style.cssText = 'display:flex;align-items:center;gap:6px;border-radius:var(--radius-md);padding:2px 3px 2px 8px;min-width:0;cursor:pointer;transition:var(--transition-control);' +
      'border:1px solid ' + (active ? ORANGE : 'var(--border)') + ';' +
      'background:' + (active ? 'color-mix(in srgb, ' + ORANGE + ' 6%, white)' : 'var(--background)') + ';';
    row.innerHTML =
      '<span style="font-family:var(--font-mono);font-size:11px;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + a.file + '</span>' +
      '<button class="icon-btn vw" title="View in JSON viewer" style="color:' + (active ? ORANGE : 'var(--muted-foreground)') + ';">' + EYE_SVG + '</button>' +
      '<button class="icon-btn dl" title="' + (downloaded[i] ? 'Downloaded' : 'Download') + '" style="color:' + (downloaded[i] ? 'var(--success)' : 'var(--muted-foreground)') + ';">' + (downloaded[i] ? CHECK_SVG : DL_SVG) + '</button>';
    row.onclick = () => openViewer(i);
    row.querySelector('.dl').onclick = (e) => { e.stopPropagation(); downloadArtifact(i); };
    grid.appendChild(row);
  });
}
function downloadArtifact(i) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([artifactData[i] || '{}'], { type: 'application/json' }));
  a.download = ARTIFACTS[i].file;
  a.click();
  URL.revokeObjectURL(a.href);
  downloaded[i] = true; renderArtifacts();
}
function openViewer(i) {
  viewing = i;
  $('viewerFile').textContent = ARTIFACTS[i].file;
  $('viewerSize').textContent = artifactSizes[i] || '';
  const pre = document.createElement('pre');
  pre.style.cssText = 'margin:0;font-family:var(--font-mono);font-size:11.5px;line-height:1.65;white-space:pre;color:var(--neutral-700);';
  let json = artifactData[i] || '{}';
  try { json = JSON.stringify(JSON.parse(json), null, 2); } catch {}
  highlightJson(json).forEach(n => pre.appendChild(n));
  $('viewerBody').innerHTML = ''; $('viewerBody').appendChild(pre);
  $('questionsView').style.display = 'none';
  $('viewerView').classList.remove('hidden'); $('viewerView').style.display = 'flex';
  renderArtifacts();
}
function closeViewer() {
  viewing = null;
  $('viewerView').style.display = 'none'; $('viewerView').classList.add('hidden');
  $('questionsView').style.display = 'contents';
  if ($('artifactGrid').children.length) renderArtifacts();
}
function highlightJson(json) {
  const out = [];
  const re = /("(?:\\.|[^"\\])*"(\s*:)?|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?)/g;
  let last = 0, m;
  while ((m = re.exec(json))) {
    if (m.index > last) out.push(document.createTextNode(json.slice(last, m.index)));
    const tok = m[0];
    const span = document.createElement('span');
    if (tok[0] === '"') span.style.color = m[2] ? ORANGE : 'var(--neutral-600)';
    else if (/true|false|null/.test(tok)) span.style.color = 'var(--neutral-500)';
    else span.style.color = 'var(--foreground)';
    span.textContent = tok;
    out.push(span);
    last = re.lastIndex;
  }
  out.push(document.createTextNode(json.slice(last)));
  return out;
}

/* save */
async function saveAll() {
  const p = $('savePath').value.trim();
  const err = $('saveError'), chip = $('savedChip');
  if (!p) { err.textContent = 'Destination path is required.'; err.classList.remove('hidden'); chip.classList.add('hidden'); return; }
  const btn = $('saveBtn');
  btn.disabled = true; btn.textContent = 'Saving…'; err.classList.add('hidden'); chip.classList.add('hidden');
  try {
    const r = await fetch('/api/save/' + jobId, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ dir: p }) });
    const d = await r.json().catch(() => ({}));
    if (r.ok) { $('savedTo').textContent = 'written to ' + d.dir; chip.title = '4 artifacts written to ' + d.dir; chip.classList.remove('hidden'); }
    else { err.textContent = d.error || r.statusText; err.classList.remove('hidden'); }
  } catch (ex) { err.textContent = String(ex); err.classList.remove('hidden'); }
  btn.disabled = false; btn.textContent = 'Save';
}
</script>
</body>
</html>
""";
}
