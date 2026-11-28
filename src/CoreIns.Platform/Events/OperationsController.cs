using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.DeadLetters;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Events;

/// <summary>Permission names of the platform operations (BO-PLT-04); granted in <c>permissions/plt.json</c>.</summary>
internal static class OperationsPermissions
{
    public const string ConsumerReplay = "plt.Consumer.replay";
    public const string ExchangeSearch = "plt.Exchange.search";
    public const string TransferList = "plt.Transfer.list";
    public const string TransferRetry = "plt.Transfer.retry";
    public const string SchemaRegister = "plt.Schema.register";
    public const string SchemaCheck = "plt.Schema.check";
    public const string OutboxPublish = "plt.Outbox.publish";
}

/// <summary>Error names of the platform operations.</summary>
internal static class OperationsErrors
{
    public const string TestTenantOnly = "TEST-TENANT-ONLY";
    public const string EventTypeNotAllowed = "EVENT-TYPE-NOT-ALLOWED";
    public const string Incompatible = "INCOMPATIBLE";
    public const string NotRetryable = "NOT-RETRYABLE";
}

/// <summary>Backward compatibility of two JSON Schemas (BO-PLT-04): a new version may not remove a property, change its type, or add a required property.</summary>
internal static class SchemaCompatibility
{
    public static List<string> Issues(JsonObject previous, JsonObject next)
    {
        var issues = new List<string>();
        Compare(previous, next, string.Empty, issues);
        return issues;
    }

    private static void Compare(JsonObject previous, JsonObject next, string path, List<string> issues)
    {
        var (oldType, newType) = (TypeOf(previous), TypeOf(next));
        if (oldType is not null && oldType != newType)
        {
            issues.Add($"type of '{(path.Length == 0 ? "(root)" : path)}' changed from {oldType} to {newType ?? "(none)"}");
            return;
        }

        var oldProps = previous["properties"] as JsonObject ?? [];
        var newProps = next["properties"] as JsonObject ?? [];
        foreach (var (name, oldValue) in oldProps)
        {
            var child = path.Length == 0 ? name : $"{path}.{name}";
            if (!newProps.TryGetPropertyValue(name, out var newValue))
            {
                issues.Add($"property '{child}' was removed");
            }
            else if (oldValue is JsonObject oldObject && newValue is JsonObject newObject)
            {
                Compare(oldObject, newObject, child, issues);
            }
        }

        var oldRequired = RequiredOf(previous);
        foreach (var name in RequiredOf(next).Except(oldRequired, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            issues.Add($"property '{(path.Length == 0 ? name : $"{path}.{name}")}' became required");
        }
    }

    private static string? TypeOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join('|', a.Select(x => x?.ToString()).Where(x => x is not null).Order(StringComparer.Ordinal)),
        _ => null,
    };

    private static IEnumerable<string> RequiredOf(JsonObject schema) =>
        schema["required"] is JsonArray array ? array.Select(x => x?.ToString()).Where(x => x is not null).Select(x => x!) : [];
}

