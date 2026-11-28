using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Audit;

/// <summary>Permission names of the audit operations (<c>x-permission</c>); granted in <c>permissions/plt.json</c>. <c>plt.Audit.append</c> is in-process only and has no REST route.</summary>
internal static class AuditPermissions
{
    public const string Query = "plt.Audit.query";
    public const string Export = "plt.Audit.export";
    public const string Verify = "plt.Audit.verify";
}

/// <summary>Error names of the audit operations.</summary>
internal static class AuditApiErrors
{
    public const string RangeTooLarge = "RANGE-TOO-LARGE";
}

/// <summary>Filter of the audit query and export (all conditions combine with AND; null = no condition).</summary>
internal sealed record AuditFilter
{
    public string? EntityType { get; init; }

    public string? EntityId { get; init; }

    public string? Actor { get; init; }

    public string? Action { get; init; }

    public string? CorrelationId { get; init; }

    public Instant? From { get; init; }

    public Instant? To { get; init; }

    public string? LegalEntity { get; init; }
}

/// <summary>Reads of <c>plt.audit_event</c> (BO-PLT-01): newest first, keyset-paged on (chain_date, sequence), which is the total order of the chains.</summary>
internal sealed class AuditQueries(NpgsqlDataSource dataSource)
{
    /// <summary>The widest range a query accepts.</summary>
    public const int MaxQueryDays = 366;

    /// <summary>The widest range an export accepts.</summary>
    public const int MaxExportDays = 92;

    /// <summary>The most rows one export returns.</summary>
    public const int MaxExportRows = 5000;

    private const string DateFormat = "yyyy-MM-dd";

    private const string Columns = """
        audit_id, chain_date, sequence, prev_hash, hash, actor_kind, actor_id, on_behalf_of, role_codes,
        authority_check_id, authority_used, operation, outcome, error_code, object_module, object_type, object_id,
        object_number, changes::text, reason, channel, correlation_id, causation_id, ai_interaction_id,
        business_keys::text, origin, legal_entity, jurisdiction, occurred_at, recorded_at
        """;

    private const string Where = """
        WHERE (@entity_type::text IS NULL OR object_type = @entity_type)
          AND (@entity_id::text IS NULL OR object_id = @entity_id)
          AND (@actor::text IS NULL OR actor_id = @actor)
          AND (@action::text IS NULL OR operation = @action)
          AND (@correlation::text IS NULL OR correlation_id = @correlation)
          AND (@legal_entity::text IS NULL OR legal_entity = @legal_entity)
          AND (@from::timestamptz IS NULL OR occurred_at >= @from)
          AND (@to::timestamptz IS NULL OR occurred_at < @to)
        """;

    /// <summary>A range check shared by query and export.</summary>
    public static DomainError? CheckRange(Instant? from, Instant? to, int maxDays)
    {
        if (from is { } f && to is { } t)
        {
            if (t < f)
            {
                return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "'to' must not be before 'from'.");
            }

            if (t.Since(f) > TimeSpan.FromDays(maxDays))
            {
                return DomainError.Of(ModuleCode.PLT, AuditApiErrors.RangeTooLarge, $"The range may span at most {maxDays} days.");
            }
        }

