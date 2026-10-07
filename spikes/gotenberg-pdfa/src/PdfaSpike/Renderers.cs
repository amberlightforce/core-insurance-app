using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfaSpike;

public sealed record RenderInput(string IndexHtml, string? HeaderHtml, string? FooterHtml, IReadOnlyDictionary<string, byte[]> Assets);

public sealed record RenderOptions(
    bool Tagged = true,
    bool Outline = false,
    string? PdfA = null,          // Gotenberg only: "PDF/A-1b" | "PDF/A-2b" | "PDF/A-3b"
    bool PdfUa = false,           // Gotenberg only
    string? MetadataJson = null,  // Gotenberg only (ExifTool)
    (string Name, byte[] Bytes)? Embed = null); // Gotenberg only (8.31+ "embeds")

public sealed record RenderResult(byte[] Pdf, TimeSpan Elapsed, long PeakMemoryBytes, string Engine);

public interface IRenderer
{
    Task<RenderResult> RenderAsync(RenderInput input, RenderOptions opt, CancellationToken ct = default);
}

/// <summary>
/// Gotenberg 8 Chromium route: POST /forms/chromium/convert/html (multipart).
/// Same Chromium Page.printToPDF call as <see cref="ChromeCdpRenderer"/>, plus Gotenberg's
/// optional post-processing engines (LibreOffice for pdfa, ExifTool for metadata, pdfcpu/qpdf).
/// </summary>
public sealed class GotenbergRenderer(Uri baseUrl) : IRenderer
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public async Task<RenderResult> RenderAsync(RenderInput input, RenderOptions opt, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        void File(string name, byte[] bytes, string field = "files")
        {
            var c = new ByteArrayContent(bytes);
            c.Headers.ContentType = new MediaTypeHeaderValue(name.EndsWith(".html") ? "text/html" : "application/octet-stream");
            form.Add(c, field, name);
        }
        File("index.html", Encoding.UTF8.GetBytes(input.IndexHtml));
        if (input.HeaderHtml is not null) File("header.html", Encoding.UTF8.GetBytes(input.HeaderHtml));
        if (input.FooterHtml is not null) File("footer.html", Encoding.UTF8.GetBytes(input.FooterHtml));
        foreach (var (n, b) in input.Assets) File(n, b);
        void F(string k, string v) => form.Add(new StringContent(v), k);
        F("paperWidth", "8.27"); F("paperHeight", "11.7");
        F("preferCssPageSize", "true");
        F("printBackground", "true");
        F("failOnConsoleExceptions", "true");
        F("failOnResourceLoadingFailed", "true");
        F("generateTaggedPdf", opt.Tagged ? "true" : "false");
        F("generateDocumentOutline", opt.Outline ? "true" : "false");
        F("waitForExpression", "document.fonts.status === 'loaded'");
        if (opt.PdfA is not null) F("pdfa", opt.PdfA);
        if (opt.PdfUa) F("pdfua", "true");
        if (opt.MetadataJson is not null) F("metadata", opt.MetadataJson);
        if (opt.Embed is { } e)
        {
            File(e.Name, e.Bytes, "embeds");
            F("embedsMetadata", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [e.Name] = new { mimeType = "application/json", relationship = "Data" },
            }));
        }
        var sw = Stopwatch.StartNew();
        using var resp = await Http.PostAsync(new Uri(baseUrl, "/forms/chromium/convert/html"), form, ct);
        var body = await resp.Content.ReadAsByteArrayAsync(ct);
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gotenberg {(int)resp.StatusCode}: {Encoding.UTF8.GetString(body)}");
        return new RenderResult(body, sw.Elapsed, -1, "gotenberg");
    }
}

