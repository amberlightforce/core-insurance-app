using System.Globalization;
using System.Numerics;
using CoreIns.SharedKernel.Json;
using FsCheck.Xunit;

namespace CoreIns.SharedKernel.Tests;

/// <summary>RFC 8785 (JCS) test vectors and properties (D-ARC-12).</summary>
public sealed class CanonicalJsonTests
{
    /// <summary>RFC 8785 Appendix B: IEEE 754 bit patterns and their canonical ECMAScript text.</summary>
    [Theory]
    [InlineData(0x0000000000000000UL, "0")]
    [InlineData(0x8000000000000000UL, "0")]
    [InlineData(0x0000000000000001UL, "5e-324")]
    [InlineData(0x8000000000000001UL, "-5e-324")]
    [InlineData(0x7fefffffffffffffUL, "1.7976931348623157e+308")]
    [InlineData(0xffefffffffffffffUL, "-1.7976931348623157e+308")]
    [InlineData(0x4340000000000000UL, "9007199254740992")]
    [InlineData(0xc340000000000000UL, "-9007199254740992")]
    [InlineData(0x4430000000000000UL, "295147905179352830000")]
    [InlineData(0x44b52d02c7e14af5UL, "9.999999999999997e+22")]
    [InlineData(0x44b52d02c7e14af6UL, "1e+23")]
    [InlineData(0x44b52d02c7e14af7UL, "1.0000000000000001e+23")]
    [InlineData(0x444b1ae4d6e2ef4eUL, "999999999999999700000")]
    [InlineData(0x444b1ae4d6e2ef4fUL, "999999999999999900000")]
    [InlineData(0x444b1ae4d6e2ef50UL, "1e+21")]
    [InlineData(0x3eb0c6f7a0b5ed8cUL, "9.999999999999997e-7")]
    [InlineData(0x3eb0c6f7a0b5ed8dUL, "0.000001")]
    [InlineData(0x41b3de4355555553UL, "333333333.3333332")]
    [InlineData(0x41b3de4355555554UL, "333333333.33333325")]
    [InlineData(0x41b3de4355555555UL, "333333333.3333333")]
    [InlineData(0x41b3de4355555556UL, "333333333.3333334")]
    [InlineData(0x41b3de4355555557UL, "333333333.33333343")]
    [InlineData(0xbecbf647612f3696UL, "-0.0000033333333333333333")]
    [InlineData(0x43143ff3c1cb0959UL, "1424953923781206.2")]
    public void Rfc8785_number_vectors(ulong bits, string expected) => Es6Number.FormatBits(bits).ShouldBe(expected);

    [Theory]
    [InlineData(0x7fffffffffffffffUL)]
    [InlineData(0x7ff0000000000000UL)]
    public void NaN_and_infinity_are_not_json(ulong bits) => Should.Throw<CanonicalJsonException>(() => Es6Number.FormatBits(bits));

    [Fact]
    public void Rfc8785_section_3_2_2_sample()
    {
        const string input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        const string expected =
            "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";

        CanonicalJson.Canonicalize(input).ShouldBe(expected);
    }

    [Fact]
    public void Rfc8785_section_3_2_3_sorting_sample()
    {
        const string input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;
        const string expected =
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\",\"ö\":\"Latin Small Letter O With Diaeresis\","
            + "\"€\":\"Euro Sign\",\"😀\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}";

        CanonicalJson.Canonicalize(input).ShouldBe(expected);
    }

    [Fact]
    public void Nested_structures_are_sorted_and_whitespace_free()
    {
        CanonicalJson.Canonicalize("{ \"b\" : [ 1 , { \"z\":1, \"a\":2 } ], \"a\" : \"Ασφάλεια\" }")
            .ShouldBe("{\"a\":\"Ασφάλεια\",\"b\":[1,{\"a\":2,\"z\":1}]}");
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("\"\\ud800\"")]
    [InlineData("1e400")]
    [InlineData("[1,]")]
    [InlineData("{'a':1}")]
    public void Non_ijson_input_is_rejected(string json) => Should.Throw<CanonicalJsonException>(() => CanonicalJson.Canonicalize(json));

    [Fact]
    public void Hash_is_independent_of_member_order_and_formatting() =>
        CanonicalJson.Hash("{\"a\":1,\"b\":[true,null]}").ShouldBe(CanonicalJson.Hash("{ \"b\": [true, null], \"a\": 1.0 }"));

    /// <summary>The shortest round-tripping digits equal those of .NET's own shortest formatting ("R").</summary>
    [Property(Arbitrary = [typeof(Generators)], MaxTest = 20000)]
    public bool Shortest_digits_match_the_runtime(DoubleBits value)
    {
        var mine = Normalise(Es6Number.FormatBits(value.Bits));
        var runtime = Normalise(BitConverter.Int64BitsToDouble((long)value.Bits).ToString("R", CultureInfo.InvariantCulture));
        return mine == runtime;
    }

    /// <summary>Parsing number text rounds to the same binary64 as the runtime parser (round-half-even).</summary>
    [Property(Arbitrary = [typeof(Generators)], MaxTest = 20000)]
    public bool Parsing_matches_the_runtime(DoubleBits value)
    {
        var number = Math.Abs(BitConverter.Int64BitsToDouble((long)value.Bits));
        var text = number.ToString("E25", CultureInfo.InvariantCulture);
        var (mantissa, exponent) = Es6Number.Parse(text);
        var expected = BitConverter.DoubleToInt64Bits(double.Parse(text, CultureInfo.InvariantCulture));
        return ToBits(mantissa, exponent) == expected;
    }

    private static long ToBits(BigInteger mantissa, int exponent)
    {
        if (mantissa.IsZero)
        {
            return 0;
        }

        var normal = mantissa >= (BigInteger.One << 52);
        var field = normal ? exponent + 1075 : 0;
        return ((long)field << 52) | (long)(mantissa & ((BigInteger.One << 52) - 1));
    }

    /// <summary>(sign, significant digits, decimal exponent of the first digit) of a number text.</summary>
    private static (bool Negative, string Digits, int Exponent) Normalise(string text)
    {
        var negative = text.StartsWith('-');
        text = text.TrimStart('-');
        var exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var integerDigits = point < 0 ? text.Length : point;
        var digits = text.Replace(".", string.Empty, StringComparison.Ordinal);
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        if (digits.Length == 0)
        {
            return (false, "0", 0);
        }

        return (negative, digits, exponent + integerDigits - leading - 1);
    }
}
