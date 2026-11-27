using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace CoreIns.Modules.Migration.Services.Quality;

/// <summary>One rule of the library: code, the object type it applies to, kind and severity.</summary>
internal sealed record DqRuleDef(string Code, string ObjectType, string Kind, string Severity, string Description);

/// <summary>A rule violation before it is stored.</summary>
internal sealed record DqDraft(int Index, string RuleCode, string Severity, string? Field, string Message);

/// <summary>A staged record read for rule evaluation. <see cref="Data"/> is the record's <c>data</c> object (empty when the payload is not an object).</summary>
internal sealed record DqRecord(Guid RecordId, int Seq, string ObjectType, string LegacyKey, JsonElement Data)
{
    /// <summary>The field as text: strings as is, numbers and booleans as written, null for a missing or null field.</summary>
    public string? Text(string field)
    {
        if (Data.ValueKind != JsonValueKind.Object || !Data.TryGetProperty(field, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
    }
}

/// <summary>
/// The versioned data quality rule library (BO-MIG-03). Rules read staged records and never change them. Bump <see cref="Version"/> whenever a rule's
/// logic or severity changes: findings are stored per rule-set version, so an earlier run stays explainable.
/// </summary>
internal static class DqRules
{
    public const int Version = 1;

    public const string Error = "ERROR";
    public const string Warning = "WARNING";

    public const string Mandatory = "REQ.MANDATORY";
    public const string Afm = "FMT.AFM";
    public const string Iban = "FMT.IBAN";
    public const string Plate = "FMT.PLATE";
    public const string DateFormat = "FMT.DATE";
    public const string AmountFormat = "FMT.AMOUNT";
    public const string DateRange = "RNG.DATE";
    public const string AmountRange = "RNG.AMOUNT";
    public const string PercentRange = "RNG.PERCENT";
    public const string PremiumSum = "SUM.PREMIUM";
    public const string PolicyParty = "REF.POLICY_PARTY";
    public const string ClaimPolicy = "REF.CLAIM_POLICY";
    public const string DuplicateAfm = "DUP.AFM";
    public const string DuplicateKey = "DUP.KEY";

    public const string Person = "Person";
    public const string Policy = "Policy";
    public const string Claim = "Claim";
    public const string Treaty = "Treaty";

    private static readonly Dictionary<string, string[]> MandatoryFields = new(StringComparer.Ordinal)
    {
        [Person] = ["givenNames", "familyName", "birthDate", "afm"],
        [Policy] = ["partyKey", "startDate", "endDate", "netPremium", "totalPremium"],
        [Claim] = ["policyKey", "lossDate", "reportedAmount"],
        [Treaty] = ["treatyType", "cessionPercent"],
    };

    private static readonly string[] DateFields = ["birthDate", "startDate", "endDate", "lossDate"];
    private static readonly string[] AmountFields = ["netPremium", "taxes", "fees", "totalPremium", "reportedAmount"];

    private static readonly DateOnly EarliestDate = new(1900, 1, 1);
    private static readonly DateOnly LatestDate = new(2100, 12, 31);

    /// <summary>The catalogue of the rule set (stored in <c>mig.dq_rule</c> when a run first uses this version).</summary>
    public static IReadOnlyList<DqRuleDef> Catalogue { get; } =
    [
        .. MandatoryFields.Select(m => new DqRuleDef(Mandatory, m.Key, "mandatory", Error, $"{m.Key}: {string.Join(", ", m.Value)} must be present and not blank")),
        new(Afm, Person, "format", Error, "AFM is nine digits with a valid mod-11 check digit"),
        new(Iban, Person, "format", Error, "IBAN has a valid length and ISO 7064 mod 97-10 check"),
        new(Plate, Policy, "format", Warning, "Plate looks like a Greek plate (2 or 3 letters, a dash, 3 or 4 digits)"),
        new(DateFormat, "*", "format", Error, "Date fields are valid calendar dates written yyyy-MM-dd"),
        new(AmountFormat, "*", "format", Error, "Amount fields are decimal numbers with a point"),
        new(DateRange, "*", "range", Error, "Dates lie between 1900 and 2100, a birth date is not in the future, a policy ends on or after it starts"),
        new(AmountRange, "*", "range", Error, "Amounts are not negative"),
        new(PercentRange, Treaty, "range", Error, "A cession percent is above 0 and at most 100"),
        new(PremiumSum, Policy, "sum", Error, "totalPremium equals netPremium + taxes + fees exactly (no rounding is applied to legacy figures)"),
        new(PolicyParty, Policy, "referential", Error, "partyKey is a Person of the same snapshot"),
        new(ClaimPolicy, Claim, "referential", Error, "policyKey is a Policy of the same snapshot"),
        new(DuplicateAfm, Person, "duplicate", Error, "No two persons of a snapshot share an AFM (the later record is reported)"),
        new(DuplicateKey, "*", "duplicate", Error, "A legacy key is used once per object type in a snapshot"),
    ];

    /// <summary>True for the fields the profile reports a minimum and maximum of.</summary>
    public static bool IsDateField(string field) => Array.IndexOf(DateFields, field) >= 0;

    public static bool IsAmountField(string field) => Array.IndexOf(AmountFields, field) >= 0 || field == "cessionPercent";

    public static bool TryDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool TryAmount(string? text, out decimal amount) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);

    /// <summary>AFM: nine digits, check digit d9 = ((sum of d_i x 2^(9-i), i = 1..8) mod 11) mod 10.</summary>
    public static bool AfmValid(string afm)
    {
        if (afm.Length != 9 || !afm.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (afm[i] - '0') * (1 << (8 - i));
        }

        return sum % 11 % 10 == afm[8] - '0';
    }

    /// <summary>IBAN: 15 to 34 letters and digits, country code, ISO 7064 mod 97-10 equals 1. Spaces are not accepted.</summary>
    public static bool IbanValid(string iban)
    {
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsAsciiLetter(c) ? (char.ToUpperInvariant(c) - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        return BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97 == 1;
    }

    public static bool PlateValid(string plate)
    {
        var dash = plate.IndexOf('-', StringComparison.Ordinal);
        if (dash is not (2 or 3))
        {
            return false;
        }

        var letters = plate[..dash];
        var digits = plate[(dash + 1)..];
        return letters.All(char.IsAsciiLetterUpper) && digits.Length is 3 or 4 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>Runs every rule over the records of a snapshot. <paramref name="today"/> is the run date (a birth date after it is an error).</summary>
    public static List<DqDraft> Evaluate(IReadOnlyList<DqRecord> records, DateOnly today)
    {
        var drafts = new List<DqDraft>();
        var persons = new HashSet<string>(StringComparer.Ordinal);
        var policies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.ObjectType == Person)
            {
                persons.Add(r.LegacyKey);
            }
            else if (r.ObjectType == Policy)
            {
                policies.Add(r.LegacyKey);
            }
        }

        var seenKeys = new HashSet<(string, string)>();
        var firstAfm = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (!seenKeys.Add((r.ObjectType, r.LegacyKey)))
            {
                drafts.Add(new DqDraft(i, DuplicateKey, Error, null, $"The legacy key {r.LegacyKey} is used by more than one {r.ObjectType} record."));
            }

            CheckMandatory(r, i, drafts);
            Formats(r, i, drafts);
            Ranges(r, i, drafts, today);
            switch (r.ObjectType)
            {
                case Person:
                    var afm = r.Text("afm")?.Trim();
                    if (!string.IsNullOrEmpty(afm) && !firstAfm.TryAdd(afm, r.LegacyKey))
                    {
                        drafts.Add(new DqDraft(i, DuplicateAfm, Error, "afm", $"The AFM is already used by person {firstAfm[afm]}."));
                    }

                    break;
                case Policy:
                    Premium(r, i, drafts);
                    var party = r.Text("partyKey");
                    if (!string.IsNullOrWhiteSpace(party) && !persons.Contains(party.Trim()))
                    {
                        drafts.Add(new DqDraft(i, PolicyParty, Error, "partyKey", $"partyKey {party} is not a person of this snapshot."));
                    }

                    break;
                case Claim:
                    var policy = r.Text("policyKey");
                    if (!string.IsNullOrWhiteSpace(policy) && !policies.Contains(policy.Trim()))
                    {
                        drafts.Add(new DqDraft(i, ClaimPolicy, Error, "policyKey", $"policyKey {policy} is not a policy of this snapshot."));
                    }

                    break;
                default:
                    break;
            }
        }

        return drafts;
    }