/// <summary>Storage and reads of the schema registry, message exchange log and file transfers (tables of migration PlatformOperations).</summary>
internal sealed partial class OperationsStore(NpgsqlDataSource dataSource, IClock clock)
{
    public const int MaxSchemaBytes = 256 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,200}$", RegexOptions.CultureInvariant)]
    public static partial Regex SubjectPattern();

    public async Task<(int Version, JsonObject Schema, string Hash)?> LatestSchemaAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT version, schema_json::text, schema_hash FROM plt.schema_registry WHERE subject = @s ORDER BY version DESC LIMIT 1");
        command.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = subject });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt32(0), JsonNode.Parse(reader.GetString(1))!.AsObject(), reader.GetString(2))
            : null;
    }

    /// <summary>Inserts the next version of a subject under an advisory lock; returns the version.</summary>
    public async Task<int> InsertSchemaAsync(string subject, JsonObject schema, string hash, ActorRef actor, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@s))", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = "schema:" + subject });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO plt.schema_registry (subject, version, schema_json, schema_hash, registered_by, registered_at)
            SELECT @s, coalesce(max(version), 0) + 1, @json::jsonb, @hash, @by, @now FROM plt.schema_registry WHERE subject = @s
            RETURNING version
            """,
            connection,
            transaction);
        insert.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = subject });
        insert.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Text) { Value = schema.ToJsonString() });
        insert.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Text) { Value = hash });
        insert.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text) { Value = ApprovalActor(actor) });
        insert.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.Now.ToUtcDateTime() });
        var version = (int)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return version;
    }

    private static string ApprovalActor(ActorRef actor) => $"{actor.KindCode}:{actor.Id}";

    public async Task<Result<ExchangeSearchPage>> SearchExchangesAsync(
        string legalEntity, string? partyRef, string? counterparty, string? direction, Instant? from, Instant? to, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take)
            || !KeysetPaging.TryDecode(cursor, 2, out var parts)
            || (parts is not null && (!KeysetPaging.TryLong(parts[0], out _) || !Guid.TryParse(parts[1], out _)))
            || (direction is not null && direction is not ("Inbound" or "Outbound")))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "cursor is malformed, limit is outside 1..200, or direction is not Inbound/Outbound.");
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT exchange_id, direction, counterparty, party_ref, channel, operation, status, correlation_id, started_at, completed_at
            FROM plt.message_exchange
            WHERE legal_entity = @le
              AND (@party::text IS NULL OR party_ref = @party)
              AND (@counterparty::text IS NULL OR counterparty = @counterparty)
              AND (@direction::text IS NULL OR direction = @direction)
              AND (@from::timestamptz IS NULL OR started_at >= @from)
              AND (@to::timestamptz IS NULL OR started_at < @to)
              AND (@after_at::timestamptz IS NULL OR (started_at, exchange_id) < (@after_at, @after_id))
            ORDER BY started_at DESC, exchange_id DESC
            LIMIT @take
            """);
        command.Parameters.Add(new NpgsqlParameter("le", NpgsqlDbType.Text) { Value = legalEntity });
        command.Parameters.Add(Text("party", partyRef));
        command.Parameters.Add(Text("counterparty", counterparty));
        command.Parameters.Add(Text("direction", direction));
        command.Parameters.Add(Time("from", from));
        command.Parameters.Add(Time("to", to));
        AddAfter(command, parts);
        command.Parameters.Add(new NpgsqlParameter<int>("take", take + 1));
        var rows = new List<ExchangeSearchItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ExchangeSearchItem
                {
                    ExchangeId = reader.GetGuid(0),
                    Direction = Enum.Parse<ExchangeSearchItem.DirectionValue>(reader.GetString(1)),
                    Counterparty = reader.GetString(2),
                    PartyRef = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Channel = reader.GetString(4),
                    Operation = reader.GetString(5),
                    Status = reader.GetString(6),
                    CorrelationId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    StartedAt = Instant.FromUtcDateTime(reader.GetDateTime(8)),
                    CompletedAt = reader.IsDBNull(9) ? null : Instant.FromUtcDateTime(reader.GetDateTime(9)),
                });
            }
        }

        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.StartedAt.ToUtcDateTime().Ticks), KeysetPaging.Part(r.ExchangeId)));
        return new ExchangeSearchPage { Items = page, NextCursor = next, Limit = take };
    }

    public async Task<Result<TransferListPage>> ListTransfersAsync(
        string legalEntity, string? partner, string? status, string? direction, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take)
            || !KeysetPaging.TryDecode(cursor, 2, out var parts)
            || (parts is not null && (!KeysetPaging.TryLong(parts[0], out _) || !Guid.TryParse(parts[1], out _)))
            || (direction is not null && direction is not ("Inbound" or "Outbound"))
            || (status is not null && status is not ("Pending" or "Sent" or "Failed")))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "cursor is malformed, limit is outside 1..200, or status/direction is not a known value.");
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT transfer_id, direction, partner, file_name, status, attempts, last_error, created_at, updated_at
            FROM plt.file_transfer
            WHERE legal_entity = @le
              AND (@partner::text IS NULL OR partner = @partner)
              AND (@status::text IS NULL OR status = @status)
              AND (@direction::text IS NULL OR direction = @direction)
              AND (@after_at::timestamptz IS NULL OR (created_at, transfer_id) < (@after_at, @after_id))
            ORDER BY created_at DESC, transfer_id DESC
            LIMIT @take
            """);
        command.Parameters.Add(new NpgsqlParameter("le", NpgsqlDbType.Text) { Value = legalEntity });
        command.Parameters.Add(Text("partner", partner));
        command.Parameters.Add(Text("status", status));
        command.Parameters.Add(Text("direction", direction));
        AddAfter(command, parts);
        command.Parameters.Add(new NpgsqlParameter<int>("take", take + 1));
        var rows = new List<TransferListItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new TransferListItem
                {
                    TransferId = reader.GetGuid(0),
                    Direction = Enum.Parse<TransferListItem.DirectionValue>(reader.GetString(1)),
                    Partner = reader.GetString(2),
                    FileName = reader.GetString(3),
                    Status = Enum.Parse<TransferListItem.StatusValue>(reader.GetString(4)),
                    Attempts = reader.GetInt32(5),
                    LastError = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CreatedAt = Instant.FromUtcDateTime(reader.GetDateTime(7)),
                    UpdatedAt = Instant.FromUtcDateTime(reader.GetDateTime(8)),
                });
            }
        }

        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.CreatedAt.ToUtcDateTime().Ticks), KeysetPaging.Part(r.TransferId)));
        return new TransferListPage { Items = page, NextCursor = next, Limit = take };
    }

    /// <summary>Failed transfers (named, or of a partner) go back to Pending for the transfer worker; returns the ids that were reset.</summary>
    public async Task<IReadOnlyList<Guid>> RetryTransfersAsync(string legalEntity, IReadOnlyCollection<Guid>? ids, string? partner, int max, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE plt.file_transfer SET status = 'Pending', updated_at = @now
            WHERE transfer_id IN (
                SELECT transfer_id FROM plt.file_transfer
                WHERE legal_entity = @le AND status = 'Failed'
                  AND (@ids::uuid[] IS NULL OR transfer_id = ANY(@ids))
                  AND (@partner::text IS NULL OR partner = @partner)
                ORDER BY created_at, transfer_id LIMIT @max FOR UPDATE)
            RETURNING transfer_id
            """);
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = clock.Now.ToUtcDateTime() });
        command.Parameters.Add(new NpgsqlParameter("le", NpgsqlDbType.Text) { Value = legalEntity });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids is { Count: > 0 } ? ids.ToArray() : DBNull.Value });
        command.Parameters.Add(Text("partner", partner));
        command.Parameters.Add(new NpgsqlParameter<int>("max", max));
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Time(string name, Instant? value) => new(name, NpgsqlDbType.TimestampTz) { Value = (object?)value?.ToUtcDateTime() ?? DBNull.Value };

    private static void AddAfter(NpgsqlCommand command, string[]? parts)
    {
        command.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz)
        {
            Value = parts is null ? DBNull.Value : new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc),
        });
        command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = parts is null ? DBNull.Value : Guid.Parse(parts[1]) });
    }
}

