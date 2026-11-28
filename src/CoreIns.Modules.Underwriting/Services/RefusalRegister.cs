using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using CoreIns.Modules.Underwriting.Queries;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary><c>uw.RefusalRegister.export</c>: the register as CSV for a period and filters (REQ-UW-229..231).</summary>
internal sealed record ExportRefusalRegister(RefusalRegisterExportRequest Request) : ICommand<RefusalRegisterExportResponse>;

/// <summary>Parses the period and the filters of the register (query string or export body) into a <see cref="RegisterFilter"/>.</summary>
internal static class RegisterFilterParser
{
    private static readonly string[] Statuses = ["Issued", "ReviewRequested", "Upheld", "Overturned"];

    /// <summary>The filter, or the message of what is wrong. <paramref name="period"/> is <c>YYYY</c>, <c>YYYY-MM</c>, <c>YYYY-MM-DD</c> or <c>from/to</c> (inclusive dates).</summary>
    public static (RegisterFilter? Filter, string? Error) Parse(string? period, IEnumerable<KeyValuePair<string, string>> filters)
    {
        DateTime? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            var parts = period.Split('/');
            if (parts.Length > 2 || !TryDay(parts[0], out var start, out var startEnd) || (parts.Length == 2 && !TryDay(parts[1], out _, out startEnd)))
            {
                return (null, "period must be YYYY, YYYY-MM, YYYY-MM-DD or from/to.");
            }

            (from, to) = (start, startEnd);
            if (to <= from)
            {
                return (null, "period ends before it starts.");
            }
        }

        string? status = null, scope = null, reason = null;
        bool? automated = null;
        foreach (var (key, value) in filters)
        {
            switch (key)
            {
                case "status" when Array.IndexOf(Statuses, value) >= 0:
                    status = value;
                    break;
                case "scope" when value is "FULL" or "PARTIAL":
                    scope = value;
                    break;
                case "automated" when bool.TryParse(value, out var flag):
                    automated = flag;
                    break;
                case "reason" when value.Length is > 1 and <= 64:
                    reason = value;
                    break;
                default:
                    return (null, $"filter '{key}={value}' is not valid; use status, scope, automated or reason.");
            }
        }

