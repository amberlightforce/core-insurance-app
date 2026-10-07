using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreIns.Modules.Market.Domain;

/// <summary>Rounding modes of <c>cur.rounding.*</c> (REQ-MKT-192).</summary>
internal enum RoundingMode
{
    HalfUp,
    HalfEven,
    Down,
    Up,
    Ceiling,
    Floor,
}

/// <summary>A rounding rule: mode, scale (null = the currency's ISO 4217 minor units) and level (REQ-MKT-192).</summary>
internal sealed record RoundingRule(RoundingMode Mode, int? Scale, string Level)
{
    private static readonly Dictionary<string, RoundingMode> Modes = new(StringComparer.Ordinal)
    {
        ["HALF_UP"] = RoundingMode.HalfUp,
        ["HALF_EVEN"] = RoundingMode.HalfEven,
        ["DOWN"] = RoundingMode.Down,
        ["UP"] = RoundingMode.Up,
        ["CEILING"] = RoundingMode.Ceiling,
        ["FLOOR"] = RoundingMode.Floor,
    };

    public string ModeCode => Modes.First(kv => kv.Value == Mode).Key;

    /// <summary>Parses the JSON value of a <c>cur.rounding.*</c> key.</summary>
    public static RoundingRule Parse(JsonElement json)
    {
        var mode = json.GetProperty("mode").GetString() ?? string.Empty;
        if (!Modes.TryGetValue(mode, out var parsed))
        {
            throw new InvalidOperationException($"Unknown rounding mode '{mode}'.");
        }

        int? scale = json.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
        var level = json.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "LINE";
        return new RoundingRule(parsed, scale, level);
    }

    /// <summary>Rounds <paramref name="amount"/> to <paramref name="scale"/> places with this rule's mode (exact decimal arithmetic).</summary>
    public decimal Apply(decimal amount, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        return Mode switch
        {
            RoundingMode.HalfUp => decimal.Round(amount, scale, MidpointRounding.AwayFromZero),
            RoundingMode.HalfEven => decimal.Round(amount, scale, MidpointRounding.ToEven),
            RoundingMode.Down => decimal.Round(amount, scale, MidpointRounding.ToZero),
            RoundingMode.Up => decimal.Round(amount, scale, amount >= 0 ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToNegativeInfinity),
            RoundingMode.Ceiling => decimal.Round(amount, scale, MidpointRounding.ToPositiveInfinity),
            _ => decimal.Round(amount, scale, MidpointRounding.ToNegativeInfinity),
        };
    }

    /// <summary>Deterministic rule id from the rule key and its parameters (REQ-MKT-195).</summary>
    public Guid IdFor(string ruleKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleKey}|{ModeCode}|{Scale}|{Level}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
