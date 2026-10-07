using FsCheck;
using FsCheck.Fluent;

namespace CoreIns.SharedKernel.Tests;

/// <summary>A random IEEE 754 binary64 bit pattern over the whole 64-bit space (finite values only).</summary>
public sealed record DoubleBits(ulong Bits);

/// <summary>Two amounts and a factor in one currency.</summary>
public sealed record MoneyTriple(Money A, Money B, Money C);

/// <summary>FsCheck generators for SharedKernel types.</summary>
public static class Generators
{
    private static readonly string[] Codes = ["EUR", "USD", "JPY", "KWD", "CLF", "GBP"];

    /// <summary>Amounts with up to 6 decimals and |amount| below 10^12 (realistic money, no overflow in sums of three).</summary>
    public static Gen<decimal> Amount() =>
        from units in Gen.Choose(-1_000_000, 1_000_000)
        from millions in Gen.Choose(-1_000_000, 1_000_000)
        from fraction in Gen.Choose(0, 999_999)
        from scale in Gen.Choose(0, 6)
        select Math.Round(((decimal)millions * 1_000_000m) + units + (fraction / 1_000_000m), scale);

    public static Gen<Currency> Currencies() => Gen.Elements(Codes).Select(Currency.FromCode);

    public static Arbitrary<MoneyTriple> MoneyTriples() => Arb.From(
        from currency in Currencies()
         from a in Amount()
         from b in Amount()
         from c in Amount()
         select new MoneyTriple(new Money(a, currency), new Money(b, currency), new Money(c, currency)));

    public static Arbitrary<Money> Monies() => Arb.From(
        from currency in Currencies()
         from a in Amount()
         select new Money(a, currency));

    public static Gen<BusinessDate> Dates() =>
        Gen.Choose(0, 3650).Select(offset => new BusinessDate(2020, 1, 1).AddDays(offset));

    public static Arbitrary<DateRange> DateRanges() => Arb.From(
        from start in Dates()
         from length in Gen.Choose(1, 400)
         from open in Gen.Frequency((1, Gen.Constant(true)), (4, Gen.Constant(false)))
         select open ? DateRange.Open(start) : DateRange.Of(start, start.AddDays(length)));

    public static Arbitrary<DoubleBits> Doubles() => Arb.From(
        from high in Gen.Choose(0, int.MaxValue)
         from low in Gen.Choose(int.MinValue, int.MaxValue)
         from negative in Gen.Elements(false, true)
         let bits = ((ulong)(uint)high << 32 | (uint)low) & 0x7FFF_FFFF_FFFF_FFFFUL
         where (bits >> 52) != 0x7FF
         select new DoubleBits(negative ? bits | 0x8000_0000_0000_0000UL : bits));
}
