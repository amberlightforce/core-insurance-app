using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;

namespace PdfaSpike;

public static class Experiments
{
    public const string EngineVersion = "CoreIns DOC engine 0.1-spike (Chromium 154 / Skia m154)";

    public static ArchiveMetadata Meta(SchedulePayload p) => new(
        DocumentNumber: p.DocumentNumber,
        DocumentType: "DT-POLICY-SCHEDULE",
        TemplateVersion: "TPL-MOTOR-SCHEDULE v7",
        Language: "el-GR",
        Title: "Πίνακας Ασφάλισης Οχήματος",
        ReferenceTime: DateTimeOffset.Parse(p.ReferenceTimeUtc),
        EngineVersion: EngineVersion,
        Payload: Composer.CanonicalJson(p));

    static StreamWriter _log = null!;
    static void Log(string s) { Console.WriteLine(s); _log.WriteLine(s); _log.Flush(); }
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    public static async Task RunAll(IRenderer r, Func<SchedulePayload, RenderInput> input, string outDir, string fontsDir, Dictionary<string, string> args)
    {
        string Arg(string k, string d) => args.TryGetValue(k, out var v) ? v : d;
        var which = Arg("mode", "all");
        _log = new StreamWriter(Path.Combine(outDir, $"results-{which}{(args.TryGetValue("rows", out var rw) ? "-" + rw : "")}{(args.TryGetValue("tagged", out var tg) ? "-tagged-" + tg : "")}-{(r is GotenbergRenderer ? "gotenberg" : "chromium")}.txt"), false, new UTF8Encoding(false));
        Log($"# PdfaSpike results  mode={which} renderer={r.GetType().Name} os={Environment.OSVersion} dotnet={Environment.Version}");
        if (r is ChromeCdpRenderer) Log($"chromium={ChromeCdpRenderer.BrowserVersion(Arg("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"))}");

        var faces = new[]
        {
            new FontCoverage("NotoSans-Regular", File.ReadAllBytes(Path.Combine(fontsDir, "NotoSans-Regular.ttf"))),
            new FontCoverage("NotoSans-Bold", File.ReadAllBytes(Path.Combine(fontsDir, "NotoSans-Bold.ttf"))),
        };
        foreach (var f in Directory.GetFiles(fontsDir, "*.ttf"))
            Log($"font {Path.GetFileName(f)} sha256={Sha(File.ReadAllBytes(f))}");

        if (which is "all" or "determinism") await Determinism(r, input, outDir, faces, int.Parse(Arg("runs", "3")));
        if (which is "all" or "glyph") await Glyph(r, input, outDir, faces);
        if (which is "large") await Large(r, input, outDir, int.Parse(Arg("rows", "16000")), Arg("tagged", "true") == "true");
        if (which is "gotenberg-native") await GotenbergNative(r, input, outDir);
        _log.Dispose();
    }

    // ---------------------------------------------------------------- REQ-DOC-160 / 157 / 291 / 150
    static async Task Determinism(IRenderer r, Func<SchedulePayload, RenderInput> input, string outDir, FontCoverage[] faces, int runs)
    {
        Log("\n## Determinism + PDF/A-3a post-processing");
        var p = Composer.SamplePayload(120);
        var pre = GlyphCheck.PreCheck(input(p).IndexHtml, faces);
        Log($"glyph pre-check (sample schedule incl. polytonic line): {(pre.Count == 0 ? "OK, all code points covered" : string.Join("; ", pre))}");
        byte[]? firstRaw = null;
        var rawHashes = new List<string>(); var ppHashes = new List<string>();
        for (int i = 1; i <= runs; i++)
        {
            var res = await r.RenderAsync(input(p), new RenderOptions(Tagged: true));
            var (pdfa, rep) = PdfaPostProcessor.Process(res.Pdf, Meta(p));
            File.WriteAllBytes(Path.Combine(outDir, $"det-run{i}.raw.pdf"), res.Pdf);
            File.WriteAllBytes(Path.Combine(outDir, $"det-run{i}.pdfa.pdf"), pdfa);
            rawHashes.Add(Sha(res.Pdf)); ppHashes.Add(Sha(pdfa));
            Log($"run {i}: render {res.Elapsed.TotalMilliseconds:0} ms, raw {res.Pdf.Length} B sha256={Sha(res.Pdf)} | post-processed {pdfa.Length} B sha256={Sha(pdfa)} | {rep}");
            if (firstRaw is null) firstRaw = res.Pdf;
            else if (i == 2) Log("raw diff run1 vs run2: " + DiffSummary(firstRaw, res.Pdf));
            if (i == 1)
            {
                Verify(pdfa, p);
                using var cert = PadesSeal.TestSealCertificate();
                var sealedPdf = PadesSeal.Seal(pdfa, cert, DateTimeOffset.UtcNow);
                File.WriteAllBytes(Path.Combine(outDir, "det-run1.sealed.pdf"), sealedPdf);
                var (ok, detail) = PadesSeal.Verify(sealedPdf);
                Log($"seal (PAdES B-B, incremental update): {sealedPdf.Length} B, valid={ok}; {detail}");
                Log($"pre-seal revision is a byte-exact prefix of the sealed file: {sealedPdf.AsSpan(0, pdfa.Length).SequenceEqual(pdfa)} (sha256 of prefix={Sha(sealedPdf[..pdfa.Length])})");
            }
        }
        Log($"raw identical across runs: {rawHashes.Distinct().Count() == 1}; post-processed identical across runs: {ppHashes.Distinct().Count() == 1}");
    }

