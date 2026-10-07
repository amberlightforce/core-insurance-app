using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace PdfaSpike;

/// <summary>Reads the Unicode coverage of a TrueType/OpenType font from its cmap table (formats 4 and 12).</summary>
public sealed class FontCoverage
{
    readonly HashSet<int> _cps = new();
    public string Name { get; }
    public int Count => _cps.Count;
    public bool Covers(int cp) => _cps.Contains(cp);

    public FontCoverage(string name, byte[] ttf)
    {
        Name = name;
        var span = ttf.AsSpan();
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        int cmap = -1;
        for (int i = 0; i < numTables; i++)
        {
            var rec = span.Slice(12 + 16 * i, 16);
            if (Encoding.ASCII.GetString(rec[..4]) == "cmap") cmap = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[8..]);
        }
        if (cmap < 0) throw new InvalidDataException("no cmap");
        int n = BinaryPrimitives.ReadUInt16BigEndian(span[(cmap + 2)..]);
        int best = -1, bestFmt = 0;
        for (int i = 0; i < n; i++)
        {
            var rec = span.Slice(cmap + 4 + 8 * i, 8);
            ushort pid = BinaryPrimitives.ReadUInt16BigEndian(rec), eid = BinaryPrimitives.ReadUInt16BigEndian(rec[2..]);
            int off = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(rec[4..]);
            int fmt = BinaryPrimitives.ReadUInt16BigEndian(span[off..]);
            bool unicode = pid == 0 || (pid == 3 && (eid == 1 || eid == 10));
            if (unicode && (fmt == 12 || (fmt == 4 && bestFmt != 12))) { best = off; bestFmt = fmt; }
        }
        if (best < 0) throw new InvalidDataException("no unicode cmap");
        if (bestFmt == 12)
        {
            uint groups = BinaryPrimitives.ReadUInt32BigEndian(span[(best + 12)..]);
            for (int g = 0; g < groups; g++)
            {
                var gr = span.Slice(best + 16 + 12 * g, 12);
                uint s = BinaryPrimitives.ReadUInt32BigEndian(gr), e = BinaryPrimitives.ReadUInt32BigEndian(gr[4..]);
                uint gid = BinaryPrimitives.ReadUInt32BigEndian(gr[8..]);
                for (uint c = s; c <= e; c++) if (gid + (c - s) != 0) _cps.Add((int)c);
            }
        }
        else
        {
            int segX2 = BinaryPrimitives.ReadUInt16BigEndian(span[(best + 6)..]);
            int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int i = 0; i < segX2 / 2; i++)
            {
                int end = BinaryPrimitives.ReadUInt16BigEndian(span[(ends + 2 * i)..]);
                int start = BinaryPrimitives.ReadUInt16BigEndian(span[(starts + 2 * i)..]);
                short delta = BinaryPrimitives.ReadInt16BigEndian(span[(deltas + 2 * i)..]);
                int ro = BinaryPrimitives.ReadUInt16BigEndian(span[(ranges + 2 * i)..]);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int gid;
                    if (ro == 0) gid = (c + delta) & 0xFFFF;
                    else
                    {
                        int addr = ranges + 2 * i + ro + 2 * (c - start);
                        gid = BinaryPrimitives.ReadUInt16BigEndian(span[addr..]);
                        if (gid != 0) gid = (gid + delta) & 0xFFFF;
                    }
                    if (gid != 0) _cps.Add(c);
                }
            }
        }
    }
}

public sealed record MissingGlyph(int CodePoint, string Context)
{
    public override string ToString() => $"U+{CodePoint:X4} '{char.ConvertFromUtf32(CodePoint)}' ({CharUnicodeInfo.GetUnicodeCategory(CodePoint)}) in \"{Context}\"";
}

public static class GlyphCheck
{
    /// <summary>
    /// Pre-render check (REQ-DOC-149): every code point of every text node (NFC) must be covered by every
    /// face of the declared font set; otherwise DOC-ERR-GLYPH-MISSING naming the code point.
    /// The spike derives text from the composed HTML; production does it on the composer's text nodes.
    /// </summary>
    public static List<MissingGlyph> PreCheck(string html, IReadOnlyList<FontCoverage> faces)
    {
        var body = Regex.Replace(html, @"<style[\s\S]*?</style>", " ");
        var text = WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", " "));
        // CSS generated content (margin boxes) is text too
        foreach (Match m in Regex.Matches(html, "content:\\s*((?:\"[^\"]*\"|[^;])*)")) text += " " + m.Groups[1].Value;
        text = text.Normalize(NormalizationForm.FormC);
        var missing = new List<MissingGlyph>();
        var seen = new HashSet<int>();
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (cp is '\n' or '\r' or '\t') continue;
            if (!seen.Add(cp)) continue;
            if (faces.All(f => f.Covers(cp))) continue;
            var ctx = text.Substring(Math.Max(0, i - 15), Math.Min(30, text.Length - Math.Max(0, i - 15))).ReplaceLineEndings(" ").Trim();
            missing.Add(new MissingGlyph(cp, ctx));
        }
        return missing;
    }

    public sealed record FontUse(string BaseFont, string Family, bool Declared, int Glyphs, int NotdefGlyphs);

    /// <summary>
    /// Post-render check: list the fonts actually embedded and flag (a) any family outside the declared set
    /// (= silent Chromium/fontconfig fallback) and (b) .notdef (glyph id 0, "tofu") usage.
    /// </summary>
    public static List<FontUse> PostCheck(byte[] pdf, IReadOnlyCollection<string> declaredFamilies)
    {
        var uses = new Dictionary<string, (int g, int nd)>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            foreach (var l in page.Letters)
            {
                var name = l.FontName ?? "?";
                uses.TryGetValue(name, out var u);
                // PdfPig gives no glyph id directly; Skia maps .notdef to CID 0 which has no ToUnicode entry.
                bool notdef = string.IsNullOrEmpty(l.Value) || l.Value == "�" || l.Value == "\0";
                uses[name] = (u.g + 1, u.nd + (notdef ? 1 : 0));
            }
        }
        return uses.Select(kv =>
        {
            var family = Regex.Replace(kv.Key, @"^[A-Z]{6}\+", "");
            family = Regex.Replace(family, @"[-,].*$", "");
            return new FontUse(kv.Key, family, declaredFamilies.Contains(family), kv.Value.g, kv.Value.nd);
        }).OrderBy(f => f.BaseFont).ToList();
    }
}