// ---- plt.Schema.check / plt.Schema.register ----

internal sealed record CheckSchema(SchemaCheckRequest Request) : ICommand<SchemaCheckResponse>;

internal sealed record RegisterSchema(SchemaRegisterRequest Request) : ICommand<SchemaRegisterResponse>;

internal static class SchemaInput
{
    /// <summary>Null when the subject and schema are acceptable, otherwise the problem.</summary>
    public static string? Problem(string? subject, JsonElement schema, out JsonObject? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(subject) || !OperationsStore.SubjectPattern().IsMatch(subject))
        {
            return "subject must be 1..200 characters of letters, digits and _ . : -.";
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "schema must be a JSON object.";
        }

        var text = schema.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > OperationsStore.MaxSchemaBytes)
        {
            return $"schema is larger than {OperationsStore.MaxSchemaBytes} bytes.";
        }

        parsed = JsonNode.Parse(text)!.AsObject();
        return parsed["type"] is { } type && type is not JsonValue && type is not JsonArray ? "schema.type must be a string or an array of strings." : null;
    }

    public static string HashOf(JsonObject schema) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(schema))));

    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(',', o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(',', a.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };
}

internal sealed class CheckSchemaValidator : AbstractValidator<CheckSchema>
{
    public CheckSchemaValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request).Must(r => SchemaInput.Problem(r.Subject, r.Schema, out _) is null)
            .WithMessage(c => SchemaInput.Problem(c.Request.Subject, c.Request.Schema, out _) ?? string.Empty).When(c => c.Request is not null);
    }
}

internal sealed class RegisterSchemaValidator : AbstractValidator<RegisterSchema>
{
    public RegisterSchemaValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request).Must(r => SchemaInput.Problem(r.Subject, r.Schema, out _) is null)
            .WithMessage(c => SchemaInput.Problem(c.Request.Subject, c.Request.Schema, out _) ?? string.Empty).When(c => c.Request is not null);
    }
}

