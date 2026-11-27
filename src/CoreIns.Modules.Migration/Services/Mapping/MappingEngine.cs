using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Modules.Migration.Services.Quality;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Migration.Services.Mapping;

/// <summary>One field mapping of a spec: the staged source field, a transform and the dotted target path in the importer payload.</summary>
internal sealed record MappingField(string Target, string? Source, string Transform, string? Format, int? Scale, string? CodeMap, string? Value, bool? Required);

internal sealed record MappingCodeEntry(string Legacy, string Core);

internal sealed record MappingCodeMapDef(string Name, IReadOnlyList<MappingCodeEntry> Entries);

internal sealed record MappingIssueView(string Code, string Field, string? Value, string Message);

internal sealed record MappingItemResult(string LegacyKey, string ObjectType, string LegacyId, string ItemHash, JsonNode Payload, IReadOnlyList<MappingIssueView> Issues);

/// <summary>The transforms of a field mapping.</summary>
internal static class MappingTransform
{
    public const string Copy = "COPY";
    public const string Trim = "TRIM";
    public const string Upper = "UPPER";
    public const string Date = "DATE";
    public const string Decimal = "DECIMAL";
    public const string Enum = "ENUM";
    public const string Constant = "CONSTANT";

    public static readonly string[] All = [Copy, Trim, Upper, Date, Decimal, Enum, Constant];
}

/// <summary>Issue codes of a mapped record.</summary>
internal static class MappingIssueCode
{
    public const string UnmappedCode = "UNMAPPED_CODE";
    public const string RequiredMissing = "REQUIRED_MISSING";
    public const string BadDate = "BAD_DATE";
    public const string BadAmount = "BAD_AMOUNT";
}

