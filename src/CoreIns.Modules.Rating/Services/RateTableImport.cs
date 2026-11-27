using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.Rules.DecisionTables;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Rating.Services;

/// <summary>One finding of a rate-table validation: severity ERROR refuses the import, WARNING is reported only.</summary>
internal sealed record TableFinding(string Severity, string Code, int? Row, string? Column, string Message);

/// <summary>The parse and analysis of an import request: the table, every finding, and the compiled table when it compiles.</summary>
internal sealed record TableAnalysis(TableDto? Dto, List<TableFinding> Findings, CompiledTable? Compiled)
{
    public bool IsValid => Dto is not null && Compiled is not null && Findings.TrueForAll(f => f.Severity != "ERROR");
}

/// <summary>A cell-level change between two table versions.</summary>
internal sealed record CellChange(string RuleId, string Kind, string Column, string? Before, string? After);

/// <summary>
/// Pure parsing, validation and diff of rate tables (REQ-RAT-167..184): CSV or JSON to a <see cref="TableDto"/>, the engine's own
/// compile check for types and cells, and gap/overlap detection on numeric bands. No I/O.
/// </summary>
internal static partial class RateTableAnalyzer
{
    public const int MaxRows = 5000;
    private static readonly string[] KnownTypes = ["string", "int", "decimal", "date", "bool"];
    private static readonly decimal Inf = 1e20m;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_\\-]{0,63}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^\s*([\[\(])\s*(-?\d+(?:\.\d+)?)\s*\.\.\s*(-?\d+(?:\.\d+)?)\s*([\]\)])\s*$")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^\s*(>=|<=|>|<|==)\s*(-?\d+(?:\.\d+)?)\s*$")]
    private static partial Regex ComparePattern();

    /// <summary>Parses and checks a request (import and validate share it). Bad content is reported as findings, not thrown.</summary>
    public static TableAnalysis Analyze(JsonElement? file, IReadOnlyList<JsonElement>? rows, JsonElement? mapping, string? latestVersion)
    {
        var findings = new List<TableFinding>();
        TableDto? dto;
        try
        {
            dto = Parse(file, rows, mapping, latestVersion, findings);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            findings.Add(Error("PARSE", null, null, "The input could not be read: " + ex.Message));
            return new TableAnalysis(null, findings, null);
        }

        if (dto is null)
        {
            return new TableAnalysis(null, findings, null);
        }

        CheckStructure(dto, findings);
        CompiledTable? compiled = null;
        if (findings.TrueForAll(f => f.Severity != "ERROR"))
        {
            try
            {
                compiled = CompiledArtefact.CompileTable(dto);
            }
            catch (DomainException ex)
            {
                foreach (var line in (ex.Error.Detail ?? "The table does not compile.").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    findings.Add(Error("COMPILE", null, null, line));
                }
            }
        }

        CheckBands(dto, findings);
        return new TableAnalysis(dto, findings, findings.TrueForAll(f => f.Severity != "ERROR") ? compiled : null);
    }

    private static TableFinding Error(string code, int? row, string? column, string message) => new("ERROR", code, row, column, message);

    private static TableFinding Warning(string code, int? row, string? column, string message) => new("WARNING", code, row, column, message);

    private static TableDto? Parse(JsonElement? file, IReadOnlyList<JsonElement>? rows, JsonElement? mapping, string? latestVersion, List<TableFinding> findings)
    {
        var hasFile = file is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        var hasRows = rows is { Count: > 0 };
        if (hasFile == hasRows)
        {
            findings.Add(Error("INPUT", null, null, "Send exactly one of file or rows."));
            return null;
        }

        string? csv = null;
        List<Dictionary<string, string>>? jsonRows = null;
        if (hasFile)
        {
            var f = file!.Value;
            if (f.ValueKind == JsonValueKind.String)
            {
                csv = f.GetString();
            }
            else if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("rules", out _) && f.TryGetProperty("code", out _))
            {
                return f.Deserialize<TableDto>(ArtefactJson.Options) is { } direct ? WithDefaults(direct) : null;
            }
            else if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("content", out var content))
            {
                var format = f.TryGetProperty("format", out var fmt) ? fmt.GetString() : "csv";
                var text = content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : content.GetRawText();
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("rules", out _))
                    {
                        return doc.RootElement.Deserialize<TableDto>(ArtefactJson.Options) is { } json ? WithDefaults(json) : null;
                    }

                    jsonRows = ReadObjectRows([.. doc.RootElement.EnumerateArray().Select(e => e.Clone())]);
                }
                else
                {
                    csv = text;
                }
            }
            else
            {
                findings.Add(Error("INPUT", null, null, "file must be a CSV text, an object {format, content}, or a table definition."));
                return null;
            }
        }
        else
        {
            jsonRows = ReadObjectRows(rows!);
        }

        if (mapping is not { ValueKind: JsonValueKind.Object } map)
        {
            findings.Add(Error("MAPPING", null, null, "mapping is required for CSV and row input: code, inputs and outputs."));
            return null;
        }

        var meta = map.Deserialize<MappingDto>(ArtefactJson.Options) ?? new MappingDto();
        List<Dictionary<string, string>> table;
        List<int> sourceRows;
        if (csv is not null)
        {
            table = ParseCsv(csv, meta.Delimiter, findings);
            sourceRows = [.. Enumerable.Range(2, table.Count)];
        }
        else
        {
            table = jsonRows!;
            sourceRows = [.. Enumerable.Range(1, table.Count)];
        }

        if (table.Count > MaxRows)
        {
            findings.Add(Error("TOO-MANY-ROWS", null, null, $"A table may have at most {MaxRows} rows."));
            return null;
        }

        var inputs = (meta.Inputs ?? []).Select(i => new TableColumnDto(i.Name ?? string.Empty, i.Type ?? string.Empty, i.Expression ?? string.Empty)).ToList();
        var outputs = (meta.Outputs ?? []).Select(o => new TableOutputDto(o.Name ?? string.Empty, o.Type ?? string.Empty)).ToList();
        var rules = new List<TableRuleDto>();
        for (var i = 0; i < table.Count; i++)
        {
            var row = table[i];
            var conditions = new List<string>();
            foreach (var input in inputs)
            {
                if (!row.TryGetValue(input.Name, out var cell))
                {
                    findings.Add(Error("MISSING-COLUMN", sourceRows[i], input.Name, $"Column {input.Name} is missing."));
                    cell = string.Empty;
                }

                conditions.Add(cell);
            }

            var outs = new List<string>();
            foreach (var output in outputs)
            {
                if (!row.TryGetValue(output.Name, out var cell))
                {
                    findings.Add(Error("MISSING-COLUMN", sourceRows[i], output.Name, $"Column {output.Name} is missing."));
                    cell = string.Empty;
                }

                outs.Add(cell);
            }

            rules.Add(new TableRuleDto(row.TryGetValue("id", out var id) && id.Length > 0 ? id : "R" + (i + 1).ToString(CultureInfo.InvariantCulture), conditions, outs));
        }

        return new TableDto(
            meta.Code ?? string.Empty, meta.Version ?? NextVersion(latestVersion), meta.HitPolicy ?? "First", meta.DataStatus ?? DataStatus.Illustrative,
            meta.Description ?? string.Empty, meta.EffectiveFrom ?? string.Empty, meta.Variables ?? [], inputs, outputs, rules);
    }

    private static TableDto WithDefaults(TableDto dto) => dto with
    {
        Variables = dto.Variables ?? [],
        Inputs = dto.Inputs ?? [],
        Outputs = dto.Outputs ?? [],
        Rules = dto.Rules ?? [],
        Code = dto.Code ?? string.Empty,
        Version = dto.Version ?? string.Empty,
        HitPolicy = dto.HitPolicy ?? "First",
        DataStatus = dto.DataStatus ?? Domain.DataStatus.Illustrative,
        Description = dto.Description ?? string.Empty,
        EffectiveFrom = dto.EffectiveFrom ?? string.Empty,
    };

    private static string NextVersion(string? latest) =>
        int.TryParse(latest, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? (n + 1).ToString(CultureInfo.InvariantCulture) : latest is null ? "1" : latest + ".1";

    private static List<Dictionary<string, string>> ReadObjectRows(IReadOnlyList<JsonElement> rows)
    {
        var result = new List<Dictionary<string, string>>();
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Every row must be an object of column to cell.");
            }

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in row.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
            }

            result.Add(dict);
        }

        return result;
    }

    /// <summary>RFC 4180 style CSV with a header row; quotes may hold the delimiter, quotes and line breaks.</summary>
    internal static List<Dictionary<string, string>> ParseCsv(string text, string? delimiter, List<TableFinding> findings)
    {
        var sep = string.IsNullOrEmpty(delimiter) ? ',' : delimiter[0];
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == sep)
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
        {
            findings.Add(Error("CSV", null, null, "A quoted CSV field is not closed."));
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        records.RemoveAll(r => r.Count == 1 && r[0].Trim().Length == 0);
        var result = new List<Dictionary<string, string>>();
        if (records.Count == 0)
        {
            findings.Add(Error("CSV", null, null, "The CSV is empty."));
            return result;
        }

        var header = records[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        for (var r = 1; r < records.Count; r++)
        {
            if (records[r].Count != header.Count)
            {
                findings.Add(Error("CSV", r + 1, null, $"Line {r + 1} has {records[r].Count} cells, the header has {header.Count}."));
                continue;
            }

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
            {
                dict[header[c]] = records[r][c].Trim();
            }

            result.Add(dict);
        }

        return result;
    }

    private static void CheckStructure(TableDto dto, List<TableFinding> findings)
    {
        if (!CodePattern().IsMatch(dto.Code))
        {
            findings.Add(Error("CODE", null, null, "The table code must start with a letter and use letters, digits, '_' or '-' (max 64)."));
        }

        if (string.IsNullOrWhiteSpace(dto.Version))
        {
            findings.Add(Error("VERSION", null, null, "The table version is required."));
        }

        if (!DateOnly.TryParseExact(dto.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            findings.Add(Error("EFFECTIVE-FROM", null, null, "effectiveFrom must be a date yyyy-MM-dd."));
        }

        if (!Enum.TryParse<HitPolicy>(dto.HitPolicy, ignoreCase: true, out _))
        {
            findings.Add(Error("HIT-POLICY", null, null, $"Unknown hit policy '{dto.HitPolicy}'."));
        }

        if (!string.Equals(dto.DataStatus, DataStatus.Illustrative, StringComparison.Ordinal))
        {
            findings.Add(Error("DATA-STATUS", null, null, "An imported table is ILLUSTRATIVE_TEST_DATA; approval is a release step, not an import."));
        }

        if (dto.Inputs.Count == 0 || dto.Outputs.Count == 0)
        {
            findings.Add(Error("COLUMNS", null, null, "A table needs at least one input and one output column."));
        }

        foreach (var column in dto.Inputs)
        {
            CheckColumn(column.Name, column.Type, findings);
        }

        foreach (var column in dto.Outputs)
        {
            CheckColumn(column.Name, column.Type, findings);
        }

        var names = dto.Inputs.Select(i => i.Name).Concat(dto.Outputs.Select(o => o.Name));
        foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            findings.Add(Error("COLUMN-DUPLICATE", null, dup.Key, $"Column {dup.Key} is declared twice."));
        }

        if (dto.Rules.Count == 0)
        {
            findings.Add(Error("NO-RULES", null, null, "The table has no rows."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < dto.Rules.Count; i++)
        {
            var rule = dto.Rules[i];
            if (!seen.Add(rule.Id))
            {
                findings.Add(Error("RULE-DUPLICATE", i + 1, null, $"Row id {rule.Id} is used twice."));
            }

            if (rule.Conditions.Count != dto.Inputs.Count || rule.Outputs.Count != dto.Outputs.Count)
            {
                findings.Add(Error("RULE-SHAPE", i + 1, null, $"Row {rule.Id} has {rule.Conditions.Count} conditions and {rule.Outputs.Count} outputs; the table has {dto.Inputs.Count} and {dto.Outputs.Count}."));
                continue;
            }

            for (var c = 0; c < rule.Conditions.Count; c++)
            {
                if (string.IsNullOrWhiteSpace(rule.Conditions[c]))
                {
                    findings.Add(Error("EMPTY-CELL", i + 1, dto.Inputs[c].Name, $"Row {rule.Id}: empty condition for {dto.Inputs[c].Name} (use '-' for any)."));
                }
            }

            for (var c = 0; c < rule.Outputs.Count; c++)
            {
                if (string.IsNullOrWhiteSpace(rule.Outputs[c]))
                {
                    findings.Add(Error("EMPTY-CELL", i + 1, dto.Outputs[c].Name, $"Row {rule.Id}: empty output for {dto.Outputs[c].Name}."));
                }
            }
        }
    }

    private static void CheckColumn(string name, string type, List<TableFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            findings.Add(Error("COLUMN-NAME", null, null, "A column has no name."));
        }

        if (Array.IndexOf(KnownTypes, type) < 0)
        {
            findings.Add(Error("COLUMN-TYPE", null, name, $"Column {name}: unknown type '{type}' (string, int, decimal, date, bool)."));
        }
    }

    private readonly record struct Band(int Row, string RuleId, decimal Lo, bool LoInc, decimal Hi, bool HiInc);

    /// <summary>
    /// Gap and overlap detection on numeric input columns: rows that agree on every other input cell form one group, and the
    /// bands of the column inside a group must not overlap (ERROR, WARNING under Collect) or leave holes (WARNING).
    /// </summary>
    private static void CheckBands(TableDto dto, List<TableFinding> findings)
    {
        var collect = string.Equals(dto.HitPolicy, "Collect", StringComparison.OrdinalIgnoreCase);
        for (var col = 0; col < dto.Inputs.Count; col++)
        {
            var type = dto.Inputs[col].Type;
            if (type is not ("int" or "decimal"))
            {
                continue;
            }

            var groups = new Dictionary<string, List<Band>>(StringComparer.Ordinal);
            for (var r = 0; r < dto.Rules.Count; r++)
            {
                var rule = dto.Rules[r];
                if (rule.Conditions.Count != dto.Inputs.Count || !TryBand(rule.Conditions[col], type == "int", out var lo, out var loInc, out var hi, out var hiInc))
                {
                    continue;
                }

                var key = string.Join('\u001f', rule.Conditions.Where((_, i) => i != col).Select(c => c.Trim()));
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = [];
                }

                list.Add(new Band(r + 1, rule.Id, lo, loInc, hi, hiInc));
            }

            foreach (var list in groups.Values.Where(l => l.Count > 1))
            {
                var sorted = list.OrderBy(b => b.Lo).ThenBy(b => b.Hi).ToList();
                var reach = sorted[0];
                for (var i = 1; i < sorted.Count; i++)
                {
                    var next = sorted[i];
                    var name = dto.Inputs[col].Name;
                    if (reach.Hi > next.Lo || (reach.Hi == next.Lo && reach.HiInc && next.LoInc))
                    {
                        var message = $"Bands of {name} overlap: rows {reach.RuleId} and {next.RuleId}.";
                        findings.Add(collect ? Warning("BAND-OVERLAP", next.Row, name, message) : Error("BAND-OVERLAP", next.Row, name, message));
                    }
                    else
                    {
                        var isGap = type == "int" ? next.Lo > reach.Hi + 1 : next.Lo > reach.Hi || (next.Lo == reach.Hi && !reach.HiInc && !next.LoInc);
                        if (isGap)
                        {
                            findings.Add(Warning("BAND-GAP", next.Row, name, $"Bands of {name} leave a gap between rows {reach.RuleId} and {next.RuleId}."));
                        }
                    }

                    if (next.Hi > reach.Hi || (next.Hi == reach.Hi && next.HiInc))
                    {
                        reach = next;
                    }
                }
            }
        }
    }

    /// <summary>Reads a band cell: [a..b], (a..b], &gt;= n, &lt; n, == n, or '-'. Exclusive integer bounds are normalised to inclusive.</summary>
    private static bool TryBand(string cell, bool integer, out decimal lo, out bool loInc, out decimal hi, out bool hiInc)
    {
        lo = -Inf;
        hi = Inf;
        loInc = hiInc = true;
        if (RangePattern().Match(cell) is { Success: true } range)
        {
            lo = decimal.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            hi = decimal.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
            loInc = range.Groups[1].Value == "[";
            hiInc = range.Groups[4].Value == "]";
        }
        else if (ComparePattern().Match(cell) is { Success: true } cmp)
        {
            var n = decimal.Parse(cmp.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (cmp.Groups[1].Value)
            {
                case ">=":
                    lo = n;
                    break;
                case ">":
                    lo = n;
                    loInc = false;
                    break;
                case "<=":
                    hi = n;
                    break;
                case "<":
                    hi = n;
                    hiInc = false;
                    break;
                default:
                    lo = hi = n;
                    break;
            }
        }
        else if (cell.Trim() != "-")
        {
            return false;
        }

        if (integer)
        {
            if (!loInc)
            {
                lo += 1;
                loInc = true;
            }

            if (!hiInc)
            {
                hi -= 1;
                hiInc = true;
            }
        }

        return true;
    }

    /// <summary>Cell-level diff of two versions of a table, rows matched by row id (REQ-RAT-176).</summary>
    public static object Diff(string fromHash, TableDto from, string toHash, TableDto to)
    {
        var meta = new List<object>();
        void Meta(string field, string? before, string? after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                meta.Add(new { field, before, after });
            }
        }

        Meta("code", from.Code, to.Code);
        Meta("version", from.Version, to.Version);
        Meta("hitPolicy", from.HitPolicy, to.HitPolicy);
        Meta("effectiveFrom", from.EffectiveFrom, to.EffectiveFrom);
        Meta("dataStatus", from.DataStatus, to.DataStatus);
        Meta("inputs", string.Join(", ", from.Inputs.Select(i => $"{i.Name}:{i.Type}={i.Expression}")), string.Join(", ", to.Inputs.Select(i => $"{i.Name}:{i.Type}={i.Expression}")));
        Meta("outputs", string.Join(", ", from.Outputs.Select(o => $"{o.Name}:{o.Type}")), string.Join(", ", to.Outputs.Select(o => $"{o.Name}:{o.Type}")));

        var fromRules = from.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var toRules = to.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var added = to.Rules.Where(r => !fromRules.ContainsKey(r.Id)).Select(r => r.Id).ToList();
        var removed = from.Rules.Where(r => !toRules.ContainsKey(r.Id)).Select(r => r.Id).ToList();
        var changes = new List<CellChange>();
        foreach (var rule in to.Rules.Where(r => fromRules.ContainsKey(r.Id)))
        {
            var before = fromRules[rule.Id];
            for (var i = 0; i < to.Inputs.Count && i < rule.Conditions.Count; i++)
            {
                var name = to.Inputs[i].Name;
                var bi = from.Inputs.FindIndex(c => c.Name == name);
                var b = bi >= 0 && bi < before.Conditions.Count ? before.Conditions[bi] : null;
                if (!string.Equals(b, rule.Conditions[i], StringComparison.Ordinal))
                {
                    changes.Add(new CellChange(rule.Id, "input", name, b, rule.Conditions[i]));
                }
            }

            for (var i = 0; i < to.Outputs.Count && i < rule.Outputs.Count; i++)
            {
                var name = to.Outputs[i].Name;
                var bi = from.Outputs.FindIndex(c => c.Name == name);
                var b = bi >= 0 && bi < before.Outputs.Count ? before.Outputs[bi] : null;
                if (!string.Equals(b, rule.Outputs[i], StringComparison.Ordinal))
                {
                    changes.Add(new CellChange(rule.Id, "output", name, b, rule.Outputs[i]));
                }
            }
        }

        var commonFrom = from.Rules.Where(r => toRules.ContainsKey(r.Id)).Select(r => r.Id);
        var commonTo = to.Rules.Where(r => fromRules.ContainsKey(r.Id)).Select(r => r.Id);
        return new
        {
            from = new { hash = fromHash, version = from.Version },
            to = new { hash = toHash, version = to.Version },
            identical = fromHash == toHash,
            metadata = meta,
            addedRules = added,
            removedRules = removed,
            changedCells = changes,
            reordered = !commonFrom.SequenceEqual(commonTo, StringComparer.Ordinal),
        };
    }

    private sealed class MappingDto
    {
        public string? Code { get; set; }

        public string? Version { get; set; }

        public string? HitPolicy { get; set; }

        public string? DataStatus { get; set; }

        public string? Description { get; set; }

        public string? EffectiveFrom { get; set; }

        public string? Delimiter { get; set; }

        public List<TableVariableDto>? Variables { get; set; }

        public List<MappingColumn>? Inputs { get; set; }

        public List<MappingColumn>? Outputs { get; set; }
    }

    private sealed class MappingColumn
    {
        public string? Name { get; set; }

        public string? Type { get; set; }

        public string? Expression { get; set; }
    }
}