internal sealed class CheckSchemaHandler(OperationsStore store) : ICommandHandler<CheckSchema, SchemaCheckResponse>
{
    public async Task<Result<SchemaCheckResponse>> HandleAsync(CheckSchema command, CancellationToken cancellationToken)
    {
        _ = SchemaInput.Problem(command.Request.Subject, command.Request.Schema, out var schema);
        var latest = await store.LatestSchemaAsync(command.Request.Subject, cancellationToken).ConfigureAwait(false);
        var issues = latest is null ? [] : SchemaCompatibility.Issues(latest.Value.Schema, schema!);
        return new SchemaCheckResponse { Subject = command.Request.Subject, Compatible = issues.Count == 0, Version = latest?.Version, Issues = issues };
    }
}

internal sealed class RegisterSchemaHandler(OperationsStore store, RequestContext context) : ICommandHandler<RegisterSchema, SchemaRegisterResponse>
{
    public async Task<Result<SchemaRegisterResponse>> HandleAsync(RegisterSchema command, CancellationToken cancellationToken)
    {
        var subject = command.Request.Subject;
        _ = SchemaInput.Problem(subject, command.Request.Schema, out var schema);
        var hash = SchemaInput.HashOf(schema!);
        var latest = await store.LatestSchemaAsync(subject, cancellationToken).ConfigureAwait(false);
        if (latest is { } existing && existing.Hash == hash)
        {
            return new SchemaRegisterResponse { Subject = subject, Compatible = true, Version = existing.Version, Issues = [] };
        }

        var issues = latest is null ? [] : SchemaCompatibility.Issues(latest.Value.Schema, schema!);
        if (issues.Count > 0)
        {
            return DomainError.Of(ModuleCode.PLT, OperationsErrors.Incompatible, $"Schema '{subject}' is not backward compatible with version {latest!.Value.Version}: {string.Join("; ", issues)}.");
        }

        var version = await store.InsertSchemaAsync(subject, schema!, hash, context.Actor, cancellationToken).ConfigureAwait(false);
        return new SchemaRegisterResponse { Subject = subject, Compatible = true, Version = version, Issues = [] };
    }
}

internal sealed class RegisterSchemaAuditor : ICommandAuditor<RegisterSchema, SchemaRegisterResponse>
{
    public CommandAuditFacts Describe(RegisterSchema command, Result<SchemaRegisterResponse>? result) => new()
    {
        ObjectRef = command.Request is null ? null : new ObjectRef(ModuleCode.PLT, "Schema", command.Request.Subject),
        Changes = result is { IsSuccess: true } ? [new Audit.AuditChange("schema.version", null, JsonValue.Create(result.Value.Value.Version))] : [],
    };
}

// ---- plt.Transfer.retry ----

internal sealed record RetryTransfers(TransferRetryRequest Request) : ICommand<TransferRetryResponse>
{
    public const int MaxPerCall = 100;
}

internal sealed class RetryTransfersValidator : AbstractValidator<RetryTransfers>
{
    public RetryTransfersValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request).Must(r => r.TransferIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(r.Partner))
            .WithMessage("Name transferIds or a partner.").When(c => c.Request is not null);
        RuleFor(c => c.Request.TransferIds).Must(ids => ids is null || ids.Count <= RetryTransfers.MaxPerCall)
            .WithMessage($"At most {RetryTransfers.MaxPerCall} transfers can be named.").When(c => c.Request is not null);
        RuleFor(c => c.Request.Partner).MaximumLength(200).When(c => c.Request?.Partner is not null);
    }
}

internal sealed class RetryTransfersHandler(OperationsStore store, RequestContext context) : ICommandHandler<RetryTransfers, TransferRetryResponse>
{
    public async Task<Result<TransferRetryResponse>> HandleAsync(RetryTransfers command, CancellationToken cancellationToken)
    {
        var ids = command.Request.TransferIds?.Distinct().ToList();
        var retried = await store.RetryTransfersAsync(context.LegalEntity?.Value ?? string.Empty, ids, command.Request.Partner, RetryTransfers.MaxPerCall, cancellationToken)
            .ConfigureAwait(false);
        var skipped = ids is null ? [] : ids.Except(retried).ToList();
        if (retried.Count == 0 && ids is { Count: > 0 })
        {
            return DomainError.Of(ModuleCode.PLT, OperationsErrors.NotRetryable, "None of the named transfers is Failed in your legal entity; only Failed transfers can be retried.");
        }

        return new TransferRetryResponse { Retried = retried, Skipped = skipped };
    }
}

