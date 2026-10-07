using System.Text;
using CoreIns.CountryPacks.GR.Language;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Identifiers;

/// <summary>
/// Greek plate normalisation, scheme <c>VEHICLE_PLATE</c> (REQ-MKT-339, REQ-MKT-325, PRD-17 §9.4.1 and §10): the plate
/// series uses the Greek letters common to both alphabets, and Latin look-alikes are normalised to the Greek series;
/// spaces, hyphens and separators are removed; case is folded by the Greek language rules (REQ-MKT-340).
/// "ikx-1234" → "ΙΚΧ1234" with finding <c>LOOKALIKE_NORMALISED</c>; "ΙΚΧ1234" → "ΙΚΧ1234".
/// </summary>
/// <remarks>
/// The PRDs state the letter series but no length or digit pattern, so no <c>FORMAT</c> finding is produced and
/// validation returns <c>Unverified</c>; letters outside the series give <c>INVALID_SERIES</c> as a finding (not a
/// blocking error).
/// </remarks>
public static class GreekVehiclePlate
{
    /// <summary>Latin capital look-alikes and the Greek capital of the series they stand for.</summary>
    private static readonly Dictionary<char, char> LatinToGreek = new()
    {
        ['A'] = 'Α', ['B'] = 'Β', ['E'] = 'Ε', ['Z'] = 'Ζ', ['H'] = 'Η', ['I'] = 'Ι', ['K'] = 'Κ',
        ['M'] = 'Μ', ['N'] = 'Ν', ['O'] = 'Ο', ['P'] = 'Ρ', ['T'] = 'Τ', ['Y'] = 'Υ', ['X'] = 'Χ',
    };

    /// <summary>The Greek capitals common to the Greek and Latin alphabets (the plate series letters).</summary>
    public static IReadOnlySet<char> SeriesLetters { get; } = new HashSet<char>(LatinToGreek.Values);

    /// <summary>Normalises a plate (REQ-MKT-339 <c>normalise</c>).</summary>
    public static PlateNormalisationResult Normalise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var upper = GreekCaseMapper.ToUpper(value);
        var plate = new StringBuilder(upper.Length);
        var lookalike = false;
        var invalidSeries = false;

        foreach (var character in upper)
        {
            if (char.IsAsciiDigit(character))
            {
                plate.Append(character);
            }
            else if (LatinToGreek.TryGetValue(character, out var greek))
            {
                plate.Append(greek);
                lookalike = true;
            }
            else if (char.IsLetter(character))
            {
                plate.Append(character);
                invalidSeries |= !SeriesLetters.Contains(character);
            }

            // Anything else (spaces, hyphens, dots, other separators) is removed.
        }

        var findings = new List<string>(2);
        if (lookalike)
        {
            findings.Add(PlateFindings.LookalikeNormalised);
        }

        if (invalidSeries)
        {
            findings.Add(PlateFindings.InvalidSeries);
        }

        return new PlateNormalisationResult(plate.ToString(), findings, GrPack.PlateRuleSetId);
    }

    /// <summary>Search key (REQ-MKT-339 <c>searchKey</c>): the normalised plate, so typed variants share one key.</summary>
    public static string SearchKey(string value) => Normalise(value).Normalised;
}
