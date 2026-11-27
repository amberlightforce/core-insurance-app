using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoreIns.Testing.Contracts;

/// <summary>Volumes of a simulated legacy extract. Keep test volumes small; real-volume rehearsals are not run here (D-ARC-09).</summary>
public sealed record LegacySimulatorOptions
{
    /// <summary>Seed: the same options always produce byte-identical output.</summary>
    public int Seed { get; init; } = 1;

    public int Parties { get; init; } = 20;

    public int Policies { get; init; } = 30;

    public int Claims { get; init; } = 10;

    public int Treaties { get; init; } = 2;

    /// <summary>Deliberate defects to exercise data quality and reconciliation. None by default.</summary>
    public LegacyDefects Defects { get; init; } = new();
}

/// <summary>How many deliberate defects to plant (each count is a number of records, capped by what exists).</summary>
public sealed record LegacyDefects
{
    /// <summary>Records with a missing mandatory field, a bad AFM or IBAN check digit, or an impossible date.</summary>
    public int MalformedRows { get; init; }

    /// <summary>Extra persons repeating an existing person's AFM, name and birth date under a new legacy key.</summary>
    public int DuplicateRows { get; init; }

    /// <summary>Policies whose totalPremium is not net premium plus taxes plus fees; the control total keeps the intended figure.</summary>
    public int SumMismatches { get; init; }
}

/// <summary>One simulated legacy record. <c>Data</c> is flat (string, number) so it also fits a CSV row.</summary>
public sealed record SimRecord(string ObjectType, string LegacyKey, JsonObject Data);

/// <summary>The legacy system's own control figure of one object type (count and the sum of its amount field), as a legacy control report would state it.</summary>
public sealed record SimControlTotal(string ObjectType, int Count, decimal Amount);