    private static void CheckMandatory(DqRecord r, int i, List<DqDraft> drafts)
    {
        if (!MandatoryFields.TryGetValue(r.ObjectType, out var fields))
        {
            return;
        }

        foreach (var field in fields.Where(f => string.IsNullOrWhiteSpace(r.Text(f))))
        {
            drafts.Add(new DqDraft(i, Mandatory, Error, field, $"{field} is missing or blank."));
        }
    }

    private static void Formats(DqRecord r, int i, List<DqDraft> drafts)
    {
        if (r.ObjectType == Person)
        {
            var afm = r.Text("afm");
            if (!string.IsNullOrWhiteSpace(afm) && !AfmValid(afm.Trim()))
            {
                drafts.Add(new DqDraft(i, Afm, Error, "afm", "The AFM is not nine digits with a valid check digit."));
            }

            var iban = r.Text("iban");
            if (!string.IsNullOrWhiteSpace(iban) && !IbanValid(iban.Trim()))
            {
                drafts.Add(new DqDraft(i, Iban, Error, "iban", "The IBAN fails the mod 97 check."));
            }
        }

        if (r.ObjectType == Policy)
        {
            var plate = r.Text("plate");
            if (!string.IsNullOrWhiteSpace(plate) && !PlateValid(plate.Trim()))
            {
                drafts.Add(new DqDraft(i, Plate, Warning, "plate", $"The plate {plate} does not look like a Greek plate."));
            }
        }

        foreach (var field in DateFields)
        {
            var text = r.Text(field);
            if (!string.IsNullOrWhiteSpace(text) && !TryDate(text.Trim(), out _))
            {
                drafts.Add(new DqDraft(i, DateFormat, Error, field, $"{field} '{text}' is not a valid yyyy-MM-dd date."));
            }
        }

        foreach (var field in AmountFields.Append("cessionPercent"))
        {
            var text = r.Text(field);
            if (!string.IsNullOrWhiteSpace(text) && !TryAmount(text.Trim(), out _))
            {
                drafts.Add(new DqDraft(i, AmountFormat, Error, field, $"{field} '{text}' is not a decimal number."));
            }
        }
    }

