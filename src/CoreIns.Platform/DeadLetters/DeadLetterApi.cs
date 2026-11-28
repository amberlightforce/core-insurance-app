using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.DeadLetters;

/// <summary>Permission names of the dead-letter operations (<c>x-permission</c>); granted to Platform.Admin only in <c>permissions/plt.json</c>.</summary>
internal static class DeadLetterPermissions
{
    public const string List = "plt.DeadLetter.list";
    public const string Get = "plt.DeadLetter.get";
    public const string Replay = "plt.DeadLetter.replay";
    public const string Discard = "plt.DeadLetter.discard";
}

/// <summary>Configuration of the dead-letter operations (<c>Platform:DeadLetters</c>).</summary>
public sealed class DeadLetterOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Platform:DeadLetters";

    /// <summary>A dead letter parked longer than this many days can only be discarded with an Approved maker-checker request (BO-PLT-04).</summary>
    public int ApprovalAfterDays { get; set; } = 7;
}

/// <summary>Error names of the dead-letter operations.</summary>
internal static class DeadLetterErrors
{
    public const string Resolved = "DEAD-LETTER-RESOLVED";
    public const string DiscardApprovalRequired = "DISCARD-APPROVAL-REQUIRED";

    /// <summary>The approval type of a maker-checker discard (subject PLT/DeadLetter/&lt;id&gt;).</summary>
    public const string DiscardApprovalType = "plt.DeadLetter.discard";
}

/// <summary>Filter of the dead-letter list (all conditions combine with AND; null = no condition).</summary>
internal sealed record DeadLetterFilter
{
    public string? Handler { get; init; }

    public string? Module { get; init; }

    public string? EventType { get; init; }

    public DeadLetterStatus? Status { get; init; }

    public Instant? ParkedFrom { get; init; }

    public Instant? ParkedTo { get; init; }

    public bool IsEmpty => Handler is null && Module is null && EventType is null && Status is null && ParkedFrom is null && ParkedTo is null;
}

/// <summary>
/// Reads of <c>plt.outbox_dead_letter</c>. Only metadata and the last error are ever returned: the event payload (which may hold
/// personal data) stays in <c>plt.event_archive</c> and is not exposed here.
/// </summary>
internal sealed class DeadLetterQueries(NpgsqlDataSource dataSource)
{
    private const string Columns =
        "dead_letter_id, handler, event_id, event_type, aggregate_type, aggregate_id, error_class, error_message, attempts, parked_at, status, resolved_at, resolution_reason";

    private const string Where = """
        WHERE (@handler::text IS NULL OR handler = @handler)
          AND (@module::text IS NULL OR upper(split_part(handler, '.', 1)) = upper(@module))
          AND (@event_type::text IS NULL OR event_type = @event_type)
          AND (@status::text IS NULL OR status = @status)
          AND (@from::timestamptz IS NULL OR parked_at >= @from)
          AND (@to::timestamptz IS NULL OR parked_at < @to)
        """;

