using System.Globalization;
using System.Numerics;

namespace CoreIns.Modules.Billing.Domain;

/// <summary>ISO 11649 reference from an opaque UUID, without payer information.</summary>
internal static class ReceivableReference
{
    public static string Create(Guid id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(id.ToByteArray());
        var value = new BigInteger(digest, isUnsigned: true, isBigEndian: true);
        value %= BigInteger.Pow(36, 21);
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var body = string.Empty;
        do
        {
            value = BigInteger.DivRem(value, 36, out var remainder);
            body = alphabet[(int)remainder] + body;
        }
        while (value > 0);

        body = body.PadLeft(21, '0');
        var numeric = string.Concat((body + "RF00").Select(c => c <= '9' ? c.ToString() : ((int)c - 'A' + 10).ToString(CultureInfo.InvariantCulture)));
        var checksum = 98 - (int)(BigInteger.Parse(numeric, CultureInfo.InvariantCulture) % 97);
        return "RF" + checksum.ToString("00", CultureInfo.InvariantCulture) + body;
    }
}


