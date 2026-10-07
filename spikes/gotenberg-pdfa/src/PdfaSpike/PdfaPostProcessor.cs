using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfaSpike;

public sealed record ArchiveMetadata(
    string DocumentNumber,
    string DocumentType,
    string TemplateVersion,
    string Language,
    string Title,
    DateTimeOffset ReferenceTime,   // fixed /CreationDate, /ModDate, xmp dates
    string EngineVersion,
    byte[] Payload,                 // canonical JSON, embedded as associated file
    string PayloadName = "payload.json");

public sealed record PostProcessReport(int ArtifactRunsWrapped, int StructIdsRenumbered, string PayloadSha256);

/// <summary>
/// Deterministic Chromium(Skia)-PDF -> PDF/A-3a + PDF/UA-1 post-processor. No third-party library.
///
/// Full rewrite (not an incremental update): every object is re-serialised in object-number order with a
/// fresh classic xref table, so output bytes depend only on the input objects + <see cref="ArchiveMetadata"/>.
///
/// Steps:
///  1. Parse classic xref (Skia always writes one; no object streams).
///  2. Replace the Info dictionary (drops Creator = Chromium user-agent, which differs per host OS) and set
///     dates from the payload reference time.
///  3. Renumber Chromium's non-deterministic StructElem /ID strings ("nodeNNNNNNNN" = DOM node ids) by
///     document order and rebuild the IDTree as one sorted leaf.
///  4. Wrap content that Chromium left untagged (CSS @page margin boxes / header-footer templates) in
///     /Artifact marked-content sequences (PDF/UA 7.1-3).
///  5. Add XMP (pdfaid 3A, pdfuaid 1, DOC extension schema), sRGB OutputIntent, the payload as an
///     associated file (/AF, AFRelationship /Data) and a deterministic trailer /ID.
/// </summary>
public static class PdfaPostProcessor
{
    sealed class Obj(int num, byte[] body)
    {
        public int Num = num;
        public byte[] Body = body;   // bytes between "N 0 obj" and "endobj" (exclusive)
    }

    static readonly Encoding L1 = Encoding.Latin1;

    public static (byte[] Pdf, PostProcessReport Report) Process(byte[] input, ArchiveMetadata meta)
    {
        var (objs, trailer) = Parse(input);
        var rootNum = RefOf(trailer, "Root") ?? throw new InvalidDataException("no /Root");
        var infoNum = RefOf(trailer, "Info");
        int next = objs.Keys.Max() + 1;

        // --- 3. StructElem /ID renumbering --------------------------------------------------------
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var idRx = new Regex(@"\((node\d+)\)", RegexOptions.CultureInvariant);
        foreach (var o in objs.Values.OrderBy(o => o.Num))
        {
            var s = L1.GetString(o.Body);
            if (!s.Contains("/StructElem")) continue;
            var m = Regex.Match(s, @"/ID \((node\d+)\)");
            if (m.Success && !idMap.ContainsKey(m.Groups[1].Value))
                idMap[m.Groups[1].Value] = $"id{idMap.Count + 1:D8}";
        }
        var rootStr = L1.GetString(objs[rootNum].Body);
        var strRootNum = RefOfDict(rootStr, "StructTreeRoot");
        int? idTreeNum = null;
        var idTreeNodes = new HashSet<int>();
        if (strRootNum is { } sr)
        {
            idTreeNum = RefOfDict(L1.GetString(objs[sr].Body), "IDTree");
            if (idTreeNum is { } it) CollectTree(objs, it, idTreeNodes);
        }
        var idToElem = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in objs.Values)
        {
            if (idTreeNodes.Contains(o.Num)) continue;
            var s = L1.GetString(o.Body);
            if (!s.Contains("(node")) continue;
            s = idRx.Replace(s, m => idMap.TryGetValue(m.Groups[1].Value, out var n) ? $"({n})" : m.Value);
            o.Body = L1.GetBytes(s);
            var m2 = Regex.Match(s, @"/ID \((id\d{8})\)");
            if (m2.Success) idToElem[m2.Groups[1].Value] = o.Num;
        }
        if (idTreeNum is { } itn)
        {
            foreach (var n in idTreeNodes.Where(n => n != itn)) objs.Remove(n);
            var sb = new StringBuilder("\n<</Names [");
            foreach (var (k, v) in idToElem) sb.Append($"({k}) {v} 0 R ");
            sb.Append("]>>\n");
            objs[itn].Body = L1.GetBytes(sb.ToString());
        }