/// <summary>
/// The mapping engine (BO-MIG-04): pure functions that normalise and validate a spec, hash it and apply it to one staged record. Nothing is written.
/// A value that cannot be mapped (a code without an entry, a bad date or amount) is an issue on the item and the field is left out; it is never guessed.
/// </summary>
internal static partial class MappingEngine
{
    public const string DefaultDateFormat = "yyyy-MM-dd";
    public const int DefaultScale = 2;
    public const int MaxFields = 200;
    public const int MaxCodeEntries = 5000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex Segment();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,80}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex ModuleRegex();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);

    public static bool ValidName(string name) => NameRegex().IsMatch(name);

    /// <summary>Normalises a field (upper-case transform, defaults) and returns the first problem, or null.</summary>
    public static string? Normalise(IReadOnlyList<MappingField>? fields, IReadOnlyList<MappingCodeMapDef>? codeMaps, out List<MappingField> normalFields, out List<MappingCodeMapDef> normalMaps)
    {
        normalFields = [];
        normalMaps = [];
        if (fields is not { Count: > 0 and <= MaxFields })
        {
            return $"Give 1 to {MaxFields} field mappings.";
        }

        var maps = codeMaps ?? [];
        var seenMaps = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var map in maps)
        {
            var name = map.Name?.Trim() ?? string.Empty;
            if (!ValidName(name) || !seenMaps.Add(name))
            {
                return $"Code table '{name}' needs a unique name of letters, digits, dot, underscore or hyphen.";
            }

            if (map.Entries is not { Count: > 0 })
            {
                return $"Code table '{name}' has no entries.";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<MappingCodeEntry>();
            foreach (var entry in map.Entries)
            {
                var legacy = entry.Legacy?.Trim() ?? string.Empty;
                var core = entry.Core?.Trim() ?? string.Empty;
                if (legacy.Length is 0 or > 100 || core.Length is 0 or > 100 || !seen.Add(legacy))
                {
                    return $"Code table '{name}' needs a distinct, non-blank legacy code and a core code (1 to 100 characters) in each entry.";
                }

                entries.Add(new MappingCodeEntry(legacy, core));
            }

            total += entries.Count;
            normalMaps.Add(new MappingCodeMapDef(name, [.. entries.OrderBy(e => e.Legacy, StringComparer.Ordinal)]));
        }

        if (total > MaxCodeEntries)
        {
            return $"At most {MaxCodeEntries} code table entries.";
        }

        normalMaps = [.. normalMaps.OrderBy(m => m.Name, StringComparer.Ordinal)];
        var targets = new List<string>();
        foreach (var f in fields)
        {
            var target = f.Target?.Trim() ?? string.Empty;
            var parts = target.Split('.');
            if (target.Length == 0 || parts.Any(p => !Segment().IsMatch(p)))
            {
                return $"The target path '{target}' is not a dotted path of names.";
            }

            if (targets.Any(t => t == target || t.StartsWith(target + ".", StringComparison.Ordinal) || target.StartsWith(t + ".", StringComparison.Ordinal)))
            {
                return $"The target '{target}' is mapped twice or overlaps another target.";
            }

            targets.Add(target);
            var transform = f.Transform?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!MappingTransform.All.Contains(transform))
            {
                return $"The transform of '{target}' is one of {string.Join(", ", MappingTransform.All)}.";
            }

            var source = f.Source?.Trim();
            if (transform != MappingTransform.Constant && string.IsNullOrEmpty(source))
            {
                return $"The mapping of '{target}' needs a source field.";
            }

            string? format = null;
            int? scale = null;
            string? codeMap = null;
            string? value = null;
            switch (transform)
            {
                case MappingTransform.Date:
                    format = string.IsNullOrWhiteSpace(f.Format) ? DefaultDateFormat : f.Format.Trim();
                    if (format.Length > 32 || !format.Contains('y') || !format.Contains('M') || !format.Contains('d') || !DateOnly.TryParseExact(new DateOnly(2001, 2, 13).ToString(format, CultureInfo.InvariantCulture), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        return $"The date format of '{target}' is not usable.";
                    }

                    break;
                case MappingTransform.Decimal:
                    scale = f.Scale ?? DefaultScale;
                    if (scale is < 0 or > 6)
                    {
                        return $"The scale of '{target}' is 0 to 6.";
                    }

                    break;
                case MappingTransform.Enum:
                    codeMap = f.CodeMap?.Trim();
                    if (codeMap is null || !seenMaps.Contains(codeMap))
                    {
                        return $"The code table of '{target}' is not defined in this spec.";
                    }

                    break;
                case MappingTransform.Constant:
                    value = f.Value;
                    if (string.IsNullOrEmpty(value) || value.Length > 500)
                    {
                        return $"The constant of '{target}' is 1 to 500 characters.";
                    }

                    source = null;
                    break;
            }

            normalFields.Add(new MappingField(target, source, transform, format, scale, codeMap, value, f.Required ?? false));
        }

        return null;
    }

    /// <summary>SHA-256 of the canonical spec content: the same fields and code tables always hash the same, whatever the order the code entries came in.</summary>
    public static string ContentHash(string name, string sourceObjectType, string targetModule, string targetObjectType, IReadOnlyList<MappingField> fields, IReadOnlyList<MappingCodeMapDef> codeMaps)
    {
        var canonical = Serialize(new { name, sourceObjectType, targetModule, targetObjectType, fields, codeMaps });
        return Sha256Hash.ComputeUtf8(canonical).Value;
    }

    /// <summary>Maps one staged record to an import item. Issues are collected per record; the offending fields are omitted from the payload.</summary>
    public static MappingItemResult Apply(DqRecord record, string targetObjectType, IReadOnlyList<MappingField> fields, IReadOnlyDictionary<string, Dictionary<string, string>> codes)
    {
        var payload = new JsonObject();
        var issues = new List<MappingIssueView>();
        foreach (var f in fields)
        {
            string? raw;
            if (f.Transform == MappingTransform.Constant)
            {
                raw = f.Value;
            }
            else
            {
                raw = record.Text(f.Source!);
                if (raw is not null)
                {
                    raw = raw.Trim();
                }
            }

            if (string.IsNullOrEmpty(raw))
            {
                if (f.Required == true)
                {
                    issues.Add(new MappingIssueView(MappingIssueCode.RequiredMissing, f.Target, null, $"The legacy field '{f.Source}' is missing or blank."));
                }

                continue;
            }

            string? output = null;
            switch (f.Transform)
            {
                case MappingTransform.Copy:
                case MappingTransform.Trim:
                case MappingTransform.Constant:
                    output = raw;
                    break;
                case MappingTransform.Upper:
                    output = raw.ToUpperInvariant();
                    break;
                case MappingTransform.Date:
                    if (DateOnly.TryParseExact(raw, f.Format ?? DefaultDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        output = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        issues.Add(new MappingIssueView(MappingIssueCode.BadDate, f.Target, raw, $"'{raw}' is not a date in the format {f.Format ?? DefaultDateFormat}."));
                    }

                    break;
                case MappingTransform.Decimal:
                    var places = f.Scale ?? DefaultScale;

                    // The scale is conformed, never rounded: a value with more places than the scale would lose cents, so it is an issue.
                    if (DqRules.TryAmount(raw, out var amount) && decimal.Round(amount, places, MidpointRounding.ToZero) == amount)
                    {
                        output = decimal.Round(amount, places, MidpointRounding.ToZero).ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        issues.Add(new MappingIssueView(MappingIssueCode.BadAmount, f.Target, raw, $"'{raw}' is not an amount with at most {places} decimal places."));
                    }

                    break;
                case MappingTransform.Enum:
                    if (codes.TryGetValue(f.CodeMap!, out var table) && table.TryGetValue(raw, out var core))
                    {
                        output = core;
                    }
                    else
                    {
                        issues.Add(new MappingIssueView(MappingIssueCode.UnmappedCode, f.Target, raw, $"'{raw}' has no entry in the code table '{f.CodeMap}'."));
                    }

                    break;
            }

            if (output is not null)
            {
                Set(payload, f.Target, output);
            }
        }

        var hash = Sha256Hash.ComputeUtf8($"{targetObjectType}\n{record.LegacyKey}\n{payload.ToJsonString()}").Value;
        return new MappingItemResult(record.LegacyKey, targetObjectType, record.LegacyKey, hash, payload, issues);
    }

    private static void Set(JsonObject root, string path, string value)
    {
        var parts = path.Split('.');
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject child)
            {
                child = [];
                node[parts[i]] = child;
            }

            node = child;
        }

        node[parts[^1]] = value;
    }

    public static bool ValidModule(string module) => ModuleRegex().IsMatch(module);
}
