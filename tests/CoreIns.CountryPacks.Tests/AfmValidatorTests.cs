using CoreIns.CountryPacks.GR;
using CoreIns.CountryPacks.GR.Identifiers;
using CoreIns.Modules.Market.Contracts.Spi;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CoreIns.CountryPacks.Tests;

/// <summary>AFM rules of REQ-PTY-049 / REQ-MKT-260 with the PRD-01 §14.1 vectors.</summary>
public sealed class AfmValidatorTests
{
    private static readonly IdScheme Afm = new(GrSchemes.Afm);
    private static readonly IdScheme Vat = new(GrSchemes.Vat);
    private readonly GreekIdValidator _validator = new();

    [Theory]
    [InlineData("123456783")]
    [InlineData("111111114")]
    [InlineData("800000002")]
    [InlineData("100000090")] // check value 10 → 0
    [InlineData("090000045")] // REQ-MKT-260 acceptance
    [InlineData("123 456 783")] // display grouping is accepted and normalised
    public async Task PRD_01_valid_vectors_are_valid(string value)
    {
        var result = await _validator.ValidateAsync(Afm, value, IdValidationContext.None, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(IdValidationStatus.Valid);
        result.Errors.ShouldBeEmpty();
        result.Normalised.ShouldBe(value.Replace(" ", string.Empty, StringComparison.Ordinal));
        result.ValidatorVersion.ShouldBe(GrPack.IdValidatorVersion);
    }

    [Theory]
    [InlineData("123456789", IdValidationErrorCodes.CheckDigit)] // wrong check digit (REQ-MKT-090 acceptance)
    [InlineData("000000000", IdValidationErrorCodes.Format)] // all zeros
    [InlineData("12345678", IdValidationErrorCodes.Length)] // 8 digits
    [InlineData("1234567830", IdValidationErrorCodes.Length)] // 10 digits
    [InlineData("12345678A", IdValidationErrorCodes.Format)] // non-digit
    [InlineData("EL123456783", IdValidationErrorCodes.Format)] // EL prefix only within the VAT scheme
    [InlineData("", IdValidationErrorCodes.Format)]
    [InlineData("١٢٣٤٥٦٧٨٣", IdValidationErrorCodes.Format)] // non-ASCII digits are not AFM digits
    public async Task PRD_01_invalid_vectors_are_invalid_with_their_code(string value, string code)
    {
        var result = await _validator.ValidateAsync(Afm, value, IdValidationContext.None, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(IdValidationStatus.Invalid);
        result.Errors.ShouldHaveSingleItem().ShouldBe(new SpiError(SpiErrorCategory.Validation, code));
    }

    [Fact]
    public async Task VAT_with_EL_prefix_is_valid_and_GR_is_corrected_to_EL()
    {
        var el = await _validator.ValidateAsync(Vat, "EL123456783", IdValidationContext.None, TestContext.Current.CancellationToken);
        el.Status.ShouldBe(IdValidationStatus.Valid);
        el.Normalised.ShouldBe("EL123456783");
        el.Findings.ShouldBeEmpty();

        var gr = await _validator.ValidateAsync(Vat, "GR123456783", IdValidationContext.None, TestContext.Current.CancellationToken);
        gr.Status.ShouldBe(IdValidationStatus.Valid);
        gr.Normalised.ShouldBe("EL123456783");
        gr.Findings.ShouldBe([GreekIdValidator.PrefixCorrected]);

        var wrong = await _validator.ValidateAsync(Vat, "EL123456789", IdValidationContext.None, TestContext.Current.CancellationToken);
        wrong.Status.ShouldBe(IdValidationStatus.Invalid);
        wrong.Errors.ShouldHaveSingleItem().Code.ShouldBe(IdValidationErrorCodes.CheckDigit);
    }

    /// <summary>Property: appending the computed check digit to any eight digits (not all zero) always gives a valid AFM.</summary>
    [Property(MaxTest = 500)]
    public Property Computed_check_digit_always_validates() =>
        Prop.ForAll(EightDigits(), digits =>
        {
            var afm = digits + Afm_CheckDigit(digits);
            return (afm.Any(digit => digit != '0') == (GR.Identifiers.Afm.Check(afm) is null)).Label(afm);
        });

    /// <summary>Property: for any nine digits the validator agrees with an independent oracle of the REQ-PTY-049 formula.</summary>
    [Property(MaxTest = 1000)]
    public Property Validator_agrees_with_an_independent_oracle_of_the_formula() =>
        Prop.ForAll(NineDigitsBiasedToValid(), afm => ((GR.Identifiers.Afm.Check(afm) is null) == Oracle(afm)).Label(afm));

    /// <summary>
    /// Property: changing one of the first eight digits of a valid AFM is detected, except when the mod-11 remainder
    /// moves between 0 and 10, which "mod 10" maps to the same check digit (a known limit of the formula).
    /// </summary>
    [Property(MaxTest = 500)]
    public Property Single_digit_changes_are_detected_except_the_10_to_0_collapse() =>
        Prop.ForAll(EightDigits(), Gen.Choose(0, 7).ToArbitrary(), Gen.Choose(1, 9).ToArbitrary(), (digits, position, delta) =>
        {
            var valid = digits + Afm_CheckDigit(digits);
            var changed = valid.ToCharArray();
            changed[position] = (char)('0' + ((changed[position] - '0' + delta) % 10));
            var mutated = new string(changed);

            var before = WeightedSumMod11(valid);
            var after = WeightedSumMod11(mutated);
            var collapse = (before, after) is (0, 10) or (10, 0);
            var accepted = GR.Identifiers.Afm.Check(mutated) is null;
            return (accepted == (collapse && mutated.Any(digit => digit != '0'))).Label($"{valid} → {mutated}");
        });

    [Fact]
    public void Check_digit_formula_matches_the_PRD_worked_example()
    {
        // 1·2^8 + 0 + … + 9·2^1 = 274; 274 mod 11 = 10; 10 mod 10 = 0 (PRD-01 §14.1 "check value 10 → 0").
        GR.Identifiers.Afm.CheckDigit("10000009").ShouldBe(0);
    }

    private static int Afm_CheckDigit(string eightDigits) => GR.Identifiers.Afm.CheckDigit(eightDigits);

    /// <summary>Σ_{i=1..8} d_i × 2^(9−i) mod 11, written independently of the implementation.</summary>
    private static int WeightedSumMod11(string afm)
    {
        var sum = 0;
        var weight = 256;
        for (var i = 0; i < 8; i++)
        {
            sum += (afm[i] - '0') * weight;
            weight /= 2;
        }

        return sum % 11;
    }

    private static bool Oracle(string afm) =>
        afm.Length == 9 && afm.All(char.IsAsciiDigit) && afm != "000000000" && WeightedSumMod11(afm) % 10 == afm[8] - '0';

    /// <summary>Nine digits; half of the samples get a correct check digit so both outcomes are exercised.</summary>
    private static Arbitrary<string> NineDigitsBiasedToValid() =>
        Gen.Zip(Gen.ArrayOf(Gen.Choose(0, 9), 9), Gen.Elements(true, false))
            .Select(pair =>
            {
                var digits = string.Concat(pair.Item1);
                return pair.Item2 ? digits[..8] + (WeightedSumMod11(digits) % 10) : digits;
            })
            .ToArbitrary();

    private static Arbitrary<string> EightDigits() =>
        Gen.ArrayOf(Gen.Choose(0, 9), 8).Select(digits => string.Concat(digits)).ToArbitrary();
}