        // --- 4. Artifact wrapping of untagged page content ----------------------------------------
        int wrapped = 0;
        foreach (var o in objs.Values.ToList())
        {
            var s = L1.GetString(o.Body);
            if (!Regex.IsMatch(s, @"/Type /Page\b")) continue;
            foreach (Match cm in Regex.Matches(s, @"/Contents (?:(\d+) 0 R|\[([^\]]*)\])"))
            {
                var refs = cm.Groups[1].Success
                    ? [int.Parse(cm.Groups[1].Value)]
                    : Regex.Matches(cm.Groups[2].Value, @"(\d+) 0 R").Select(x => int.Parse(x.Groups[1].Value)).ToArray();
                foreach (var cn in refs) wrapped += WrapUntaggedContent(objs[cn]);
            }
        }

        // --- 5. New objects -------------------------------------------------------------------------
        var date = PdfDate(meta.ReferenceTime);
        var payloadSha = Convert.ToHexStringLower(SHA256.HashData(meta.Payload));

        int iccNum = next++, oiNum = next++, efNum = next++, fsNum = next++, xmpNum = next++;
        var icc = SrgbIcc.Build();
        objs[iccNum] = new Obj(iccNum, Stream($"/N 3 /Alternate /DeviceRGB", Deflate(icc), filter: true));
        objs[oiNum] = new Obj(oiNum, L1.GetBytes(
            $"\n<</Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB IEC61966-2.1) /Info (sRGB IEC61966-2.1) /RegistryName (http://www.color.org) /DestOutputProfile {iccNum} 0 R>>\n"));
        var md5 = Convert.ToHexString(MD5.HashData(meta.Payload));
        objs[efNum] = new Obj(efNum, Stream(
            $"/Type /EmbeddedFile /Subtype /application#2Fjson /Params <</Size {meta.Payload.Length} /CheckSum <{md5}> /ModDate ({date}) /CreationDate ({date})>>",
            Deflate(meta.Payload), filter: true));
        objs[fsNum] = new Obj(fsNum, L1.GetBytes(
            $"\n<</Type /Filespec /F ({meta.PayloadName}) /UF {PdfText(meta.PayloadName)} /Desc {PdfText("Frozen document payload (canonical JSON), SHA-256 " + payloadSha)} /AFRelationship /Data /EF <</F {efNum} 0 R /UF {efNum} 0 R>>>>\n"));
        var xmp = Encoding.UTF8.GetBytes(Xmp(meta, date, payloadSha));
        objs[xmpNum] = new Obj(xmpNum, Stream("/Type /Metadata /Subtype /XML", xmp, filter: false));

        // Catalog
        if (rootStr.Contains("/Names") || rootStr.Contains("/Metadata") || rootStr.Contains("/AF"))
            throw new InvalidDataException("unexpected catalog keys: " + rootStr);
        var end = rootStr.LastIndexOf(">>", StringComparison.Ordinal);
        rootStr = rootStr[..end]
                  + $"\n/Metadata {xmpNum} 0 R\n/OutputIntents [{oiNum} 0 R]\n/AF [{fsNum} 0 R]\n/Names <</EmbeddedFiles <</Names [({meta.PayloadName}) {fsNum} 0 R]>>>>"
                  + rootStr[end..];
        rootStr = Regex.Replace(rootStr, @"/Lang \([^)]*\)", $"/Lang ({meta.Language})");
        objs[rootNum].Body = L1.GetBytes(rootStr);

        // Info (2.)
        int info = infoNum ?? next++;
        objs[info] = new Obj(info, L1.GetBytes(
            $"\n<</Title {PdfText(meta.Title)}\n/Producer {PdfText(meta.EngineVersion)}\n/CreationDate ({date})\n/ModDate ({date})>>\n"));