/// <summary>
/// Drives a local headless Chromium over the DevTools protocol with exactly the
/// Page.printToPDF parameters Gotenberg's Chromium module uses. Used because the
/// spike host had no working Docker daemon; also isolates "what Chromium itself writes".
/// No NuGet packages: System.Net.WebSockets + System.Text.Json.
/// </summary>
public sealed class ChromeCdpRenderer(string chromePath) : IRenderer
{
    public async Task<RenderResult> RenderAsync(RenderInput input, RenderOptions opt, CancellationToken ct = default)
    {
        var work = Directory.CreateTempSubdirectory("pdfa-spike-");
        var profile = Directory.CreateDirectory(Path.Combine(work.FullName, "profile"));
        var site = Directory.CreateDirectory(Path.Combine(work.FullName, "site"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(site.FullName, "index.html"), input.IndexHtml, ct);
        foreach (var (n, b) in input.Assets) await System.IO.File.WriteAllBytesAsync(Path.Combine(site.FullName, n), b, ct);

        var psi = new ProcessStartInfo(chromePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[]
                 {
                     "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--disable-background-networking", "--disable-sync",
                     "--font-render-hinting=none", "--remote-debugging-port=0",
                     "--allow-file-access-from-files",
                     $"--user-data-dir={profile.FullName}", "about:blank",
                 }) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        _ = proc.StandardOutput.ReadToEndAsync(ct);
        _ = proc.StandardError.ReadToEndAsync(ct);
        var portFile = Path.Combine(profile.FullName, "DevToolsActivePort");
        string? wsPath = null; int port = 0;
        for (int i = 0; i < 200 && wsPath is null; i++)
        {
            if (System.IO.File.Exists(portFile))
            {
                try
                {
                    var lines = await System.IO.File.ReadAllLinesAsync(portFile, ct);
                    if (lines.Length >= 2) { port = int.Parse(lines[0]); wsPath = lines[1]; }
                }
                catch (IOException) { }
            }
            if (wsPath is null) await Task.Delay(50, ct);
        }
        if (wsPath is null) throw new InvalidOperationException("Chrome did not expose DevToolsActivePort");

        long peak = 0;
        var pids = new HashSet<int> { proc.Id };
        using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cdp = new Cdp(new Uri($"ws://127.0.0.1:{port}{wsPath}"));
        await cdp.ConnectAsync(ct);
        var sampler = Task.Run(async () =>
        {
            while (!sampleCts.IsCancellationRequested)
            {
                try
                {
                    var info = await cdp.SendAsync("SystemInfo.getProcessInfo", new JsonObject(), null, sampleCts.Token);
                    foreach (var p in info["processInfo"]!.AsArray()) pids.Add((int)p!["id"]!.GetValue<double>());
                }
                catch { /* ignore */ }
                long sum = 0;
                foreach (var pid in pids.ToArray())
                {
                    try { using var p = Process.GetProcessById(pid); sum += p.WorkingSet64; } catch { }
                }
                peak = Math.Max(peak, sum);
                try { await Task.Delay(100, sampleCts.Token); } catch { }
            }
        });

        try
        {
            var target = await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null, ct);
            var targetId = target["targetId"]!.GetValue<string>();
            var att = await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null, ct);
            var sid = att["sessionId"]!.GetValue<string>();
            await cdp.SendAsync("Page.enable", new JsonObject(), sid, ct);
            var loaded = cdp.WaitForEvent("Page.loadEventFired", sid);
            var url = new Uri(Path.Combine(site.FullName, "index.html")).AbsoluteUri;
            await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = url }, sid, ct);
            await loaded.WaitAsync(TimeSpan.FromMinutes(5), ct);
            await cdp.SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = "document.fonts.ready.then(() => document.fonts.status)",
                ["awaitPromise"] = true,
            }, sid, ct);
            // Same parameter set as Gotenberg 8 (pkg/modules/chromium/tasks.go printToPdfActionFunc)
            var pdf = await cdp.SendAsync("Page.printToPDF", new JsonObject
            {
                ["landscape"] = false,
                ["printBackground"] = true,
                ["scale"] = 1.0,
                ["paperWidth"] = 8.27,
                ["paperHeight"] = 11.7,
                ["marginTop"] = 0.39, ["marginBottom"] = 0.39, ["marginLeft"] = 0.39, ["marginRight"] = 0.39,
                ["preferCSSPageSize"] = true,
                ["displayHeaderFooter"] = input.HeaderHtml is not null || input.FooterHtml is not null,
                ["headerTemplate"] = input.HeaderHtml ?? "<span></span>",
                ["footerTemplate"] = input.FooterHtml ?? "<span></span>",
                ["generateTaggedPDF"] = opt.Tagged,
                ["generateDocumentOutline"] = opt.Outline,
                ["transferMode"] = "ReturnAsStream",
            }, sid, ct, TimeSpan.FromMinutes(15));
            var stream = pdf["stream"]!.GetValue<string>();
            using var ms = new MemoryStream();
            while (true)
            {
                var chunk = await cdp.SendAsync("IO.read", new JsonObject { ["handle"] = stream, ["size"] = 4 * 1024 * 1024 }, sid, ct);
                var data = chunk["data"]!.GetValue<string>();
                var bytes = chunk["base64Encoded"]?.GetValue<bool>() == true ? Convert.FromBase64String(data) : Encoding.Latin1.GetBytes(data);
                ms.Write(bytes);
                if (chunk["eof"]!.GetValue<bool>()) break;
            }
            await cdp.SendAsync("IO.close", new JsonObject { ["handle"] = stream }, sid, ct);
            sw.Stop();
            sampleCts.Cancel();
            await sampler;
            try { await cdp.SendAsync("Browser.close", new JsonObject(), null, ct); } catch { }
            return new RenderResult(ms.ToArray(), sw.Elapsed, peak, "chromium-cdp");
        }
        finally
        {
            sampleCts.Cancel();
            cdp.Dispose();
            if (!proc.WaitForExit(5000)) try { proc.Kill(true); } catch { }
            try { work.Delete(true); } catch { }
        }
    }

    public static string? BrowserVersion(string chromePath)
        => FileVersionInfo.GetVersionInfo(chromePath).ProductVersion;
}