internal sealed class RetryTransfersAuditor : ICommandAuditor<RetryTransfers, TransferRetryResponse>
{
    public CommandAuditFacts Describe(RetryTransfers command, Result<TransferRetryResponse>? result) => new()
    {
        Changes = result is { IsSuccess: true }
            ? [.. result.Value.Value.Retried.Select(id => new Audit.AuditChange($"transfer.{id:D}.status", JsonValue.Create("Failed"), JsonValue.Create("Pending")))]
            : [],
    };
}

// ---- plt.Consumer.replay ----

internal sealed record ReplayConsumer(ConsumerReplayRequest Request) : ICommand<ConsumerReplayResponse>
{
    /// <summary>The most archived events one replay call re-delivers; a wider range is refused so the caller narrows it.</summary>
    public const int MaxEvents = 1000;

    public const int MaxDeadLetters = 100;
}

internal sealed class ReplayConsumerValidator : AbstractValidator<ReplayConsumer>
{
    public ReplayConsumerValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request).Must(r => r.DeadLetterIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(r.Consumer))
            .WithMessage("Name deadLetterIds, or a consumer with a recordedFrom/recordedTo range.").When(c => c.Request is not null);
        RuleFor(c => c.Request).Must(r => r.DeadLetterIds is { Count: > 0 } || r.RecordedFrom is not null || r.RecordedTo is not null || r.AggregateId is not null)
            .WithMessage("A consumer replay needs recordedFrom, recordedTo or an aggregate: an unbounded replay is refused.").When(c => c.Request is not null);
        RuleFor(c => c.Request.DeadLetterIds).Must(ids => ids is null || ids.Count <= ReplayConsumer.MaxDeadLetters)
            .WithMessage($"At most {ReplayConsumer.MaxDeadLetters} dead letters can be named.").When(c => c.Request is not null);
        RuleFor(c => c.Request.Consumer).MaximumLength(200).When(c => c.Request?.Consumer is not null);
    }
}

internal sealed class ReplayConsumerHandler(DeadLetterQueries queries, OutboxReplayService replay, EventHandlerRegistry registry, RequestContext context)
    : ICommandHandler<ReplayConsumer, ConsumerReplayResponse>
{
    public async Task<Result<ConsumerReplayResponse>> HandleAsync(ReplayConsumer command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        return request.DeadLetterIds is { Count: > 0 }
            ? await ReplayDeadLettersAsync(request.DeadLetterIds.Distinct().ToList(), cancellationToken).ConfigureAwait(false)
            : await ReplayRangeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ConsumerReplayResponse>> ReplayRangeAsync(ConsumerReplayRequest request, CancellationToken cancellationToken)
    {
        var consumer = request.Consumer!.Trim();
        if (registry.Find(consumer) is null)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, $"No event handler named '{consumer}' is registered.");
        }

        var filter = new ReplayFilter { RecordedFrom = request.RecordedFrom, RecordedTo = request.RecordedTo, AggregateType = request.AggregateType, AggregateId = request.AggregateId };
        var matched = await replay.CountAsync(consumer, filter, cancellationToken).ConfigureAwait(false);
        if (matched > ReplayConsumer.MaxEvents)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, $"The range matches {matched} events; at most {ReplayConsumer.MaxEvents} are replayed per call. Narrow the range.");
        }

        var delivered = context.DryRun ? 0 : await replay.ReplayAsync(consumer, filter, cancellationToken).ConfigureAwait(false);
        return new ConsumerReplayResponse { DryRun = context.DryRun, Consumer = consumer, Matched = matched, Delivered = delivered, Skipped = 0, StillFailing = 0 };
    }

    private async Task<Result<ConsumerReplayResponse>> ReplayDeadLettersAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var found = (await queries.SelectAsync(ids, new DeadLetterFilter(), ids.Count, cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.DeadLetterId);
        var delivered = 0;
        var failing = 0;
        var skipped = 0;
        var matched = 0;
        foreach (var id in ids)
        {
            // A resolved (Replayed/Discarded) or unknown dead letter is a no-op: nothing is re-run.
            if (!found.TryGetValue(id, out var target) || target.Status != DeadLetterStatus.Parked || registry.Find(target.Handler) is null)
            {
                skipped++;
                continue;
            }

            matched++;
            if (context.DryRun)
            {
                continue;
            }

            try
            {
                if (await replay.ReplayDeadLetterAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    delivered++;
                }
                else
                {
                    failing++;
                }
            }
            catch (InvalidOperationException)
            {
                skipped++;
                matched--;
            }
        }

        return new ConsumerReplayResponse { DryRun = context.DryRun, Consumer = null, Matched = matched, Delivered = delivered, Skipped = skipped, StillFailing = failing };
    }
}