        return (new RegisterFilter(from, to, status, automated, scope, reason), null);
    }

    /// <summary><c>a=b,c=d</c> as pairs; null when a part is not <c>key=value</c>.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? Split(string? filters)
    {
        if (string.IsNullOrWhiteSpace(filters))
        {
            return [];
        }

        var result = new List<KeyValuePair<string, string>>();
        foreach (var part in filters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                return null;
            }

            result.Add(new(part[..at], part[(at + 1)..]));
        }

        return result;
    }

    private static bool TryDay(string text, out DateTime start, out DateTime endExclusive)
    {
        start = endExclusive = default;
        foreach (var (format, span) in new[] { ("yyyy", 12), ("yyyy-MM", 1), ("yyyy-MM-dd", 0) })
        {
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                start = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                endExclusive = span == 0 ? start.AddTicks(TimeSpan.TicksPerDay) : start.AddMonths(span);
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads of the decline: one decline, and the refusal register (REQ-UW-229, -230).</summary>
internal sealed class DeclineQueries(DeclineStore store, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public async Task<Result<DeclineGetResponse>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(LegalEntity, id, forUpdate: false, cancellationToken).ConfigureAwait(false) is { } row
            ? new DeclineGetResponse { Decline = DeclineSupport.View(row), RefusalRequirement = DeclineSupport.Requirement(row) }
            : DeclineSupport.NotFound(id);

    public async Task<Result<RefusalRegisterQueryPage>> QueryAsync(
        string? afm, string? period, string? filters, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (afm is not null)
        {
            // TODO(hardening): the decline does not hold the customer's AFM (UW does not own party data); filter by AFM once PTY resolves it from the job.
            return DomainError.Of(ModuleCode.UW, "NOT-AVAILABLE", "Filtering the register by AFM is not available yet.");
        }

        var parsed = RegisterFilterParser.Split(filters);
        if (parsed is null)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "filters must be key=value pairs separated by commas.");
        }

        var (filter, error) = RegisterFilterParser.Parse(period, parsed);
        if (filter is null)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", error!);
        }

        if (!KeysetPaging.TryTake(limit, 50, out var size) || !TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var rows = await store.QueryAsync(LegalEntity, filter, after, size + 1, cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, size, last => KeysetPaging.Encode(KeysetPaging.Part(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc).Ticks), KeysetPaging.Part(last.DeclineId)));
        return new RefusalRegisterQueryPage { Items = [.. page.Select(Entry)], NextCursor = next, Limit = size };
    }

    internal static RefusalRegisterQueryItem Entry(DeclineRecord r) => new()
    {
        DeclineId = r.DeclineId,
        DeclineNumber = r.DeclineNumber,
        JobRef = r.JobId?.ToString("D"),
        IntakeRef = r.IntakeId?.ToString("D"),
        Scope = r.Scope == "FULL" ? RefusalRegisterQueryItem.ScopeValue.Full : RefusalRegisterQueryItem.ScopeValue.Partial,
        ReasonCodes = JsonSerializer.Deserialize<string[]>(r.ReasonCodes) ?? [],
        Automated = r.Automated,
        Status = r.Status switch
        {
            "ReviewRequested" => RefusalRegisterQueryItem.StatusValue.ReviewRequested,
            "Upheld" => RefusalRegisterQueryItem.StatusValue.Upheld,
            "Overturned" => RefusalRegisterQueryItem.StatusValue.Overturned,
            _ => RefusalRegisterQueryItem.StatusValue.Issued,
        },
        IssuedAt = Instant.FromUtcDateTime(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        IssuedBy = r.CreatedBy,
        ReviewOutcome = r.ReviewOutcome,
    };

    private static bool TryDecode(string? cursor, out (DateTime CreatedAt, Guid DeclineId)? after)
    {
        after = null;
        if (!KeysetPaging.TryDecode(cursor, 2, out var parts))
        {
            return false;
        }

        if (parts is null)
        {
            return true;
        }

        if (!KeysetPaging.TryLong(parts[0], out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "D", out var id))
        {
            return false;
        }

        after = (new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}

internal sealed class ExportRefusalRegisterValidator : AbstractValidator<ExportRefusalRegister>
{
    public ExportRefusalRegisterValidator()
    {
        RuleFor(c => DeclineInput.Text(c.Request.Requester)).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(200).WithName("requester");
        RuleFor(c => DeclineInput.Text(c.Request.Purpose)).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(500).WithName("purpose");
    }
}

/// <summary>
/// Exports the register rows of a period and filters as CSV with a manifest (row count, columns, SHA-256, requester and purpose). The export
/// is audited with its requester and purpose; at most <see cref="MaxRows"/> rows leave in one export.
/// </summary>
internal sealed class ExportRefusalRegisterHandler(DeclineStore store, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ExportRefusalRegister, RefusalRegisterExportResponse>
{
    public const int MaxRows = 10_000;

    public static readonly string[] Columns =
        ["declineNumber", "issuedAt", "jobRef", "intakeRef", "scope", "reasonCodes", "automated", "status", "reviewOutcome", "issuedBy"];

    public async Task<Result<RefusalRegisterExportResponse>> HandleAsync(ExportRefusalRegister command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!DeclineInput.Absent(request.Afm))
        {
            // TODO(hardening): see DeclineQueries.QueryAsync: no AFM on the decline yet.
            return DomainError.Of(ModuleCode.UW, "NOT-AVAILABLE", "Exporting the register by AFM is not available yet.");
        }

        var pairs = new List<KeyValuePair<string, string>>();
        if (request.Filters is { ValueKind: JsonValueKind.Object } filters)
        {
            foreach (var property in filters.EnumerateObject())
            {
                pairs.Add(new(property.Name, property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.Value.GetRawText() : property.Value.ToString()));
            }
        }
        else if (!DeclineInput.Absent(request.Filters))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "filters must be an object.");
        }

        var (filter, error) = RegisterFilterParser.Parse(DeclineInput.Text(request.Period), pairs);
        if (filter is null)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", error!);
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var rows = await store.QueryAsync(legalEntity, filter, null, MaxRows + 1, cancellationToken).ConfigureAwait(false);
        if (rows.Count > MaxRows)
        {
            return DomainError.Of(ModuleCode.UW, "LIMIT-EXCEEDED", $"The export would hold more than {MaxRows} rows; narrow the period or the filters.");
        }

        var entries = rows.Select(DeclineQueries.Entry).ToList();
        var csv = new StringBuilder(string.Join(',', Columns)).Append("\r\n");
        foreach (var r in rows)
        {
            csv.Append(string.Join(',', new[]
            {
                r.DeclineNumber, DeclineSupport.Iso(r.CreatedAt), r.JobId?.ToString("D"), r.IntakeId?.ToString("D"), r.Scope,
                string.Join(';', JsonSerializer.Deserialize<string[]>(r.ReasonCodes) ?? []), r.Automated ? "true" : "false", r.Status, r.ReviewOutcome, r.CreatedBy,
            }.Select(Cell))).Append("\r\n");
        }

        var text = csv.ToString();
        return new RefusalRegisterExportResponse
        {
            Entries = [.. entries.Select(e => JsonSerializer.SerializeToElement(e, SharedKernelJson.Options))],
            FileRef = JsonSerializer.SerializeToElement(new { name = "refusal-register.csv", contentType = "text/csv; charset=utf-8", content = text }, SharedKernelJson.Options),
            Manifest = JsonSerializer.SerializeToElement(
                new
                {
                    rowCount = rows.Count,
                    columns = Columns,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                    generatedAt = DeclineSupport.Iso(store.Now),
                    requester = DeclineInput.Text(request.Requester),
                    purpose = DeclineInput.Text(request.Purpose),
                    period = DeclineInput.Text(request.Period),
                }, SharedKernelJson.Options),
        };
    }

    /// <summary>One CSV cell: quoted, with quotes doubled; a leading formula character is neutralised so a spreadsheet never runs it.</summary>
    private static string Cell(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>Audit facts of the export: who asked, why, and how many rows left.</summary>
internal sealed class ExportRefusalRegisterAuditor : ICommandAuditor<ExportRefusalRegister, RefusalRegisterExportResponse>
{
    public CommandAuditFacts Describe(ExportRefusalRegister command, Result<RefusalRegisterExportResponse>? result) =>
        new()
        {
            ObjectRef = new ObjectRef(ModuleCode.UW, "RefusalRegister", "export"),
            BusinessKeys = BusinessKeys.Empty,
            Changes = Platform.Audit.AuditDiff.Compute(
                new { },
                new
                {
                    requester = DeclineInput.Text(command.Request.Requester),
                    purpose = DeclineInput.Text(command.Request.Purpose),
                    period = DeclineInput.Text(command.Request.Period),
                    rows = result is { IsSuccess: true } ok ? ok.Value.Entries?.Count : null,
                }),
        };
}