    static string DiffSummary(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return $"lengths differ ({a.Length} vs {b.Length})";
        var s1 = Encoding.Latin1.GetString(a);
        int diffs = 0; var kinds = new SortedDictionary<string, int>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            diffs++;
            var ctx = s1.Substring(Math.Max(0, i - 40), Math.Min(40, i));
            var kind = ctx.Contains("Date (D:") ? "Info /CreationDate,/ModDate"
                : ctx.Contains("(node") ? "StructElem /ID + IDTree strings (DOM node ids)"
                : "other";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        return $"{diffs} differing bytes: " + string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value}"));
    }

    static void Verify(byte[] pdfa, SchedulePayload p)
    {
        using var doc = PdfDocument.Open(pdfa);
        var expected = Sha(Composer.CanonicalJson(p));
        if (doc.Advanced.TryGetEmbeddedFiles(out var files))
            foreach (var f in files)
            {
                var b = f.Bytes.ToArray();
                Log($"embedded file '{f.Name}' {b.Length} B sha256={Sha(b)} equals payload hash: {Sha(b) == expected}");
            }
        else Log("NO embedded files found");
        var text = string.Concat(doc.GetPages().Select(pg => pg.Text)).Normalize(NormalizationForm.FormC);
        string Squash(string s) => s.Replace(" ", "").Replace("\u00A0", "");
        foreach (var probe in new[] { "ΑΣΦΑΛΙΣΤΗΡΙΟ ΟΧΗΜΑΤΟΣ", "«ΑΥΤ-2026-0001234»", "Ἡ ἀσφάλισις ἰσχύει", "«ζημιάς»", "Ευρωπαϊκή", Composer.Eur(p.Total), "Σελίδα 2 από" })
            Log($"text round-trip (NFC, ToUnicode) contains \"{probe}\": {Squash(text).Contains(Squash(probe.Normalize(NormalizationForm.FormC)))}");
        Log($"pages={doc.NumberOfPages} version={doc.Version} xmp={(doc.TryGetXmpMetadata(out var xmp) ? xmp.GetXDocument().ToString().Contains("<pdfaid:part>3</pdfaid:part>") ? "pdfaid:part=3" : "present" : "MISSING")}");
    }

    // ---------------------------------------------------------------- REQ-DOC-149
    static async Task Glyph(IRenderer r, Func<SchedulePayload, RenderInput> input, string outDir, FontCoverage[] faces)
    {
        Log("\n## Missing-glyph detection");
        var declared = new HashSet<string> { "NotoSans" };
        var cases = new (string Name, string Extra)[]
        {
            ("ok-polytonic", "ᾼ ᾅ ῷ Ἀθῆναι — polytonic U+1F00 ἀ is in Noto Sans"),
            ("cjk", "Ονοματεπώνυμο: 保险 (CJK not in declared font set)"),
            ("emoji", "Σχόλιο πελάτη 🚗 (emoji)"),
            ("coptic", "Ⲁⲃⲅ Coptic U+2C80"),
            ("pua", "Private use \U0010FFFD and unassigned \U0001FAFF"),
        };
        foreach (var (name, extra) in cases)
        {
            var p = Composer.SamplePayload(10, extra);
            var html = input(p).IndexHtml;
            var pre = GlyphCheck.PreCheck(html, faces);
            Log($"[{name}] pre-check: {(pre.Count == 0 ? "PASS" : "DOC-ERR-GLYPH-MISSING " + string.Join("; ", pre))}");
            var res = await r.RenderAsync(input(p), new RenderOptions(Tagged: true));
            File.WriteAllBytes(Path.Combine(outDir, $"glyph-{name}.raw.pdf"), res.Pdf);
            var post = GlyphCheck.PostCheck(res.Pdf, declared);
            Log($"[{name}] post-check embedded fonts: " + string.Join(", ", post.Select(f => $"{f.BaseFont}{(f.Declared ? "" : " **UNDECLARED FALLBACK**")} glyphs={f.Glyphs}{(f.NotdefGlyphs > 0 ? $" notdef/unmapped={f.NotdefGlyphs}" : "")}")));
            try
            {
                var (pdfa, _) = PdfaPostProcessor.Process(res.Pdf, Meta(p));
                File.WriteAllBytes(Path.Combine(outDir, $"glyph-{name}.pdfa.pdf"), pdfa);
            }
            catch (Exception e) { Log($"[{name}] post-process failed: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- REQ-DOC-170 / NFR-DOC-002
    static async Task Large(IRenderer r, Func<SchedulePayload, RenderInput> input, string outDir, int rows, bool tagged)
    {
        Log($"\n## Large document ({rows} cover rows)");
        var p = Composer.SamplePayload(rows);
        var sw = Stopwatch.StartNew();
        var inp = input(p);
        Log($"compose HTML: {sw.ElapsedMilliseconds} ms, {Encoding.UTF8.GetByteCount(inp.IndexHtml) / 1024} KiB");
        var res = await r.RenderAsync(inp, new RenderOptions(Tagged: tagged));
        Log($"render: {res.Elapsed.TotalSeconds:0.0} s, {res.Pdf.Length / 1048576.0:0.0} MiB, renderer peak working set {(res.PeakMemoryBytes > 0 ? $"{res.PeakMemoryBytes / 1048576.0:0} MiB" : "n/a (measure with docker stats)")}");
        File.WriteAllBytes(Path.Combine(outDir, $"large-{rows}{(tagged ? "" : "-untagged")}.raw.pdf"), res.Pdf);
        if (!tagged) return;
        var before = Process.GetCurrentProcess().PeakWorkingSet64;
        sw.Restart();
        var (pdfa, rep) = PdfaPostProcessor.Process(res.Pdf, Meta(p));
        Log($"post-process: {sw.Elapsed.TotalSeconds:0.0} s, {pdfa.Length / 1048576.0:0.0} MiB, {rep}, .NET process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:0} MiB (before post-process {before / 1048576.0:0} MiB)");
        File.WriteAllBytes(Path.Combine(outDir, $"large-{rows}.pdfa.pdf"), pdfa);
        using var doc = PdfDocument.Open(pdfa);
        Log($"pages={doc.NumberOfPages}");
    }

    // ---------------------------------------------------------------- Gotenberg's own PDF/A + PDF/UA + embeds
    static async Task GotenbergNative(IRenderer r, Func<SchedulePayload, RenderInput> input, string outDir)
    {
        Log("\n## Gotenberg-native options");
        var p = Composer.SamplePayload(120);
        var payload = Composer.CanonicalJson(p);
        var variants = new (string Name, RenderOptions Opt)[]
        {
            ("tagged", new RenderOptions(Tagged: true)),
            ("pdfa3b", new RenderOptions(Tagged: true, PdfA: "PDF/A-3b")),
            ("pdfa3b-ua", new RenderOptions(Tagged: true, PdfA: "PDF/A-3b", PdfUa: true)),
            ("pdfa3b-embed-meta", new RenderOptions(Tagged: true, PdfA: "PDF/A-3b",
                MetadataJson: "{\"Title\":\"Πίνακας Ασφάλισης Οχήματος\",\"CreationDate\":\"2026-10-01T09:30:00Z\",\"ModDate\":\"2026-10-01T09:30:00Z\"}",
                Embed: ("payload.json", payload))),
        };
        foreach (var (name, opt) in variants)
        {
            for (int run = 1; run <= 2; run++)
            {
                try
                {
                    var res = await r.RenderAsync(input(p), opt);
                    File.WriteAllBytes(Path.Combine(outDir, $"gtb-{name}-run{run}.pdf"), res.Pdf);
                    Log($"[{name}] run{run} {res.Elapsed.TotalMilliseconds:0} ms {res.Pdf.Length} B sha256={Sha(res.Pdf)}");
                    if (run == 1)
                    {
                        using var doc = PdfDocument.Open(res.Pdf);
                        Log($"[{name}] producer={doc.Information.Producer} creator={doc.Information.Creator} pages={doc.NumberOfPages} fonts={string.Join(",", GlyphCheck.PostCheck(res.Pdf, new HashSet<string> { "NotoSans" }).Select(f => f.BaseFont))}");
                    }
                }
                catch (Exception e) { Log($"[{name}] FAILED: {e.Message}"); break; }
            }
        }
    }
}