/// <summary>Tiny CDP client (flattened sessions).</summary>
sealed class Cdp(Uri uri) : IDisposable
{
    readonly ClientWebSocket _ws = new();
    int _id;
    readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    readonly List<(string Method, string? Session, TaskCompletionSource<JsonNode> Tcs)> _waiters = new();
    readonly SemaphoreSlim _send = new(1, 1);
    Task? _pump;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await _ws.ConnectAsync(uri, ct);
        _pump = Task.Run(Pump);
    }

    public Task<JsonNode> WaitForEvent(string method, string? session)
    {
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters) _waiters.Add((method, session, tcs));
        return tcs.Task;
    }

    public async Task<JsonNode> SendAsync(string method, JsonObject @params, string? session, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = @params };
        if (session is not null) msg["sessionId"] = session;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _send.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
        return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(2), ct);
    }

    async Task Pump()
    {
        var buf = new byte[1 << 20];
        var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var node = JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length))!;
                ms.SetLength(0);
                if (node["id"] is { } idNode)
                {
                    var id = idNode.GetValue<int>();
                    TaskCompletionSource<JsonNode>? tcs;
                    lock (_pending) { _pending.Remove(id, out tcs); }
                    if (tcs is null) continue;
                    if (node["error"] is { } err) tcs.SetException(new InvalidOperationException("CDP error: " + err.ToJsonString()));
                    else tcs.SetResult(node["result"] ?? new JsonObject());
                }
                else if (node["method"] is { } m)
                {
                    var method = m.GetValue<string>();
                    var session = node["sessionId"]?.GetValue<string>();
                    lock (_waiters)
                    {
                        for (int i = _waiters.Count - 1; i >= 0; i--)
                        {
                            var w = _waiters[i];
                            if (w.Method == method && (w.Session is null || w.Session == session))
                            {
                                w.Tcs.TrySetResult(node["params"] ?? new JsonObject());
                                _waiters.RemoveAt(i);
                            }
                        }
                    }
                }
            }
        }
        catch { /* socket closed */ }
        lock (_pending) foreach (var t in _pending.Values) t.TrySetException(new IOException("CDP closed"));
    }

    public void Dispose()
    {
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
    }
}