    private static void Ranges(DqRecord r, int i, List<DqDraft> drafts, DateOnly today)
    {
        foreach (var field in DateFields)
        {
            if (!TryDate(r.Text(field)?.Trim(), out var date))
            {
                continue;
            }

            if (date < EarliestDate || date > LatestDate)
            {
                drafts.Add(new DqDraft(i, DateRange, Error, field, $"{field} {date:yyyy-MM-dd} is outside 1900 to 2100."));
            }
            else if (field == "birthDate" && date > today)
            {
                drafts.Add(new DqDraft(i, DateRange, Error, field, "The birth date is in the future."));
            }
        }

        if (r.ObjectType == Policy && TryDate(r.Text("startDate")?.Trim(), out var start) && TryDate(r.Text("endDate")?.Trim(), out var end) && end < start)
        {
            drafts.Add(new DqDraft(i, DateRange, Error, "endDate", "The policy ends before it starts."));
        }

        foreach (var field in AmountFields)
        {
            if (TryAmount(r.Text(field)?.Trim(), out var amount) && amount < 0)
            {
                drafts.Add(new DqDraft(i, AmountRange, Error, field, $"{field} is negative."));
            }
        }

        if (r.ObjectType == Treaty && TryAmount(r.Text("cessionPercent")?.Trim(), out var percent) && (percent <= 0 || percent > 100))
        {
            drafts.Add(new DqDraft(i, PercentRange, Error, "cessionPercent", "The cession percent must be above 0 and at most 100."));
        }
    }

    /// <summary>Net + taxes + fees must equal the total. Missing taxes or fees count as zero; an unparsable figure is a format finding, not a sum finding.</summary>
    // TODO(hardening): legacy figures are compared exactly; route a tolerance through the MKT rounding rule (DecimalRounding) if a source rounds per line.
    private static void Premium(DqRecord r, int i, List<DqDraft> drafts)
    {
        if (!TryAmount(r.Text("netPremium")?.Trim(), out var net) || !TryAmount(r.Text("totalPremium")?.Trim(), out var total))
        {
            return;
        }

        var taxes = 0m;
        var fees = 0m;
        if ((!string.IsNullOrWhiteSpace(r.Text("taxes")) && !TryAmount(r.Text("taxes")!.Trim(), out taxes)) || (!string.IsNullOrWhiteSpace(r.Text("fees")) && !TryAmount(r.Text("fees")!.Trim(), out fees)))
        {
            return;
        }

        if (net + taxes + fees != total)
        {
            drafts.Add(new DqDraft(i, PremiumSum, Error, "totalPremium", $"totalPremium {total.ToString("0.00", CultureInfo.InvariantCulture)} is not net {net.ToString("0.00", CultureInfo.InvariantCulture)} + taxes {taxes.ToString("0.00", CultureInfo.InvariantCulture)} + fees {fees.ToString("0.00", CultureInfo.InvariantCulture)}."));
        }
    }
}