    /// <summary>Newest first, keyset-paged on (parked_at, dead_letter_id).</summary>
    public async Task<Result<DeadLetterListPage>> ListAsync(DeadLetterFilter filter, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take)
            || !KeysetPaging.TryDecode(cursor, 2, out var parts)
            || (parts is not null && (!KeysetPaging.TryLong(parts[0], out _) || !Guid.TryParse(parts[1], out _))))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "cursor is malformed or limit is outside 1..200.");
        }

        var sql = $"""
            SELECT {Columns} FROM plt.outbox_dead_letter
            {Where}
              AND (@after_at::timestamptz IS NULL OR (parked_at, dead_letter_id) < (@after_at, @after_id))
            ORDER BY parked_at DESC, dead_letter_id DESC
            LIMIT @take
            """;
        var rows = new List<DeadLetterListItem>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            AddFilter(command, filter);
            command.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz)
            {
                Value = parts is null ? DBNull.Value : new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc),
            });
            command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = parts is null ? DBNull.Value : Guid.Parse(parts[1]) });
            command.Parameters.Add(new NpgsqlParameter<int>("take", take + 1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(Read(reader));
            }
        }

        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.ParkedAt.ToUtcDateTime().Ticks), KeysetPaging.Part(r.DeadLetterId)));
        return new DeadLetterListPage { Items = page, NextCursor = next, Limit = take };
    }

    public async Task<DeadLetterListItem?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM plt.outbox_dead_letter WHERE dead_letter_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>The dead letters a replay acts on: the named ones, else the Parked matches of the filter (oldest first).</summary>
    public async Task<IReadOnlyList<DeadLetterListItem>> SelectAsync(IReadOnlyCollection<Guid>? ids, DeadLetterFilter filter, int max, CancellationToken cancellationToken)
    {
        var rows = new List<DeadLetterListItem>();
        var named = ids is { Count: > 0 };
        await using var command = dataSource.CreateCommand(
            named
                ? $"SELECT {Columns} FROM plt.outbox_dead_letter WHERE dead_letter_id = ANY(@ids) ORDER BY parked_at, dead_letter_id"
                : $"SELECT {Columns} FROM plt.outbox_dead_letter {Where} ORDER BY parked_at, dead_letter_id LIMIT @max");
        if (named)
        {
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids!.ToArray() });
        }
        else
        {
            AddFilter(command, filter with { Status = DeadLetterStatus.Parked });
            command.Parameters.Add(new NpgsqlParameter<int>("max", max));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private static void AddFilter(NpgsqlCommand command, DeadLetterFilter filter)
    {
        command.Parameters.Add(new NpgsqlParameter("handler", NpgsqlDbType.Text) { Value = (object?)filter.Handler ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("module", NpgsqlDbType.Text) { Value = (object?)filter.Module ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Text) { Value = (object?)filter.EventType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = filter.Status is { } s ? s.ToString() : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)filter.ParkedFrom?.ToUtcDateTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)filter.ParkedTo?.ToUtcDateTime() ?? DBNull.Value });
    }

    private static DeadLetterListItem Read(NpgsqlDataReader r)
    {
        var handler = r.GetString(1);
        return new DeadLetterListItem
        {
            DeadLetterId = r.GetGuid(0),
            Handler = handler,
            Module = handler.Split('.')[0].ToUpperInvariant(),
            EventId = r.GetGuid(2),
            EventType = r.GetString(3),
            AggregateType = r.GetString(4),
            AggregateId = r.GetString(5),
            ErrorClass = r.GetString(6),
            ErrorMessage = r.GetString(7),
            Attempts = r.GetInt32(8),
            ParkedAt = Instant.FromUtcDateTime(r.GetDateTime(9)),
            Status = Enum.Parse<DeadLetterStatus>(r.GetString(10)),
            ResolvedAt = r.IsDBNull(11) ? null : Instant.FromUtcDateTime(r.GetDateTime(11)),
            ResolutionReason = r.IsDBNull(12) ? null : r.GetString(12),
        };
    }
}

/// <summary><c>plt.DeadLetter.replay</c> (NX-OT-14): re-runs parked handlers; privileged, idempotent, audited, dry-run capable.</summary>
internal sealed record ReplayDeadLetters(DeadLetterReplayRequest Request) : ICommand<DeadLetterReplayResponse>
{
    /// <summary>At most this many dead letters are replayed per call; the caller repeats for the rest.</summary>
    public const int MaxPerCall = 100;

    public DeadLetterFilter Filter => Request.ToFilter();
}