        // Deterministic file identifier derived from the document number (REQ-DOC-160)
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("DOC-ID:" + meta.DocumentNumber)).AsSpan(0, 16));
        var pdf = Write(objs, $"/Root {rootNum} 0 R /Info {info} 0 R /ID [<{id}> <{id}>]");
        return (pdf, new PostProcessReport(wrapped, idMap.Count, payloadSha));
    }

    // ------------------------------------------------------------------ parsing
    static (SortedDictionary<int, Obj>, string) Parse(byte[] pdf)
    {
        var tail = L1.GetString(pdf, Math.Max(0, pdf.Length - 1024), Math.Min(1024, pdf.Length));
        var sx = Regex.Matches(tail, @"startxref\s+(\d+)");
        if (sx.Count == 0) throw new InvalidDataException("no startxref");
        long xref = long.Parse(sx[^1].Groups[1].Value);
        var head = L1.GetString(pdf, (int)xref, (int)Math.Min(pdf.Length - xref, int.MaxValue));
        if (!head.StartsWith("xref")) throw new InvalidDataException("xref stream not supported in spike (expected Skia classic xref)");
        var trailerIdx = head.IndexOf("trailer", StringComparison.Ordinal);
        var trailer = head[trailerIdx..head.IndexOf("startxref", trailerIdx, StringComparison.Ordinal)];
        if (trailer.Contains("/Prev")) throw new InvalidDataException("incremental updates not supported in spike");
        var offsets = new List<(int Num, long Off)>();
        var lines = head[4..trailerIdx].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int cur = 0, left = 0;
        foreach (var l in lines)
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left == 0 && p.Length == 2) { cur = int.Parse(p[0]); left = int.Parse(p[1]); continue; }
            if (p.Length >= 3 && p[2] == "n") offsets.Add((cur, long.Parse(p[0])));
            cur++; left--;
        }
        offsets.Sort((a, b) => a.Off.CompareTo(b.Off));
        var objs = new SortedDictionary<int, Obj>();
        for (int i = 0; i < offsets.Count; i++)
        {
            long start = offsets[i].Off, stop = i + 1 < offsets.Count ? offsets[i + 1].Off : xref;
            var span = pdf.AsSpan((int)start, (int)(stop - start));
            var hdr = L1.GetBytes($"{offsets[i].Num} 0 obj");
            if (!span.StartsWith(hdr)) throw new InvalidDataException($"object {offsets[i].Num} not at offset");
            var endIdx = span.LastIndexOf("endobj"u8);
            objs[offsets[i].Num] = new Obj(offsets[i].Num, span[hdr.Length..endIdx].ToArray());
        }
        return (objs, trailer);
    }

    static int? RefOf(string dict, string key) => RefOfDict(dict, key);

    static int? RefOfDict(string dict, string key)
    {
        var m = Regex.Match(dict, $@"/{key}\s+(\d+)\s+0\s+R");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    static void CollectTree(SortedDictionary<int, Obj> objs, int n, HashSet<int> acc)
    {
        if (!acc.Add(n)) return;
        var s = L1.GetString(objs[n].Body);
        var k = Regex.Match(s, @"/Kids\s*\[([^\]]*)\]");
        if (k.Success)
            foreach (Match r in Regex.Matches(k.Groups[1].Value, @"(\d+) 0 R")) CollectTree(objs, int.Parse(r.Groups[1].Value), acc);
    }

    // ------------------------------------------------------------------ content streams
    static (string Dict, byte[] Data) SplitStream(byte[] body)
    {
        var s = L1.GetString(body);
        var si = s.IndexOf("stream", StringComparison.Ordinal);
        int dataStart = si + 6;
        if (s[dataStart] == '\r') dataStart++;
        if (s[dataStart] == '\n') dataStart++;
        var dict = s[..si];
        var lm = Regex.Match(dict, @"/Length (\d+)(?! 0 R)");
        int len = lm.Success ? int.Parse(lm.Groups[1].Value) : s.LastIndexOf("endstream", StringComparison.Ordinal) - dataStart;
        return (dict, body.AsSpan(dataStart, len).ToArray());
    }

    static readonly HashSet<string> PaintOps = ["Tj", "TJ", "'", "\"", "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "sh", "Do", "EI"];

    /// <summary>Wraps runs of painting operators at marked-content depth 0 in /Artifact BMC..EMC.
    /// Runs never cross BT/ET or other marked-content operators, so nesting rules hold.</summary>
    static int WrapUntaggedContent(Obj o)
    {
        var (dict, data) = SplitStream(o.Body);
        bool flate = dict.Contains("/FlateDecode");
        var raw = flate ? Inflate(data) : data;
        var ops = ContentTokenizer.Operators(raw);   // (start, end, op)
        var outp = new MemoryStream(raw.Length + 1024);
        int depth = 0, runStart = -1, wraps = 0; bool runPaints = false; int lastEnd = 0;
        var pending = new List<(int Start, int End)>();
        void Close()
        {
            if (runStart >= 0 && runPaints) pending.Add((runStart, lastEnd));
            runStart = -1; runPaints = false;
        }
        foreach (var (start, endPos, op) in ops)
        {
            switch (op)
            {
                case "BDC" or "BMC":
                    if (depth == 0) Close();
                    depth++; break;
                case "EMC":
                    depth--; break;
                case "BT" or "ET":
                    if (depth == 0) Close();
                    break;
                default:
                    if (depth == 0)
                    {
                        if (runStart < 0) runStart = start;
                        if (PaintOps.Contains(op)) runPaints = true;
                        lastEnd = endPos;
                    }
                    break;
            }
        }
        Close();
        int pos = 0;
        foreach (var (s, e) in pending)
        {
            outp.Write(raw, pos, s - pos);
            outp.Write("/Artifact BMC\n"u8);
            outp.Write(raw, s, e - s);
            outp.Write("\nEMC"u8);
            pos = e; wraps++;
        }
        outp.Write(raw, pos, raw.Length - pos);
        if (wraps == 0) return 0;
        var newData = flate ? Deflate(outp.ToArray()) : outp.ToArray();
        var d = Regex.Replace(dict, @"/Length \d+", $"/Length {newData.Length}");
        o.Body = Concat(L1.GetBytes(d), "stream\n"u8.ToArray(), newData, "\nendstream\n"u8.ToArray());
        return wraps;
    }

    // ------------------------------------------------------------------ writing
    static byte[] Write(SortedDictionary<int, Obj> objs, string trailerExtra)
    {
        var ms = new MemoryStream();
        ms.Write(L1.GetBytes("%PDF-1.7\n%âãÏÓ\n"));
        int max = objs.Keys.Max();
        var off = new long[max + 1];
        foreach (var (n, o) in objs)
        {
            off[n] = ms.Position;
            ms.Write(L1.GetBytes($"{n} 0 obj"));
            ms.Write(o.Body);
            ms.Write("endobj\n"u8);
        }
        long xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {max + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= max; i++)
            sb.Append(objs.ContainsKey(i) ? $"{off[i]:D10} 00000 n \n" : "0000000000 00000 f \n");
        sb.Append($"trailer\n<</Size {max + 1} {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(L1.GetBytes(sb.ToString()));
        return ms.ToArray();
    }

    static byte[] Stream(string dictEntries, byte[] data, bool filter) =>
        Concat(L1.GetBytes($"\n<<{dictEntries}{(filter ? " /Filter /FlateDecode" : "")} /Length {data.Length}>>\nstream\n"), data, "\nendstream\n"u8.ToArray());

    static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)]; int i = 0;
        foreach (var p in parts) { p.CopyTo(r, i); i += p.Length; }
        return r;
    }

    public static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var ms = new MemoryStream(); z.CopyTo(ms); return ms.ToArray();
    }

    static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    static string PdfDate(DateTimeOffset t) => "D:" + t.UtcDateTime.ToString("yyyyMMddHHmmss") + "+00'00'";

    static string PdfText(string s)
    {
        if (s.All(c => c >= 0x20 && c < 0x7F && c != '(' && c != ')' && c != '\\')) return "(" + s + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";
    }

    static string X(string s) => System.Security.SecurityElement.Escape(s)!;

    static string Xmp(ArchiveMetadata m, string pdfDate, string payloadSha)
    {
        var iso = m.ReferenceTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss") + "Z";
        return $"""
<?xpacket begin="{'﻿'}" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
 <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
  <rdf:Description rdf:about=""
    xmlns:dc="http://purl.org/dc/elements/1.1/"
    xmlns:xmp="http://ns.adobe.com/xap/1.0/"
    xmlns:pdf="http://ns.adobe.com/pdf/1.3/"
    xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"
    xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/"
    xmlns:doc="urn:coreins:doc:1.0/"
    xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/"
    xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#"
    xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#">
   <dc:format>application/pdf</dc:format>
   <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{X(m.Title)}</rdf:li></rdf:Alt></dc:title>
   <dc:language><rdf:Bag><rdf:li>{X(m.Language)}</rdf:li></rdf:Bag></dc:language>
   <xmp:CreateDate>{iso}</xmp:CreateDate>
   <xmp:ModifyDate>{iso}</xmp:ModifyDate>
   <xmp:MetadataDate>{iso}</xmp:MetadataDate>
   <pdf:Producer>{X(m.EngineVersion)}</pdf:Producer>
   <pdfaid:part>3</pdfaid:part>
   <pdfaid:conformance>A</pdfaid:conformance>
   <pdfuaid:part>1</pdfuaid:part>
   <doc:DocumentNumber>{X(m.DocumentNumber)}</doc:DocumentNumber>
   <doc:DocumentType>{X(m.DocumentType)}</doc:DocumentType>
   <doc:TemplateVersion>{X(m.TemplateVersion)}</doc:TemplateVersion>
   <doc:PayloadSha256>{payloadSha}</doc:PayloadSha256>
   <doc:Language>{X(m.Language)}</doc:Language>
   <pdfaExtension:schemas>
    <rdf:Bag>
     <rdf:li rdf:parseType="Resource">
      <pdfaSchema:schema>PDF/UA identification schema</pdfaSchema:schema>
      <pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/</pdfaSchema:namespaceURI>
      <pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>
      <pdfaSchema:property><rdf:Seq>
       <rdf:li rdf:parseType="Resource"><pdfaProperty:name>part</pdfaProperty:name><pdfaProperty:valueType>Integer</pdfaProperty:valueType><pdfaProperty:category>internal</pdfaProperty:category><pdfaProperty:description>PDF/UA version identifier</pdfaProperty:description></rdf:li>
      </rdf:Seq></pdfaSchema:property>
     </rdf:li>
     <rdf:li rdf:parseType="Resource">
      <pdfaSchema:schema>CoreIns DOC archive rendition schema</pdfaSchema:schema>
      <pdfaSchema:namespaceURI>urn:coreins:doc:1.0/</pdfaSchema:namespaceURI>
      <pdfaSchema:prefix>doc</pdfaSchema:prefix>
      <pdfaSchema:property><rdf:Seq>
       <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentNumber</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>DOC document number</pdfaProperty:description></rdf:li>
       <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentType</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>DOC document type code</pdfaProperty:description></rdf:li>
       <rdf:li rdf:parseType="Resource"><pdfaProperty:name>TemplateVersion</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>Template version id</pdfaProperty:description></rdf:li>
       <rdf:li rdf:parseType="Resource"><pdfaProperty:name>PayloadSha256</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>SHA-256 of the embedded canonical payload</pdfaProperty:description></rdf:li>
       <rdf:li rdf:parseType="Resource"><pdfaProperty:name>Language</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>Document language</pdfaProperty:description></rdf:li>
      </rdf:Seq></pdfaSchema:property>
     </rdf:li>
    </rdf:Bag>
   </pdfaExtension:schemas>
  </rdf:Description>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end="r"?>
""";
    }
}