/// <summary>Reads and writes <c>rat.rate_table_version</c> for the authoring operations; the only write is a content-hash insert (write-once).</summary>
internal sealed class RateTableImportService(DbSession session, IClock clock, RequestContext context)
{
    public async Task<RateTableValidateResponse> ValidateAsync(RateTableValidateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (analysis, latest) = await AnalyzeAsync(request.File, request.Rows, request.Mapping, cancellationToken).ConfigureAwait(false);
        var diff = analysis.Compiled is null || latest is null ? null : RateTableAnalyzer.Diff(latest.Value.Hash, latest.Value.Dto, analysis.Compiled.Hash, analysis.Dto!);
        return new RateTableValidateResponse
        {
            TableVersion = Version(analysis, created: false),
            ValidationReport = Report(analysis),
            Diff = diff is null ? null : JsonSerializer.SerializeToElement(diff, ArtefactJson.Options),
        };
    }

    public async Task<RateTableImportResponse> ImportAsync(RateTableImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (analysis, latest) = await AnalyzeAsync(request.File, request.Rows, request.Mapping, cancellationToken).ConfigureAwait(false);
        if (!analysis.IsValid)
        {
            var errors = analysis.Findings.Where(f => f.Severity == "ERROR").ToList();
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "IMPORT-INVALID",
                $"The table is not valid ({errors.Count} errors): " + string.Join(" | ", errors.Take(10).Select(e => e.Message))));
        }

        var compiled = analysis.Compiled!;
        var dto = analysis.Dto!;
        var created = false;
        if (!context.DryRun)
        {
            var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var sameVersion = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT table_hash FROM rat.rate_table_version WHERE table_code = @code AND version_no = @version LIMIT 1",
                new { code = dto.Code, version = dto.Version }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (sameVersion is not null && !string.Equals(sameVersion, compiled.Hash, StringComparison.Ordinal))
            {
                throw new DomainException(DomainError.Of(ModuleCode.RAT, "IMPORT-INVALID",
                    $"Version {dto.Version} of table {dto.Code} already exists with different content; versions are write-once, send a new version."));
            }

            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO rat.rate_table_version (table_hash, table_code, version_no, hit_policy, data_status, definition, created_at, created_by)
                VALUES (@hash, @code, @version, @hitPolicy, @status, @definition::jsonb, @now, @actor) ON CONFLICT DO NOTHING
                """,
                new
                {
                    hash = compiled.Hash,
                    code = dto.Code,
                    version = dto.Version,
                    hitPolicy = dto.HitPolicy,
                    status = dto.DataStatus,
                    definition = ArtefactJson.Serialize(dto),
                    now = clock.Now.ToUtcDateTime(),
                    actor = context.Actor.ToString(),
                }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            created = inserted == 1;
        }

        var diff = latest is null || latest.Value.Hash == compiled.Hash
            ? null
            : RateTableAnalyzer.Diff(latest.Value.Hash, latest.Value.Dto, compiled.Hash, dto);
        return new RateTableImportResponse
        {
            TableVersion = Version(analysis, created),
            ValidationReport = Report(analysis),
            Diff = diff is null ? null : JsonSerializer.SerializeToElement(diff, ArtefactJson.Options),
        };
    }

    public async Task<RateTableDiffResponse> DiffAsync(string? from, string? to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "IMPORT-INVALID", "Both from and to (table hashes) are required."));
        }

        var a = await LoadAsync(from, cancellationToken).ConfigureAwait(false);
        var b = await LoadAsync(to, cancellationToken).ConfigureAwait(false);
        return new RateTableDiffResponse
        {
            TableVersion = JsonSerializer.SerializeToElement(new { from = new { hash = from, version = a.Version }, to = new { hash = to, version = b.Version } }, ArtefactJson.Options),
            Diff = JsonSerializer.SerializeToElement(RateTableAnalyzer.Diff(from, a, to, b), ArtefactJson.Options),
        };
    }

    private async Task<TableDto> LoadAsync(string hash, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var definition = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT definition::text FROM rat.rate_table_version WHERE table_hash = @hash", new { hash }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return definition is null
            ? throw new DomainException(DomainError.Of(ModuleCode.RAT, "IMPORT-INVALID", $"Unknown table version {hash}."))
            : ArtefactJson.Deserialize<TableDto>(definition);
    }

    /// <summary>Parses the request; the latest stored version of the table code (if the request names one) is the diff base and the version default.</summary>
    private async Task<(TableAnalysis Analysis, (string Hash, TableDto Dto)? Latest)> AnalyzeAsync(
        JsonElement? file, IReadOnlyList<JsonElement>? rows, JsonElement? mapping, CancellationToken cancellationToken)
    {
        var code = CodeOf(file, mapping);
        (string Hash, TableDto Dto)? latest = null;
        if (code is not null)
        {
            var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QueryFirstOrDefaultAsync<TableRecord>(new CommandDefinition(
                "SELECT table_hash AS Hash, definition::text AS Definition FROM rat.rate_table_version WHERE table_code = @code ORDER BY created_at DESC, version_no DESC LIMIT 1",
                new { code }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (row is not null)
            {
                latest = (row.Hash, ArtefactJson.Deserialize<TableDto>(row.Definition));
            }
        }

        return (RateTableAnalyzer.Analyze(file, rows, mapping, latest?.Dto.Version), latest);
    }

    private static string? CodeOf(JsonElement? file, JsonElement? mapping)
    {
        if (mapping is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
        {
            return c.GetString();
        }

        return file is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("code", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() : null;
    }

    private static JsonElement Version(TableAnalysis analysis, bool created) => JsonSerializer.SerializeToElement(new
    {
        code = analysis.Dto?.Code,
        version = analysis.Dto?.Version,
        hash = analysis.Compiled?.Hash,
        dataStatus = analysis.Dto?.DataStatus,
        ruleCount = analysis.Dto?.Rules.Count,
        created,
    }, ArtefactJson.Options);

    private static JsonElement Report(TableAnalysis analysis) => JsonSerializer.SerializeToElement(new
    {
        valid = analysis.IsValid,
        errors = analysis.Findings.Where(f => f.Severity == "ERROR"),
        warnings = analysis.Findings.Where(f => f.Severity == "WARNING"),
    }, ArtefactJson.Options);
}