internal sealed partial class ReplayDeadLettersValidator : AbstractValidator<ReplayDeadLetters>
{
    public ReplayDeadLettersValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.DeadLetterIds).Must(ids => ids is null || ids.Count <= ReplayDeadLetters.MaxPerCall)
            .WithMessage($"At most {ReplayDeadLetters.MaxPerCall} dead letters can be named.").When(c => c.Request is not null);
        RuleFor(c => c.Request).Must(r => r.DeadLetterIds is { Count: > 0 } || !r.ToFilter().IsEmpty)
            .WithMessage("Name deadLetterIds or give at least one filter (handler, module, eventType, parkedFrom, parkedTo).").When(c => c.Request is not null);
        RuleFor(c => c.Request.Module).Matches(ModulePattern()).When(c => c.Request?.Module is not null);
        RuleFor(c => c.Request.Handler).MaximumLength(200).When(c => c.Request?.Handler is not null);
        RuleFor(c => c.Request.EventType).MaximumLength(200).When(c => c.Request?.EventType is not null);
    }

    [GeneratedRegex("^[A-Za-z]{2,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModulePattern();
}

internal static class ReplayRequestExtensions
{
    public static DeadLetterFilter ToFilter(this DeadLetterReplayRequest request) => new()
    {
        Handler = request.Handler, Module = request.Module, EventType = request.EventType, ParkedFrom = request.ParkedFrom, ParkedTo = request.ParkedTo,
    };
}

internal sealed class ReplayDeadLettersHandler(DeadLetterQueries queries, OutboxReplayService replay, EventHandlerRegistry registry, RequestContext context)
    : ICommandHandler<ReplayDeadLetters, DeadLetterReplayResponse>
{
    public async Task<Result<DeadLetterReplayResponse>> HandleAsync(ReplayDeadLetters command, CancellationToken cancellationToken)
    {
        var ids = command.Request.DeadLetterIds?.Distinct().ToList();
        var named = ids is { Count: > 0 };
        var targets = await queries.SelectAsync(ids, command.Filter, ReplayDeadLetters.MaxPerCall, cancellationToken).ConfigureAwait(false);
        var results = new List<DeadLetterReplayOutcome>();

        if (named)
        {
            var found = targets.ToDictionary(t => t.DeadLetterId);
            if (found.Count == 0)
            {
                return DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "None of the named dead letters exists.");
            }

            if (found.Values.All(t => t.Status != DeadLetterStatus.Parked))
            {
                return DomainError.Of(ModuleCode.PLT, DeadLetterErrors.Resolved, "Every named dead letter was already replayed or discarded; nothing to do.");
            }

            results.AddRange(ids!.Where(id => !found.ContainsKey(id)).Select(id => new DeadLetterReplayOutcome { DeadLetterId = id, Outcome = DeadLetterReplayOutcome.OutcomeValue.NotFound }));
        }

        foreach (var target in targets)
        {
            results.Add(await ReplayOneAsync(target, cancellationToken).ConfigureAwait(false));
        }

        return new DeadLetterReplayResponse
        {
            DryRun = context.DryRun,
            Matched = targets.Count,
            Replayed = results.Count(r => r.Outcome == DeadLetterReplayOutcome.OutcomeValue.Replayed),
            StillFailing = results.Count(r => r.Outcome == DeadLetterReplayOutcome.OutcomeValue.StillFailing),
            Skipped = results.Count(r => r.Outcome is DeadLetterReplayOutcome.OutcomeValue.AlreadyResolved or DeadLetterReplayOutcome.OutcomeValue.NotFound
                or DeadLetterReplayOutcome.OutcomeValue.HandlerMissing),
            Results = results,
        };
    }

    private async Task<DeadLetterReplayOutcome> ReplayOneAsync(DeadLetterListItem target, CancellationToken cancellationToken)
    {
        DeadLetterReplayOutcome Outcome(DeadLetterReplayOutcome.OutcomeValue value, string? error = null) =>
            new() { DeadLetterId = target.DeadLetterId, Outcome = value, ErrorMessage = error };

        if (target.Status != DeadLetterStatus.Parked)
        {
            return Outcome(DeadLetterReplayOutcome.OutcomeValue.AlreadyResolved);
        }

        if (registry.Find(target.Handler) is null)
        {
            return Outcome(DeadLetterReplayOutcome.OutcomeValue.HandlerMissing, $"No event handler named '{target.Handler}' is registered in this process.");
        }

        if (context.DryRun)
        {
            return Outcome(DeadLetterReplayOutcome.OutcomeValue.WouldReplay);
        }

        try
        {
            if (await replay.ReplayDeadLetterAsync(target.DeadLetterId, cancellationToken).ConfigureAwait(false))
            {
                return Outcome(DeadLetterReplayOutcome.OutcomeValue.Replayed);
            }
        }
        catch (InvalidOperationException)
        {
            // Resolved by a concurrent replay between selection and invocation: a no-op, not an error.
            return Outcome(DeadLetterReplayOutcome.OutcomeValue.AlreadyResolved);
        }

        var after = await queries.GetAsync(target.DeadLetterId, cancellationToken).ConfigureAwait(false);
        return Outcome(DeadLetterReplayOutcome.OutcomeValue.StillFailing, after?.ErrorMessage);
    }
}

internal sealed class ReplayDeadLettersAuditor : ICommandAuditor<ReplayDeadLetters, DeadLetterReplayResponse>
{
    public CommandAuditFacts Describe(ReplayDeadLetters command, Result<DeadLetterReplayResponse>? result)
    {
        var ids = command.Request?.DeadLetterIds;
        return new CommandAuditFacts
        {
            ObjectRef = ids is { Count: 1 } ? new ObjectRef(ModuleCode.PLT, "DeadLetter", ids[0].ToString("D")) : null,
            Changes = result is { IsSuccess: true }
                ? [.. result.Value.Value.Results.Where(r => r.Outcome == DeadLetterReplayOutcome.OutcomeValue.Replayed)
                    .Select(r => new AuditChange($"deadLetter.{r.DeadLetterId:D}.status", JsonValue.Create("Parked"), JsonValue.Create("Replayed")))]
                : [],
            BusinessKeys = ids is { Count: > 0 } ? BusinessKeys.Empty.With("deadLetterIds", string.Join(',', ids)) : BusinessKeys.Empty,
        };
    }
}

