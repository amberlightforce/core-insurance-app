using System.Text;
using PdfaSpike;

var argsMap = args.Select(a => a.Split('=', 2)).ToDictionary(a => a[0].TrimStart('-'), a => a.Length > 1 ? a[1] : "true");
string Arg(string k, string d) => argsMap.TryGetValue(k, out var v) ? v : d;

var outDir = Directory.CreateDirectory(Arg("out", "out")).FullName;
var fontsDir = Arg("fonts", "fonts");
var mode = Arg("mode", "probe");

IRenderer renderer = Arg("renderer", "chrome") switch
{
    "gotenberg" => new GotenbergRenderer(new Uri(Arg("gotenberg", "http://localhost:3000"))),
    _ => new ChromeCdpRenderer(Arg("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")),
};

var assets = new Dictionary<string, byte[]>
{
    ["NotoSans-Regular.ttf"] = File.ReadAllBytes(Path.Combine(fontsDir, "NotoSans-Regular.ttf")),
    ["NotoSans-Bold.ttf"] = File.ReadAllBytes(Path.Combine(fontsDir, "NotoSans-Bold.ttf")),
};
var fontUri = "data:font/ttf;base64," + Convert.ToBase64String(assets["NotoSans-Regular.ttf"]);

var templates = Arg("hf", "margin-boxes"); // "margin-boxes" (CSS @page) | "templates" (Chromium header/footer templates)
RenderInput Input(SchedulePayload p) => templates == "templates"
    ? new(Composer.Index(p, marginBoxes: false), Composer.Header(fontUri), Composer.Footer(fontUri), assets)
    : new(Composer.Index(p, marginBoxes: true), null, null, assets);

switch (mode)
{
    case "probe":
    {
        var p = Composer.SamplePayload(int.Parse(Arg("rows", "120")));
        var inp = Input(p);
        // Chunked rendering experiment: can a chunk continue page numbering ("Σελίδα 101 από 601")?
        if (argsMap.TryGetValue("pageCss", out var pageCss))
            inp = inp with { IndexHtml = inp.IndexHtml.Replace("</style>", pageCss + "\n</style>") };
        var r = await renderer.RenderAsync(inp, new RenderOptions(Tagged: true));
        var path = Path.Combine(outDir, Arg("name", "probe.pdf"));
        File.WriteAllBytes(path, r.Pdf);
        Console.WriteLine($"{r.Engine} {r.Pdf.Length} bytes in {r.Elapsed.TotalMilliseconds:0} ms peak {r.PeakMemoryBytes / 1048576.0:0} MiB -> {path}");
        var (pdfa, rep) = PdfaPostProcessor.Process(r.Pdf, Experiments.Meta(p));
        File.WriteAllBytes(Path.ChangeExtension(path, ".pdfa.pdf"), pdfa);
        Console.WriteLine($"post-processed: {pdfa.Length} bytes, {rep}");
        break;
    }
    default:
        await Experiments.RunAll(renderer, Input, outDir, fontsDir, argsMap);
        break;
}