/// <summary>A simulated extract plus its control totals and the defects planted.</summary>
public sealed record LegacyExtract(string Source, IReadOnlyList<SimRecord> Records, IReadOnlyList<SimControlTotal> ControlTotals, IReadOnlyList<string> PlantedDefects)
{
    /// <summary>The extract as JSON in the staging shape: <c>{"records":[{"objectType","legacyKey","data":{}}]}</c>.</summary>
    public string ToJson()
    {
        var records = new JsonArray();
        foreach (var r in Records)
        {
            records.Add(new JsonObject { ["objectType"] = r.ObjectType, ["legacyKey"] = r.LegacyKey, ["data"] = JsonNode.Parse(r.Data.ToJsonString()) });
        }

        return new JsonObject { ["source"] = Source, ["records"] = records }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>The records of one object type as CSV (header: objectType, legacyKey, then the union of the data fields in first-seen order).</summary>
    public string ToCsv(string objectType)
    {
        var rows = Records.Where(r => r.ObjectType == objectType).ToList();
        var columns = new List<string>();
        foreach (var field in rows.SelectMany(r => r.Data.Select(p => p.Key)))
        {
            if (!columns.Contains(field))
            {
                columns.Add(field);
            }
        }

        var sb = new StringBuilder("objectType,legacyKey");
        foreach (var column in columns)
        {
            sb.Append(',').Append(Escape(column));
        }

        sb.Append('\n');
        foreach (var r in rows)
        {
            sb.Append(Escape(r.ObjectType)).Append(',').Append(Escape(r.LegacyKey));
            foreach (var column in columns)
            {
                var value = r.Data[column];
                sb.Append(',').Append(value is null ? string.Empty : Escape(value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString()));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Escape(string value) => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

/// <summary>
/// Generates synthetic Greek legacy extracts (BO-MIG-02): parties, policies, claims and treaties, deterministic from a seed.
/// Everything is fictitious. AFM and IBAN values are checksum-valid so they pass format rules, but they are built from random digits
/// (IBAN bank code 999 is not a real bank) and belong to nobody. Never put real personal data here.
/// </summary>
public static class LegacyExtractSimulator
{
    private static readonly string[] GivenNames = ["Νικόλαος", "Ελένη", "Γεώργιος", "Μαρία", "Δημήτριος", "Αικατερίνη", "Ιωάννης", "Σοφία", "Κωνσταντίνος", "Αναστασία", "Παναγιώτης", "Χριστίνα"];
    private static readonly string[] FamilyNames = ["Παπαδόπουλος", "Νικολάου", "Γεωργίου", "Ιωάννου", "Δημητρίου", "Αντωνίου", "Μακρής", "Βλάχος", "Καρράς", "Ζερβός"];
    private static readonly string[] Streets = ["Οδός Δοκιμής", "Λεωφόρος Παραδείγματος", "Οδός Πλατάνων", "Οδός Ελιάς", "Οδός Θαλάσσης"];
    private static readonly string[] Cities = ["Αθήνα", "Θεσσαλονίκη", "Πάτρα", "Ηράκλειο", "Λάρισα"];
    private static readonly string[] PlateLetters = ["A", "B", "E", "Z", "H", "I", "K", "M", "N", "O", "P", "T", "Y", "X"];

    /// <summary>Builds the extract for the source system code.</summary>
    public static LegacyExtract Generate(string source, LegacySimulatorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(options);
        var rng = new SplitMix(unchecked((ulong)options.Seed));
        var persons = new List<SimRecord>();
        var policies = new List<SimRecord>();
        var claims = new List<SimRecord>();
        var treaties = new List<SimRecord>();
        var baseDate = new DateOnly(2026, 1, 1);

        for (var i = 1; i <= options.Parties; i++)
        {
            persons.Add(new SimRecord("Person", $"P{i:D6}", new JsonObject
            {
                ["givenNames"] = Pick(rng, GivenNames),
                ["familyName"] = Pick(rng, FamilyNames),
                ["fatherName"] = Pick(rng, GivenNames),
                ["birthDate"] = baseDate.AddDays(-(18 * 365 + (int)rng.Next(0, 50 * 365))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["afm"] = Afm(rng),
                ["iban"] = Iban(rng),
                ["street"] = Pick(rng, Streets),
                ["streetNumber"] = (1 + (int)rng.Next(0, 150)).ToString(CultureInfo.InvariantCulture),
                ["postalCode"] = (10000 + (int)rng.Next(0, 80000)).ToString(CultureInfo.InvariantCulture),
                ["city"] = Pick(rng, Cities),
                ["email"] = $"test.person{i}@example.invalid",
                ["phone"] = "+3069000" + ((int)rng.Next(0, 100000)).ToString("D5", CultureInfo.InvariantCulture),
            }));
        }

        var policyTotal = 0m;
        for (var i = 1; i <= options.Policies && options.Parties > 0; i++)
        {
            var net = (5000 + (long)rng.Next(0, 95000)) / 100m;
            var taxes = Math.Round(net * 0.15m, 2);
            var fees = (100 + (long)rng.Next(0, 900)) / 100m;
            var total = net + taxes + fees;
            var start = baseDate.AddDays((int)rng.Next(0, 300));
            policies.Add(new SimRecord("Policy", $"POL{i:D6}", new JsonObject
            {
                ["partyKey"] = $"P{1 + (int)rng.Next(0, options.Parties):D6}",
                ["productCode"] = "MOTOR",
                ["plate"] = Plate(rng),
                ["startDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["endDate"] = start.AddYears(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["currency"] = "EUR",
                ["netPremium"] = Money(net),
                ["taxes"] = Money(taxes),
                ["fees"] = Money(fees),
                ["totalPremium"] = Money(total),
            }));
            policyTotal += total;
        }

        var claimTotal = 0m;
        for (var i = 1; i <= options.Claims && options.Policies > 0; i++)
        {
            var reported = (20000 + (long)rng.Next(0, 480000)) / 100m;
            claims.Add(new SimRecord("Claim", $"CLM{i:D6}", new JsonObject
            {
                ["policyKey"] = $"POL{1 + (int)rng.Next(0, options.Policies):D6}",
                ["lossDate"] = baseDate.AddDays(60 + (int)rng.Next(0, 240)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["currency"] = "EUR",
                ["reportedAmount"] = Money(reported),
            }));
            claimTotal += reported;
        }

        for (var i = 1; i <= options.Treaties; i++)
        {
            treaties.Add(new SimRecord("Treaty", $"TRT{i:D4}", new JsonObject
            {
                ["name"] = $"Σύμβαση δοκιμής {i}",
                ["treatyType"] = i % 2 == 0 ? "SURPLUS" : "QUOTA_SHARE",
                ["cessionPercent"] = (20 + 10 * (int)rng.Next(0, 5)).ToString(CultureInfo.InvariantCulture),
                ["startDate"] = baseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }));
        }

        var control = new List<SimControlTotal>
        {
            new("Person", persons.Count, 0m),
            new("Policy", policies.Count, policyTotal),
            new("Claim", claims.Count, claimTotal),
            new("Treaty", treaties.Count, 0m),
        };
        var planted = new List<string>();
        PlantDefects(options.Defects, rng, persons, policies, claims, planted);

        return new LegacyExtract(source, [.. persons, .. policies, .. claims, .. treaties], control, planted);
    }

    private static void PlantDefects(LegacyDefects defects, SplitMix rng, List<SimRecord> persons, List<SimRecord> policies, List<SimRecord> claims, List<string> planted)
    {
        // Sum mismatches first, so a malformed row never hides one.
        foreach (var index in Distinct(rng, policies.Count, defects.SumMismatches))
        {
            var data = policies[index].Data;
            var total = decimal.Parse(data["totalPremium"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            data["totalPremium"] = Money(total + 1m + (int)rng.Next(0, 9));
            planted.Add($"SUM_MISMATCH:{policies[index].LegacyKey}");
        }

        var candidates = persons.Concat(policies).Concat(claims).ToList();
        var variant = 0;
        foreach (var index in Distinct(rng, candidates.Count, defects.MalformedRows))
        {
            var record = candidates[index];
            var data = record.Data;
            string kind;
            switch (record.ObjectType, variant++ % 3)
            {
                case ("Person", 0):
                    data.Remove("familyName");
                    kind = "MISSING_FAMILY_NAME";
                    break;
                case ("Person", 1):
                    var afm = data["afm"]!.GetValue<string>();
                    data["afm"] = afm[..8] + ((afm[8] - '0' + 1) % 10).ToString(CultureInfo.InvariantCulture);
                    kind = "BAD_AFM";
                    break;
                case ("Person", _):
                    var iban = data["iban"]!.GetValue<string>();
                    data["iban"] = iban[..^1] + ((iban[^1] - '0' + 1) % 10).ToString(CultureInfo.InvariantCulture);
                    kind = "BAD_IBAN";
                    break;
                case ("Policy", 0):
                    data.Remove("startDate");
                    kind = "MISSING_START_DATE";
                    break;
                case ("Policy", _):
                    data["endDate"] = "31/02/2027";
                    kind = "BAD_DATE";
                    break;
                default:
                    data["reportedAmount"] = "not-a-number";
                    kind = "BAD_AMOUNT";
                    break;
            }

            planted.Add($"{kind}:{record.LegacyKey}");
        }

        var duplicates = new List<SimRecord>();
        foreach (var index in Distinct(rng, persons.Count, defects.DuplicateRows))
        {
            var original = persons[index];
            var copy = JsonNode.Parse(original.Data.ToJsonString())!.AsObject();
            var key = $"P{persons.Count + duplicates.Count + 1:D6}";
            copy["email"] = $"test.duplicate.{key}@example.invalid";
            duplicates.Add(new SimRecord("Person", key, copy));
            planted.Add($"DUPLICATE_OF:{original.LegacyKey}:{key}");
        }

        persons.AddRange(duplicates);
    }

    private static IEnumerable<int> Distinct(SplitMix rng, int population, int count)
    {
        var pool = Enumerable.Range(0, population).ToList();
        for (var i = 0; i < Math.Min(count, population); i++)
        {
            var at = i + (int)rng.Next(0, pool.Count - i);
            (pool[i], pool[at]) = (pool[at], pool[i]);
            yield return pool[i];
        }
    }

    private static string Pick(SplitMix rng, string[] values) => values[(int)rng.Next(0, values.Length)];

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Plate(SplitMix rng) =>
        Pick(rng, PlateLetters) + Pick(rng, PlateLetters) + Pick(rng, PlateLetters) + "-" + ((int)rng.Next(1000, 10000)).ToString(CultureInfo.InvariantCulture);

    /// <summary>A nine-digit AFM whose check digit is valid: d9 = ((sum of d_i x 2^(9-i), i = 1..8) mod 11) mod 10.</summary>
    public static string Afm(SplitMix rng)
    {
        var digits = new char[9];
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            var d = i == 0 ? 1 + (int)rng.Next(0, 9) : (int)rng.Next(0, 10);
            digits[i] = (char)('0' + d);
            sum += d * (1 << (8 - i));
        }

        digits[8] = (char)('0' + (sum % 11 % 10));
        return new string(digits);
    }

    /// <summary>A 27-character Greek IBAN (GR + check digits + fictitious bank code 999 + 16 random digits) with a valid ISO 7064 mod 97-10 check.</summary>
    public static string Iban(SplitMix rng)
    {
        var bban = new StringBuilder("999");
        for (var i = 0; i < 20; i++)
        {
            bban.Append((char)('0' + (int)rng.Next(0, 10)));
        }

        // GR = 16 27; the rearranged number is bban + "1627" + "00".
        var remainder = (int)(BigInteger.Parse(bban + "162700", CultureInfo.InvariantCulture) % 97);
        return "GR" + (98 - remainder).ToString("D2", CultureInfo.InvariantCulture) + bban;
    }

    /// <summary>Small deterministic generator (SplitMix64); <see cref="Random"/> is not stable across runtime versions.</summary>
    public sealed class SplitMix(ulong seed)
    {
        private ulong _state = seed;

        /// <summary>A value in [min, max).</summary>
        public long Next(long min, long max)
        {
            if (max <= min)
            {
                return min;
            }

            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return min + (long)(z % (ulong)(max - min));
        }
    }
}
