using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreIns.Modules.Documents.Rendering;

/// <summary>A rendered PDF and whether it is PDF/A (BO-DOC-07).</summary>
internal sealed record PdfRenderResult(byte[] Bytes, bool PdfA, string Renderer);

/// <summary>Turns a self-contained HTML letter into PDF bytes. The renderer is behind this interface so tests and local runs need no Gotenberg.</summary>
internal interface IPdfRenderer
{
    /// <summary>Renders <paramref name="html"/>; throws <see cref="PdfRenderException"/> when the renderer is unreachable or refuses.</summary>
    Task<PdfRenderResult> RenderAsync(string html, CancellationToken cancellationToken);
}

/// <summary>The renderer failed (unreachable, timed out, or an error response).</summary>
internal sealed class PdfRenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Post-processing hook after rendering (BO-DOC-07): the place for the PDF/A-3 step that embeds the source HTML and its hash as an
/// attachment. The default passes the bytes through.
/// </summary>
internal interface IPdfPostProcessor
{
    /// <summary>Returns the final PDF bytes.</summary>
    byte[] Process(byte[] pdf, PdfPostProcessContext context);
}

/// <summary>What a post-processor may embed: the source HTML and its SHA-256.</summary>
internal sealed record PdfPostProcessContext(string Html, string SourceHash, bool PdfA);

/// <summary>Pass-through post-processor. TODO(hardening): embed the source HTML as a PDF/A-3 associated file (AFRelationship=Source).</summary>
internal sealed class PassThroughPdfPostProcessor : IPdfPostProcessor
{
    public byte[] Process(byte[] pdf, PdfPostProcessContext context) => pdf;
}

/// <summary>Renders through Gotenberg's Chromium route, asking for PDF/A-3b.</summary>
internal sealed class GotenbergPdfRenderer(HttpClient http) : IPdfRenderer
{
    internal const string Route = "forms/chromium/convert/html";

    public async Task<PdfRenderResult> RenderAsync(string html, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(file, "files", "index.html");
        form.Add(new StringContent("PDF/A-3b"), "pdfa");
        form.Add(new StringContent("true"), "printBackground");
        try
        {
            using var response = await http.PostAsync(new Uri(Route, UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new PdfRenderException($"Gotenberg answered {(int)response.StatusCode}.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 5 || !bytes.AsSpan(0, 4).SequenceEqual("%PDF"u8))
            {
                throw new PdfRenderException("Gotenberg did not return a PDF.");
            }

            return new PdfRenderResult(bytes, PdfA: true, "gotenberg");
        }
        catch (HttpRequestException ex)
        {
            throw new PdfRenderException("Gotenberg is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdfRenderException("Gotenberg timed out.", ex);
        }
    }
}

/// <summary>
/// Local fallback when no Gotenberg is configured: a plain text-only PDF (Helvetica, WinAnsi; characters outside printable ASCII become '?'),
/// flagged <c>pdfA: false</c>. For development and smoke tests only. TODO(hardening): embed a Greek-capable font if the fallback is ever used beyond local runs.
/// </summary>
internal sealed partial class PlainPdfRenderer : IPdfRenderer
{
    private const int LinesPerPage = 52;
    private const int WrapAt = 90;

    public Task<PdfRenderResult> RenderAsync(string html, CancellationToken cancellationToken) =>
        Task.FromResult(new PdfRenderResult(Build(ToLines(html)), PdfA: false, "plain"));

    private static List<string> ToLines(string html)
    {
        var text = StyleBlock().Replace(html, " ");
        text = BreakTag().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = WhiteSpace().Replace(raw, " ").Trim();
            while (line.Length > WrapAt)
            {
                var cut = line.LastIndexOf(' ', WrapAt);
                cut = cut <= 0 ? WrapAt : cut;
                lines.Add(line[..cut]);
                line = line[cut..].TrimStart();
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static byte[] Build(List<string> lines)
    {
        var pages = lines.Chunk(LinesPerPage).ToList();
        if (pages.Count == 0)
        {
            pages.Add([" "]);
        }

        // Objects: 1 catalog, 2 pages, 3 font, then (page, content) pairs.
        var objects = new List<byte[]>();
        var kids = string.Join(' ', pages.Select((_, i) => $"{4 + (2 * i)} 0 R"));
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        for (var i = 0; i < pages.Count; i++)
        {
            var stream = new StringBuilder("BT /F1 11 Tf 14 TL 50 790 Td\n");
            foreach (var line in pages[i])
            {
                stream.Append('(').Append(Escape(line)).Append(") Tj T*\n");
            }

            stream.Append("ET");
            var content = stream.ToString();
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * i)} 0 R >>"));
            objects.Add(Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
        }

        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Ascii($"{i + 1} 0 obj\n"));
            output.Write(objects[i]);
            output.Write(Ascii("\nendobj\n"));
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Ascii(table.ToString()));
        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private static string Escape(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is '(' or ')' or (char)92)
            {
                sb.Append((char)92).Append(c);
            }
            else if (c < ' ' || c > '~')
            {
                sb.Append('?');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlock();

    [GeneratedRegex(@"<(br\s*/?|/p|/div|/h[1-6]|/li|/tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
