using System.Buffers.Binary;
using System.Text;

namespace PdfaSpike;

/// <summary>
/// Builds a small ICC v2 display (mntr/RGB/XYZ) profile with sRGB primaries (D50-adapted) and a
/// gamma 2.2 TRC, deterministically, so the spike needs no third-party ICC file.
/// PRODUCTION: ship the ICC's official sRGB profile (sRGB2014.icc / sRGB_v4_ICC_preference.icc,
/// freely redistributable per color.org terms) as a pinned, hashed asset of the rendering profile.
/// </summary>
public static class SrgbIcc
{
    public static byte[] Build()
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc("sRGB IEC61966-2.1 (spike approximation)")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4361, 0.2225, 0.0139)),
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
            ("rTRC", Curv(2.2)),
            ("gTRC", Curv(2.2)),
            ("bTRC", Curv(2.2)),
        };
        int tableSize = 4 + 12 * tags.Count;
        int offset = 128 + tableSize;
        var body = new MemoryStream();
        var table = new byte[tableSize];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            var (sig, data) = tags[i];
            int pos = offset + (int)body.Length;
            Encoding.ASCII.GetBytes(sig).CopyTo(table, 4 + i * 12);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + i * 12), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12 + i * 12), (uint)data.Length);
            body.Write(data);
            while (body.Length % 4 != 0) body.WriteByte(0);
        }
        var total = 128 + tableSize + (int)body.Length;
        var h = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), 0x02100000);     // v2.1
        Encoding.ASCII.GetBytes("mntr").CopyTo(h, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(h, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(h, 20);
        // date 2026-01-01 00:00:00 (fixed -> deterministic)
        ushort[] dt = [2026, 1, 1, 0, 0, 0];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(24 + i * 2), dt[i]);
        Encoding.ASCII.GetBytes("acsp").CopyTo(h, 36);
        WriteS15(h.AsSpan(68), 0.9642); WriteS15(h.AsSpan(72), 1.0); WriteS15(h.AsSpan(76), 0.8249);
        var r = new byte[total];
        h.CopyTo(r, 0); table.CopyTo(r, 128); body.ToArray().CopyTo(r, 128 + tableSize);
        return r;
    }

    static void WriteS15(Span<byte> s, double v) => BinaryPrimitives.WriteInt32BigEndian(s, (int)Math.Round(v * 65536));

    static byte[] Xyz(double x, double y, double z)
    {
        var b = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(b, 0);
        WriteS15(b.AsSpan(8), x); WriteS15(b.AsSpan(12), y); WriteS15(b.AsSpan(16), z);
        return b;
    }

    static byte[] Curv(double gamma)
    {
        var b = new byte[14];
        Encoding.ASCII.GetBytes("curv").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return b;
    }

    static byte[] Text(string s)
    {
        var a = Encoding.ASCII.GetBytes(s + "\0");
        var b = new byte[8 + a.Length];
        Encoding.ASCII.GetBytes("text").CopyTo(b, 0);
        a.CopyTo(b, 8);
        return b;
    }

    static byte[] Desc(string s)
    {
        var a = Encoding.ASCII.GetBytes(s + "\0");
        var b = new byte[12 + a.Length + 4 + 4 + 2 + 1 + 67];
        Encoding.ASCII.GetBytes("desc").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)a.Length);
        a.CopyTo(b, 12);
        return b; // unicode lang/count = 0, scriptcode count = 0
    }
}
