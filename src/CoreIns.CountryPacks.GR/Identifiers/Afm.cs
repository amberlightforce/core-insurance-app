using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Identifiers;

/// <summary>
/// AFM (Greek tax identification number) rules of REQ-PTY-049 / BR-PTY-002 / REQ-MKT-260 (contract §3.3): exactly nine
/// digits, not 000000000, check digit d9 = ((Σ_{i=1..8} d_i × 2^(9−i)) mod 11) mod 10. The "EL" prefix is accepted only
/// within the VAT scheme.
/// </summary>
public static class Afm
{
    /// <summary>Length of an AFM.</summary>
    public const int Length = 9;

    /// <summary>Removes white space (spaces, no-break spaces) used for display grouping ("123 456 789").</summary>
    public static string Normalise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
    }

    /// <summary>
    /// Validates an AFM and returns the first error code (<c>FORMAT</c>, <c>LENGTH</c>, <c>CHECK_DIGIT</c>), or null
    /// when valid. The value must already be normalised (<see cref="Normalise"/>).
    /// </summary>
    public static string? Check(string normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);

        if (normalised.Length == 0 || !normalised.All(char.IsAsciiDigit))
        {
            return IdValidationErrorCodes.Format;
        }

        if (normalised.Length != Length)
        {
            return IdValidationErrorCodes.Length;
        }

        if (normalised.All(digit => digit == '0'))
        {
            return IdValidationErrorCodes.Format;
        }

        return CheckDigit(normalised) == normalised[8] - '0' ? null : IdValidationErrorCodes.CheckDigit;
    }

    /// <summary>The check digit of the first eight digits of <paramref name="digits"/> (REQ-PTY-049 formula).</summary>
    public static int CheckDigit(ReadOnlySpan<char> digits)
    {
        if (digits.Length < Length - 1)
        {
            throw new ArgumentException("At least eight digits are required.", nameof(digits));
        }

        var sum = 0;
        for (var position = 0; position < Length - 1; position++)
        {
            var digit = digits[position] - '0';
            if (digit is < 0 or > 9)
            {
                throw new ArgumentException("Digits only.", nameof(digits));
            }

            sum += digit << (Length - 1 - position); // d_i × 2^(9−i), i = position + 1
        }

        return sum % 11 % 10;
    }
}