internal sealed class ReplayConsumerAuditor : ICommandAuditor<ReplayConsumer, ConsumerReplayResponse>
{
    public CommandAuditFacts Describe(ReplayConsumer command, Result<ConsumerReplayResponse>? result) => new()
    {
        Changes = result is { IsSuccess: true } && !result.Value.Value.DryRun
            ? [new Audit.AuditChange("consumerReplay.delivered", null, JsonValue.Create(result.Value.Value.Delivered))]
            : [],
        BusinessKeys = command.Request?.Consumer is { Length: > 0 } consumer ? BusinessKeys.Empty.With("consumer", consumer) : BusinessKeys.Empty,
    };
}

// ---- plt.Outbox.publish ----

internal sealed record PublishOutbox(OutboxPublishRequest Request) : ICommand<OutboxPublishResponse>;

/// <summary>The events an administrator can publish by hand on a test tenant: event type to descriptor and payload record (the payload must deserialise into it).</summary>
internal static class OperationsEvents
{
    public static readonly IReadOnlyDictionary<string, (EventDescriptor Descriptor, Type Payload)> Allowed =
        new Dictionary<string, (EventDescriptor, Type)>(StringComparer.Ordinal)
        {
            ["ConfigChanged"] = (EventDescriptor.From(ConfigChangedV1.Descriptor), typeof(ConfigChangedV1)),
            ["FeatureFlagChanged"] = (EventDescriptor.From(FeatureFlagChangedV1.Descriptor), typeof(FeatureFlagChangedV1)),
        };
}

internal sealed class PublishOutboxValidator : AbstractValidator<PublishOutbox>
{
    public PublishOutboxValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.EventType).NotEmpty().MaximumLength(100).When(c => c.Request is not null);
        RuleFor(c => c.Request.AggregateType).NotEmpty().MaximumLength(100).When(c => c.Request is not null);
        RuleFor(c => c.Request.AggregateId).NotEmpty().MaximumLength(200).When(c => c.Request is not null);
        RuleFor(c => c.Request.Payload.ValueKind).Equal(JsonValueKind.Object).WithMessage("payload must be a JSON object.").When(c => c.Request is not null);
    }
}

internal sealed class PublishOutboxHandler(IEventPublisher publisher, RequestContext context, IOptions<StampOptions> stamp) : ICommandHandler<PublishOutbox, OutboxPublishResponse>
{
    public Task<Result<OutboxPublishResponse>> HandleAsync(PublishOutbox command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = context.LegalEntity?.Value ?? stamp.Value.LegalEntity ?? string.Empty;
        if (!legalEntity.EndsWith("-TEST", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<Result<OutboxPublishResponse>>(
                DomainError.Of(ModuleCode.PLT, OperationsErrors.TestTenantOnly, $"plt.Outbox.publish only works on a test tenant (legal entity ending -TEST); this one is '{legalEntity}'."));
        }

        if (!OperationsEvents.Allowed.TryGetValue(request.EventType, out var entry))
        {
            return Task.FromResult<Result<OutboxPublishResponse>>(
                DomainError.Of(ModuleCode.PLT, OperationsErrors.EventTypeNotAllowed, $"Event type '{request.EventType}' cannot be published by hand; allowed: {string.Join(", ", OperationsEvents.Allowed.Keys)}."));
        }

        try
        {
            var payload = JsonSerializer.Deserialize(request.Payload.GetRawText(), entry.Payload, SharedKernelJson.Options)
                ?? throw new JsonException("payload is empty.");
            var keys = BusinessKeys.From((request.BusinessKeys ?? new Dictionary<string, string>()).AsEnumerable());
            var envelope = publisher.Publish(new OutgoingEvent(entry.Descriptor, request.AggregateType, request.AggregateId, payload, keys));
            return Task.FromResult<Result<OutboxPublishResponse>>(new OutboxPublishResponse { EventId = envelope.EventId.Value, EventType = request.EventType });
        }
        catch (Exception ex) when (ex is JsonException or EnvelopeValidationException or FormatException or ArgumentException)
        {
            return Task.FromResult<Result<OutboxPublishResponse>>(DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, ex.Message));
        }
    }
}