/// <summary>Minimal PDF content-stream tokenizer returning operator spans (operands + operator).</summary>
static class ContentTokenizer
{
    public static List<(int Start, int End, string Op)> Operators(byte[] b)
    {
        var res = new List<(int, int, string)>();
        int i = 0, n = b.Length, opStart = -1;
        while (i < n)
        {
            byte c = b[i];
            if (IsWs(c)) { i++; continue; }
            if (opStart < 0) opStart = i;
            if (c == '%') { while (i < n && b[i] != '\n' && b[i] != '\r') i++; continue; }
            if (c == '(') { int d = 0; for (; i < n; i++) { if (b[i] == '\\') { i++; continue; } if (b[i] == '(') d++; else if (b[i] == ')' && --d == 0) { i++; break; } } continue; }
            if (c == '<' && i + 1 < n && b[i + 1] == '<') { i += 2; continue; }
            if (c == '>' && i + 1 < n && b[i + 1] == '>') { i += 2; continue; }
            if (c == '<') { while (i < n && b[i] != '>') i++; i++; continue; }
            if (c == '[' || c == ']' || c == '{' || c == '}') { i++; continue; }
            if (c == '/') { i++; while (i < n && !IsWs(b[i]) && !IsDelim(b[i])) i++; continue; }
            int s = i;
            while (i < n && !IsWs(b[i]) && !IsDelim(b[i])) i++;
            if (i == s) { i++; continue; }
            var tok = Encoding.Latin1.GetString(b, s, i - s);
            if (IsNumber(tok) || tok is "true" or "false" or "null") continue;
            res.Add((opStart, i, tok));
            opStart = -1;
            if (tok == "BI") throw new NotSupportedException("inline images not handled in spike");
        }
        return res;
    }
    static bool IsWs(byte c) => c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0;
    static bool IsDelim(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    static bool IsNumber(string t) => t.Length > 0 && t.All(ch => char.IsAsciiDigit(ch) || ch is '.' or '-' or '+');
}
