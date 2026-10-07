using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfaSpike;

/// <summary>
/// Feasibility probe for REQ-DOC-167: apply an invisible PAdES (ETSI.CAdES.detached, baseline B-B) seal to the
/// PDF/A-3a archive rendition as an INCREMENTAL UPDATE, using only in-box .NET crypto
/// (System.Security.Cryptography.Pkcs = Microsoft, MIT). The original bytes stay an exact prefix of the sealed
/// file, so REQ-DOC-160 reproducibility can be proven on the pre-seal revision.
/// Not done here (see FINDINGS): RFC 3161 signature timestamp (B-T), DSS/VRI with OCSP/CRL (B-LT),
/// document timestamp (B-LTA), HSM/Key Vault/QTSP-held key.
/// </summary>
public static class PadesSeal
{
    const int SigHexLen = 16384; // reserved /Contents size in hex chars (8 KiB DER)
    static readonly Encoding L1 = Encoding.Latin1;

    public static X509Certificate2 TestSealCertificate()
    {
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest("CN=Elliniki Asfalistiki AE Test Seal, O=Elliniki Asfalistiki AE, C=GR, OID.2.5.4.97=VATGR-123456789",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
        using var cert = req.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    public static byte[] Seal(byte[] pdf, X509Certificate2 cert, DateTimeOffset signingTime)
    {
        var s = L1.GetString(pdf);
        var trailer = s[s.LastIndexOf("trailer", StringComparison.Ordinal)..];
        int size = int.Parse(Regex.Match(trailer, @"/Size (\d+)").Groups[1].Value);
        int root = int.Parse(Regex.Match(trailer, @"/Root (\d+) 0 R").Groups[1].Value);
        var info = Regex.Match(trailer, @"/Info (\d+) 0 R").Groups[1].Value;
        var id = Regex.Match(trailer, @"/ID \[<([0-9A-F]+)>").Groups[1].Value;
        long prevXref = long.Parse(Regex.Match(trailer, @"startxref\s+(\d+)").Groups[1].Value);

        string ObjBody(int n)
        {
            var m = Regex.Match(s, $@"(?<![0-9]){n} 0 obj([\s\S]*?)endobj");
            return m.Groups[1].Value;
        }
        var pagesNum = int.Parse(Regex.Match(ObjBody(root), @"/Pages (\d+) 0 R").Groups[1].Value);
        var firstPage = int.Parse(Regex.Match(ObjBody(pagesNum), @"/Kids \[(\d+) 0 R").Groups[1].Value);

        int sigNum = size, widgetNum = size + 1;
        var catalog = ObjBody(root);
        catalog = catalog[..catalog.LastIndexOf(">>", StringComparison.Ordinal)] + $"\n/AcroForm <</Fields [{widgetNum} 0 R] /SigFlags 3>>>>\n";
        var page = ObjBody(firstPage);
        if (page.Contains("/Annots")) throw new NotSupportedException("page already has annotations (spike)");
        page = page[..page.LastIndexOf(">>", StringComparison.Ordinal)] + $"\n/Annots [{widgetNum} 0 R]>>\n";
        var m = "D:" + signingTime.UtcDateTime.ToString("yyyyMMddHHmmss") + "+00'00'";

        const string brPlaceholder = "/ByteRange [0 0000000000 0000000000 0000000000]";
        var sigObj = $"{sigNum} 0 obj\n<</Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached {brPlaceholder} /Contents <{new string('0', SigHexLen)}> /M ({m}) /Reason (Insurer electronic seal - REQ-DOC-167)>>\nendobj\n";
        // Invisible widget: zero-size Rect, Print flag (PDF/A 6.3.2). Not tagged (zero-area = not visible).
        var widgetObj = $"{widgetNum} 0 obj\n<</Type /Annot /Subtype /Widget /FT /Sig /T (InsurerSeal1) /TU (Insurer electronic seal) /V {sigNum} 0 R /F 4 /Rect [0 0 0 0] /P {firstPage} 0 R>>\nendobj\n";

        var inc = new StringBuilder("\n");
        var offs = new SortedDictionary<int, long>();
        void Add(int n, string text) { offs[n] = pdf.Length + L1.GetByteCount(inc.ToString()); inc.Append(text); }
        Add(root, $"{root} 0 obj{catalog}endobj\n");
        Add(firstPage, $"{firstPage} 0 obj{page}endobj\n");
        Add(sigNum, sigObj);
        Add(widgetNum, widgetObj);
        long xrefPos = pdf.Length + L1.GetByteCount(inc.ToString());
        inc.Append("xref\n");
        foreach (var (n, o) in offs) inc.Append($"{n} 1\n{o:D10} 00000 n \n");
        inc.Append($"trailer\n<</Size {size + 2} /Root {root} 0 R /Info {info} 0 R /ID [<{id}> <{id}>] /Prev {prevXref}>>\nstartxref\n{xrefPos}\n%%EOF\n");

        var outBytes = new byte[pdf.Length + L1.GetByteCount(inc.ToString())];
        pdf.CopyTo(outBytes, 0);
        L1.GetBytes(inc.ToString()).CopyTo(outBytes, pdf.Length);

        // ByteRange
        var all = L1.GetString(outBytes);
        int contentsStart = all.IndexOf("/Contents <", (int)offs[sigNum], StringComparison.Ordinal) + "/Contents ".Length; // at '<'
        int contentsEnd = contentsStart + SigHexLen + 2;                                                                   // after '>'
        var br = $"/ByteRange [0 {contentsStart} {contentsEnd} {outBytes.Length - contentsEnd}]";
        br = br.PadRight(brPlaceholder.Length);
        int brPos = all.IndexOf(brPlaceholder, (int)offs[sigNum], StringComparison.Ordinal);
        L1.GetBytes(br).CopyTo(outBytes, brPos);

        var signed = new byte[contentsStart + (outBytes.Length - contentsEnd)];
        Buffer.BlockCopy(outBytes, 0, signed, 0, contentsStart);
        Buffer.BlockCopy(outBytes, contentsEnd, signed, contentsStart, outBytes.Length - contentsEnd);

        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };
        signer.SignedAttributes.Add(SigningCertificateV2(cert));   // ESS signing-certificate-v2 (PAdES baseline)
        signer.IncludeOption = X509IncludeOption.EndCertOnly;
        cms.ComputeSignature(signer, silent: true);
        var der = cms.Encode();
        if (der.Length * 2 > SigHexLen) throw new InvalidOperationException("signature too large for placeholder");
        var hex = Convert.ToHexString(der).PadRight(SigHexLen, '0');
        L1.GetBytes(hex).CopyTo(outBytes, contentsStart + 1);
        return outBytes;
    }

    static AsnEncodedData SigningCertificateV2(X509Certificate2 cert)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())            // SigningCertificateV2
        using (w.PushSequence())            // certs
        using (w.PushSequence())            // ESSCertIDv2 (hashAlgorithm DEFAULT sha256 omitted)
            w.WriteOctetString(SHA256.HashData(cert.RawData));
        return new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), w.Encode());
    }

    /// <summary>Re-verifies the seal: ByteRange covers the whole file except /Contents, CMS signature valid,
    /// and returns the length of the pre-seal revision (for the reproducibility prefix check).</summary>
    public static (bool Valid, string Detail) Verify(byte[] sealedPdf)
    {
        var s = L1.GetString(sealedPdf);
        var mm = Regex.Match(s, @"/ByteRange \[0 (\d+) (\d+) (\d+)\s*\]");
        int a = int.Parse(mm.Groups[1].Value), b = int.Parse(mm.Groups[2].Value), c = int.Parse(mm.Groups[3].Value);
        bool coversAll = b + c == sealedPdf.Length;
        var hex = s.Substring(a + 1, b - a - 2).TrimEnd('0');
        if (hex.Length % 2 == 1) hex += "0";
        var signed = new byte[a + c];
        Buffer.BlockCopy(sealedPdf, 0, signed, 0, a);
        Buffer.BlockCopy(sealedPdf, b, signed, a, c);
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(Convert.FromHexString(hex));
        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
            var si = cms.SignerInfos[0];
            var attrs = string.Join(",", si.SignedAttributes.Cast<CryptographicAttributeObject>().Select(x => x.Oid.FriendlyName ?? x.Oid.Value));
            return (coversAll, $"CMS signature valid; ByteRange covers whole file except /Contents: {coversAll}; signer={si.Certificate?.Subject}; digest={si.DigestAlgorithm.FriendlyName}; signed attrs=[{attrs}]");
        }
        catch (CryptographicException e) { return (false, "CMS invalid: " + e.Message); }
    }
}