/// <summary><c>plt.DeadLetter.discard</c> (BO-PLT-04): gives a parked dead letter up. Reason required, audited, dry-run capable; older than the threshold needs an Approved maker-checker request.</summary>
internal sealed record DiscardDeadLetters(DeadLetterDiscardRequest Request) : ICommand<DeadLetterDiscardResponse>
{
    public const int MaxPerCall = 100;
}

internal sealed class DiscardDeadLettersValidator : AbstractValidator<DiscardDeadLetters>
{
    public DiscardDeadLettersValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.DeadLetterIds).NotNull().Must(ids => ids.Count is >= 1 and <= DiscardDeadLetters.MaxPerCall)
            .WithMessage($"Name between 1 and {DiscardDeadLetters.MaxPerCall} dead letters.").When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => r.Trim().Length is >= 5 and <= 500)
            .WithMessage("The reason is required (5 to 500 characters).").When(c => c.Request is not null);
    }
}

internal sealed class DiscardDeadLettersHandler(
    DeadLetterQueries queries, NpgsqlDataSource dataSource, RequestContext context, IClock clock, IOptions<DeadLetterOptions> options)
    : ICommandHandler<DiscardDeadLetters, DeadLetterDiscardResponse>
{
    public async Task<Result<DeadLetterDiscardResponse>> HandleAsync(DiscardDeadLetters command, CancellationToken cancellationToken)
    {
        var reason = command.Request.Reason.Trim();
        context.Reason = reason;
        var ids = command.Request.DeadLetterIds.Distinct().ToList();
        var found = (await queries.SelectAsync(ids, new DeadLetterFilter(), ids.Count, cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.DeadLetterId);
        if (found.Count == 0)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "None of the named dead letters exists.");
        }

        var parked = found.Values.Where(t => t.Status == DeadLetterStatus.Parked).ToList();
        if (parked.Count == 0)
        {
            return DomainError.Of(ModuleCode.PLT, DeadLetterErrors.Resolved, "Every named dead letter was already replayed or discarded; nothing to do.");
        }

        var threshold = clock.Now.ToUtcDateTime().AddTicks(-TimeSpan.TicksPerDay * Math.Max(0, options.Value.ApprovalAfterDays));
        var old = parked.Where(t => t.ParkedAt.ToUtcDateTime() < threshold).Select(t => t.DeadLetterId).ToList();
        if (old.Count > 0)
        {
            var approved = await ApprovedAsync(old, cancellationToken).ConfigureAwait(false);
            var missing = old.Where(id => !approved.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                return DomainError.Of(ModuleCode.PLT, DeadLetterErrors.DiscardApprovalRequired,
                    $"Dead letters parked longer than {options.Value.ApprovalAfterDays} days need an Approved {DeadLetterErrors.DiscardApprovalType} request decided by someone else: {string.Join(", ", missing)}.");
            }
        }

        var discarded = new HashSet<Guid>();
        if (context.DryRun)
        {
            discarded.UnionWith(parked.Select(t => t.DeadLetterId));
        }
        else
        {
            await using var update = dataSource.CreateCommand(
                """
                UPDATE plt.outbox_dead_letter
                SET status = 'Discarded', resolved_at = @now, resolution_reason = @reason
                WHERE dead_letter_id = ANY(@ids) AND status = 'Parked'
                RETURNING dead_letter_id
                """);
            update.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.Now.ToUtcDateTime() });
            update.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = reason });
            update.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = parked.Select(t => t.DeadLetterId).ToArray() });
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                discarded.Add(reader.GetGuid(0));
            }
        }

        var results = ids.Select(id => new DeadLetterDiscardOutcome
        {
            DeadLetterId = id,
            Outcome = discarded.Contains(id)
                ? context.DryRun ? DeadLetterDiscardOutcome.OutcomeValue.WouldDiscard : DeadLetterDiscardOutcome.OutcomeValue.Discarded
                : found.ContainsKey(id) ? DeadLetterDiscardOutcome.OutcomeValue.AlreadyResolved : DeadLetterDiscardOutcome.OutcomeValue.NotFound,
        }).ToList();
        return new DeadLetterDiscardResponse { DryRun = context.DryRun, Discarded = discarded.Count, Skipped = ids.Count - discarded.Count, Results = results };
    }

    /// <summary>The dead letters with an Approved discard request that somebody other than the discarding actor decided (four eyes).</summary>
    private async Task<HashSet<Guid>> ApprovedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT DISTINCT object_id FROM plt.approval_request
            WHERE legal_entity = @le AND approval_type = @type AND object_module = 'PLT' AND object_type = 'DeadLetter'
              AND object_id = ANY(@ids) AND status = 'Approved'
              AND NOT (checker_kind = @kind AND checker_id = @actor)
            """);
        command.Parameters.Add(new NpgsqlParameter("le", NpgsqlDbType.Text) { Value = context.LegalEntity?.Value ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = DeadLetterErrors.DiscardApprovalType });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids.Select(i => i.ToString("D")).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = context.Actor.KindCode });
        command.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Text) { Value = context.Actor.Id });
        var result = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}

internal sealed class DiscardDeadLettersAuditor : ICommandAuditor<DiscardDeadLetters, DeadLetterDiscardResponse>
{
    public CommandAuditFacts Describe(DiscardDeadLetters command, Result<DeadLetterDiscardResponse>? result)
    {
        var ids = command.Request?.DeadLetterIds;
        return new CommandAuditFacts
        {
            ObjectRef = ids is { Count: 1 } ? new ObjectRef(ModuleCode.PLT, "DeadLetter", ids[0].ToString("D")) : null,
            Changes = result is { IsSuccess: true } && !result.Value.Value.DryRun
                ? [.. result.Value.Value.Results.Where(r => r.Outcome == DeadLetterDiscardOutcome.OutcomeValue.Discarded)
                    .Select(r => new AuditChange($"deadLetter.{r.DeadLetterId:D}.status", JsonValue.Create("Parked"), JsonValue.Create("Discarded"))),
                    new AuditChange("deadLetter.discardReason", null, JsonValue.Create(command.Request!.Reason.Trim()))]
                : [],
            BusinessKeys = ids is { Count: > 0 } ? BusinessKeys.Empty.With("deadLetterIds", string.Join(',', ids)) : BusinessKeys.Empty,
        };
    }
}

/// <summary>REST facade of <c>plt.DeadLetter.*</c> (contracts/openapi/plt.yaml). Platform.Admin only.</summary>
[ApiController]
[Route("api/plt/v1/dead-letters")]
internal sealed class DeadLettersController : ControllerBase
{
    /// <summary>plt.DeadLetter.list.</summary>
    [HttpGet]
    [Authorize(Policy = DeadLetterPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? eventType, [FromQuery] string? module, [FromQuery] string? handler, [FromQuery] DeadLetterStatus? status,
        [FromQuery] DateTimeOffset? parkedFrom, [FromQuery] DateTimeOffset? parkedTo, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] DeadLetterQueries queries, CancellationToken cancellationToken)
    {
        var filter = new DeadLetterFilter
        {
            EventType = eventType, Module = module, Handler = handler, Status = status,
            ParkedFrom = parkedFrom is { } f ? Instant.FromDateTimeOffset(f) : null,
            ParkedTo = parkedTo is { } t ? Instant.FromDateTimeOffset(t) : null,
        };
        return (await queries.ListAsync(filter, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>plt.DeadLetter.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = DeadLetterPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] DeadLetterQueries queries, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid) ? await queries.GetAsync(guid, cancellationToken).ConfigureAwait(false) : null;
        return found is null
            ? ((Result<DeadLetterListItem>)DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The dead letter does not exist.")).ToHttpResult(HttpContext)
            : Results.Ok(found);
    }

    /// <summary>plt.DeadLetter.replay: Idempotency-Key required; <c>?dryRun=true</c> reports what would be replayed.</summary>
    [HttpPost("replay")]
    [Authorize(Policy = DeadLetterPermissions.Replay)]
    public async Task<IResult> ReplayAsync(
        [FromBody] DeadLetterReplayRequest body, [FromServices] ICommandHandler<ReplayDeadLetters, DeadLetterReplayResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReplayDeadLetters(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.DeadLetter.discard: Idempotency-Key required; <c>?dryRun=true</c> reports what would be discarded.</summary>
    [HttpPost("discard")]
    [Authorize(Policy = DeadLetterPermissions.Discard)]
    public async Task<IResult> DiscardAsync(
        [FromBody] DeadLetterDiscardRequest body, [FromServices] ICommandHandler<DiscardDeadLetters, DeadLetterDiscardResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DiscardDeadLetters(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