internal sealed class PublishOutboxAuditor : ICommandAuditor<PublishOutbox, OutboxPublishResponse>
{
    public CommandAuditFacts Describe(PublishOutbox command, Result<OutboxPublishResponse>? result) => new()
    {
        ObjectRef = result is { IsSuccess: true } ? new ObjectRef(ModuleCode.PLT, "Event", result.Value.Value.EventId.ToString("D")) : null,
        Changes = result is { IsSuccess: true } ? [new Audit.AuditChange("outbox.published", null, JsonValue.Create(command.Request.EventType))] : [],
    };
}

/// <summary>REST facade of the platform operations (BO-PLT-04): consumer replay, exchange search, file transfers, schema registry, manual outbox publish. Platform.Admin (exchange search also compliance).</summary>
[ApiController]
[Route("api/plt/v1")]
internal sealed class OperationsController : ControllerBase
{
    /// <summary>plt.Consumer.replay.</summary>
    [HttpPost("consumers/replay")]
    [Authorize(Policy = OperationsPermissions.ConsumerReplay)]
    public async Task<IResult> ReplayConsumerAsync(
        [FromBody] ConsumerReplayRequest body, [FromServices] ICommandHandler<ReplayConsumer, ConsumerReplayResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReplayConsumer(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.Exchange.search.</summary>
    [HttpGet("exchanges/search")]
    [Authorize(Policy = OperationsPermissions.ExchangeSearch)]
    public async Task<IResult> SearchExchangesAsync(
        [FromQuery] string? partyRef, [FromQuery] string? counterparty, [FromQuery] string? direction, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] OperationsStore store, [FromServices] RequestContext context, CancellationToken cancellationToken) =>
        (await store.SearchExchangesAsync(
            context.LegalEntity?.Value ?? string.Empty, partyRef, counterparty, direction,
            from is { } f ? Instant.FromDateTimeOffset(f) : null, to is { } t ? Instant.FromDateTimeOffset(t) : null, cursor, limit, cancellationToken).ConfigureAwait(false))
        .ToHttpResult(HttpContext);

    /// <summary>plt.Transfer.list.</summary>
    [HttpGet("transfers")]
    [Authorize(Policy = OperationsPermissions.TransferList)]
    public async Task<IResult> ListTransfersAsync(
        [FromQuery] string? partner, [FromQuery] string? status, [FromQuery] string? direction, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] OperationsStore store, [FromServices] RequestContext context, CancellationToken cancellationToken) =>
        (await store.ListTransfersAsync(context.LegalEntity?.Value ?? string.Empty, partner, status, direction, cursor, limit, cancellationToken).ConfigureAwait(false))
        .ToHttpResult(HttpContext);

    /// <summary>plt.Transfer.retry.</summary>
    [HttpPost("transfers/retry")]
    [Authorize(Policy = OperationsPermissions.TransferRetry)]
    public async Task<IResult> RetryTransfersAsync(
        [FromBody] TransferRetryRequest body, [FromServices] ICommandHandler<RetryTransfers, TransferRetryResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RetryTransfers(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.Schema.register.</summary>
    [HttpPost("schemas/register")]
    [Authorize(Policy = OperationsPermissions.SchemaRegister)]
    public async Task<IResult> RegisterSchemaAsync(
        [FromBody] SchemaRegisterRequest body, [FromServices] ICommandHandler<RegisterSchema, SchemaRegisterResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RegisterSchema(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.Schema.check: read-only compatibility check (no key needed, nothing stored).</summary>
    [HttpPost("schemas/check")]
    [Authorize(Policy = OperationsPermissions.SchemaCheck)]
    public async Task<IResult> CheckSchemaAsync(
        [FromBody] SchemaCheckRequest body, [FromServices] ICommandHandler<CheckSchema, SchemaCheckResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CheckSchema(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.Outbox.publish: test tenants only.</summary>
    [HttpPost("outbox/publish")]
    [Authorize(Policy = OperationsPermissions.OutboxPublish)]
    public async Task<IResult> PublishOutboxAsync(
        [FromBody] OutboxPublishRequest body, [FromServices] ICommandHandler<PublishOutbox, OutboxPublishResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PublishOutbox(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