        return null;
    }

    public async Task<Result<AuditQueryPage>> QueryAsync(AuditFilter filter, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take)
            || !KeysetPaging.TryDecode(cursor, 2, out var parts)
            || (parts is not null && (!DateOnly.TryParseExact(parts[0], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || !KeysetPaging.TryLong(parts[1], out _))))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "cursor is malformed or limit is outside 1..200.");
        }

        if (CheckRange(filter.From, filter.To, MaxQueryDays) is { } rangeError)
        {
            return rangeError;
        }

        var sql = $"""
            SELECT {Columns} FROM plt.audit_event
            {Where}
              AND (@after_date::date IS NULL OR (chain_date, sequence) < (@after_date, @after_seq))
            ORDER BY chain_date DESC, sequence DESC
            LIMIT @take
            """;
        var rows = new List<StoredAudit>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            AddFilter(command, filter);
            command.Parameters.Add(new NpgsqlParameter("after_date", NpgsqlDbType.Date)
            {
                Value = parts is null ? DBNull.Value : DateOnly.ParseExact(parts[0], DateFormat, CultureInfo.InvariantCulture),
            });
            command.Parameters.Add(new NpgsqlParameter("after_seq", NpgsqlDbType.Bigint) { Value = parts is null ? DBNull.Value : long.Parse(parts[1], CultureInfo.InvariantCulture) });
            command.Parameters.Add(new NpgsqlParameter<int>("take", take + 1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(StoredAudit.Read(reader));
            }
        }

        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(r.ChainDate.ToString(DateFormat, CultureInfo.InvariantCulture), KeysetPaging.Part(r.Sequence)));
        return new AuditQueryPage { Items = [.. page.Select(ToItem)], NextCursor = next, Limit = take };
    }

    /// <summary>Up to <see cref="MaxExportRows"/> + 1 rows, oldest first.</summary>
    public async Task<IReadOnlyList<StoredAudit>> ExportRowsAsync(AuditFilter filter, CancellationToken cancellationToken)
    {
        var rows = new List<StoredAudit>();
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM plt.audit_event {Where} ORDER BY chain_date, sequence LIMIT @max");
        AddFilter(command, filter);
        command.Parameters.Add(new NpgsqlParameter<int>("max", MaxExportRows + 1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(StoredAudit.Read(reader));
        }

        return rows;
    }

    private static void AddFilter(NpgsqlCommand command, AuditFilter filter)
    {
        static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
        command.Parameters.Add(Text("entity_type", filter.EntityType));
        command.Parameters.Add(Text("entity_id", filter.EntityId));
        command.Parameters.Add(Text("actor", filter.Actor));
        command.Parameters.Add(Text("action", filter.Action));
        command.Parameters.Add(Text("correlation", filter.CorrelationId));
        command.Parameters.Add(Text("legal_entity", filter.LegalEntity));
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)filter.From?.ToUtcDateTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)filter.To?.ToUtcDateTime() ?? DBNull.Value });
    }

    private static AuditQueryItem ToItem(StoredAudit r) => new()
    {
        AuditId = r.AuditId,
        ChainDate = r.ChainDate.ToString(DateFormat, CultureInfo.InvariantCulture),
        Sequence = checked((int)r.Sequence),
        Hash = r.Hash,
        OccurredAt = r.OccurredAt,
        RecordedAt = r.RecordedAt,
        ActorKind = r.ActorKind,
        ActorId = r.ActorId,
        OnBehalfOf = r.OnBehalfOf,
        Roles = r.RoleCodes,
        Operation = r.Operation,
        Outcome = r.Outcome,
        ErrorCode = r.ErrorCode,
        ObjectModule = r.ObjectModule,
        ObjectType = r.ObjectType,
        ObjectId = r.ObjectId,
        ObjectNumber = r.ObjectNumber,
        Changes = JsonDocument.Parse(r.Changes).RootElement.Clone(),
        Reason = r.Reason,
        Channel = r.Channel,
        CorrelationId = r.CorrelationId,
        Origin = r.Origin,
    };
}

/// <summary><c>plt.Audit.export</c>: a capped CSV of the filtered records; the export is itself audited (the command pipeline appends its record).</summary>
internal sealed record ExportAudit(AuditExportRequest Request) : ICommand<AuditExportResponse>;

internal sealed class ExportAuditHandler(AuditQueries queries, RequestContext context) : ICommandHandler<ExportAudit, AuditExportResponse>
{
    private static readonly string[] Header =
    [
        "auditId", "chainDate", "sequence", "occurredAt", "actorKind", "actorId", "roles", "operation", "outcome", "errorCode", "objectModule",
        "objectType", "objectId", "objectNumber", "reason", "channel", "correlationId", "origin", "hash",
    ];

    public async Task<Result<AuditExportResponse>> HandleAsync(ExportAudit command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request is null)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "A request body is required.");
        }

        if (AuditQueries.CheckRange(request.From, request.To, AuditQueries.MaxExportDays) is { } rangeError)
        {
            return rangeError;
        }

        var filter = new AuditFilter
        {
            EntityType = request.EntityType, EntityId = request.EntityId, Actor = request.Actor, Action = request.Action,
            CorrelationId = request.CorrelationId, From = request.From, To = request.To, LegalEntity = context.LegalEntity?.Value,
        };
        var rows = await queries.ExportRowsAsync(filter, cancellationToken).ConfigureAwait(false);
        var truncated = rows.Count > AuditQueries.MaxExportRows;
        var exported = truncated ? rows.Take(AuditQueries.MaxExportRows).ToList() : [.. rows];

        var csv = new StringBuilder();
        AppendLine(csv, Header);
        foreach (var r in exported)
        {
            AppendLine(csv,
            [
                r.AuditId.ToString("D"), r.ChainDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Sequence.ToString(CultureInfo.InvariantCulture),
                r.OccurredAt.ToString(), r.ActorKind, r.ActorId, string.Join('|', r.RoleCodes), r.Operation, r.Outcome, r.ErrorCode ?? string.Empty,
                r.ObjectModule ?? string.Empty, r.ObjectType ?? string.Empty, r.ObjectId ?? string.Empty, r.ObjectNumber ?? string.Empty,
                r.Reason ?? string.Empty, r.Channel ?? string.Empty, r.CorrelationId, r.Origin, r.Hash,
            ]);
        }

        return new AuditExportResponse
        {
            FileName = $"audit-{request.From.UtcDate}-{request.To.UtcDate}.csv",
            ContentType = "text/csv; charset=utf-8",
            RowCount = exported.Count,
            Truncated = truncated,
            Csv = csv.ToString(),
        };
    }

    private static void AppendLine(StringBuilder csv, string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                csv.Append(',');
            }

            csv.Append(Cell(cells[i]));
        }

        csv.Append("\r\n");
    }

    /// <summary>RFC 4180 quoting, and a leading apostrophe on cells a spreadsheet would read as a formula (CSV injection).</summary>
    internal static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

internal sealed class ExportAuditAuditor : ICommandAuditor<ExportAudit, AuditExportResponse>
{
    public CommandAuditFacts Describe(ExportAudit command, Result<AuditExportResponse>? result) => new()
    {
        Changes = result is { IsSuccess: true }
            ? [new AuditChange("export.rowCount", null, JsonValue.Create(result.Value.Value.RowCount))]
            : [],
    };
}

/// <summary><c>plt.Audit.verify</c>: verifies the hash chain of each UTC day in a range of at most 31 days.</summary>
internal sealed record VerifyAudit(AuditVerifyRequest Request) : ICommand<AuditVerifyResponse>
{
    public const int MaxDays = 31;
}

internal sealed class VerifyAuditHandler(AuditChainVerifier verifier) : ICommandHandler<VerifyAudit, AuditVerifyResponse>
{
    public async Task<Result<AuditVerifyResponse>> HandleAsync(VerifyAudit command, CancellationToken cancellationToken)
    {
        if (command.Request is null || !BusinessDate.TryParse(command.Request.From, out var from) || !BusinessDate.TryParse(command.Request.To, out var to) || to < from)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "'from' and 'to' must be dates (yyyy-MM-dd) with from <= to.");
        }

        if (to.DaysSince(from) + 1 > VerifyAudit.MaxDays)
        {
            return DomainError.Of(ModuleCode.PLT, AuditApiErrors.RangeTooLarge, $"At most {VerifyAudit.MaxDays} days can be verified per call.");
        }

        var days = new List<Contracts.Api.AuditChainVerification>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var v = await verifier.VerifyAsync(day, cancellationToken).ConfigureAwait(false);
            days.Add(new Contracts.Api.AuditChainVerification
            {
                ChainDate = v.ChainDate.ToString(), Records = checked((int)v.Records), IsValid = v.IsValid,
                BrokenAtSequence = v.BrokenAtSequence is { } s ? checked((int)s) : null, Problem = v.Problem,
            });
        }

        return new AuditVerifyResponse { AllValid = days.All(d => d.IsValid), Days = days };
    }
}

internal sealed class VerifyAuditAuditor : ICommandAuditor<VerifyAudit, AuditVerifyResponse>
{
    public CommandAuditFacts Describe(VerifyAudit command, Result<AuditVerifyResponse>? result) => new()
    {
        Changes = result is { IsSuccess: true }
            ? [new AuditChange("verify.allValid", null, JsonValue.Create(result.Value.Value.AllValid))]
            : [],
    };
}

/// <summary>REST facade of <c>plt.Audit.*</c> (contracts/openapi/plt.yaml). <c>plt.Audit.append</c> is in-process only (no route).</summary>
[ApiController]
[Route("api/plt/v1/audit")]
internal sealed class AuditController : ControllerBase
{
    /// <summary>plt.Audit.query: newest first, keyset-paged.</summary>
    [HttpGet("query")]
    [Authorize(Policy = AuditPermissions.Query)]
    public async Task<IResult> QueryAsync(
        [FromQuery] string? entityType, [FromQuery] string? entityId, [FromQuery] string? actor, [FromQuery] string? action, [FromQuery] string? correlationId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] AuditQueries queries, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var filter = new AuditFilter
        {
            EntityType = entityType, EntityId = entityId, Actor = actor, Action = action, CorrelationId = correlationId,
            From = from is { } f ? Instant.FromDateTimeOffset(f) : null,
            To = to is { } t ? Instant.FromDateTimeOffset(t) : null,
            LegalEntity = context.LegalEntity?.Value,
        };
        return (await queries.QueryAsync(filter, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>plt.Audit.export: Idempotency-Key required.</summary>
    [HttpPost("export")]
    [Authorize(Policy = AuditPermissions.Export)]
    public async Task<IResult> ExportAsync(
        [FromBody] AuditExportRequest body, [FromServices] ICommandHandler<ExportAudit, AuditExportResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExportAudit(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.Audit.verify: Idempotency-Key required.</summary>
    [HttpPost("verify")]
    [Authorize(Policy = AuditPermissions.Verify)]
    public async Task<IResult> VerifyAsync(
        [FromBody] AuditVerifyRequest body, [FromServices] ICommandHandler<VerifyAudit, AuditVerifyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new VerifyAudit(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
