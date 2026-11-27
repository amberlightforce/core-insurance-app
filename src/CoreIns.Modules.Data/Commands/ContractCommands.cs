using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Domain;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Modules.Data.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Commands;

/// <summary><c>dat.DataContract.register</c>.</summary>
internal sealed record RegisterDataContract(DataContractRegisterRequest Request) : ICommand<DataContractRegisterResponse>;

/// <summary><c>dat.Ingestion.replay</c>.</summary>
internal sealed record ReplayIngestion(IngestionReplayRequest Request) : ICommand<IngestionReplayResponse>;

/// <summary><c>dat.Quarantine.release</c>.</summary>
internal sealed record ReleaseQuarantine(QuarantineReleaseRequest Request) : ICommand<QuarantineReleaseResponse>;

internal static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string Label(int major, int minor) => string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}");
}

/// <summary>
/// Registers a contract version. The first version of a name is 1.0 (or the stated <c>major</c>); an identical document is a
/// no-op; a compatible change is the next minor; a breaking change (property removed, type changed, field newly required)
/// needs a higher <c>major</c> or is refused with DAT-ERR-001. <c>dryRun</c> returns the compatibility result without writing.
/// </summary>
internal sealed class RegisterDataContractHandler(DataDbContext db, IClock clock) : ICommandHandler<RegisterDataContract, DataContractRegisterResponse>
{
    public async Task<Result<DataContractRegisterResponse>> HandleAsync(RegisterDataContract command, CancellationToken cancellationToken)
    {
        var raw = command.Request.ContractDocument;
        if (raw is not { } element)
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "contractDocument is required.");
        }

        var (document, problems) = ContractDocument.Parse(element);
        if (document is null)
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, string.Join("; ", problems));
        }

        var latest = await db.DataContracts.AsNoTracking()
            .Where(c => c.Name == document.Name)
            .OrderByDescending(c => c.Major).ThenByDescending(c => c.Minor)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        int major;
        int minor;
        IReadOnlyList<string> breaking = [];
        if (latest is null)
        {
            major = document.Major ?? 1;
            minor = 0;
        }
        else
        {
            using var parsed = JsonDocument.Parse(latest.Document);
            var (previous, _) = ContractDocument.Parse(parsed.RootElement);
            if (previous is not null && Same(previous, document))
            {
                return Response(latest.Major, latest.Minor, compatible: true, [], "UNCHANGED");
            }

            major = document.Major ?? latest.Major;
            breaking = previous is null ? [] : ContractDocument.BreakingChanges(previous, document);
            if (major < latest.Major || (breaking.Count > 0 && major == latest.Major))
            {
                var why = major < latest.Major ? $"major {major} is below the registered major {latest.Major}" : string.Join("; ", breaking);
                return DomainError.Of(ModuleCode.DAT, "001", $"Breaking change without a new major version: {why}.");
            }

            minor = major == latest.Major ? latest.Minor + 1 : 0;
        }

        db.DataContracts.Add(new DataContractRow
        {
            ContractId = Guid.CreateVersion7(),
            Name = document.Name,
            Major = major,
            Minor = minor,
            Producer = document.Name[..3].ToUpperInvariant(),
            Document = ContractJson.ToElement(new
            {
                name = document.Name,
                major,
                requiredFields = document.RequiredFields,
                properties = document.Properties,
            }).GetRawText(),
            Compatibility = ContractJson.ToElement(new { compatible = true, breakingChanges = breaking }).GetRawText(),
            CreatedAt = clock.Now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Response(major, minor, compatible: true, breaking, latest is null ? "NEW" : "REGISTERED");
    }

    private static Result<DataContractRegisterResponse> Response(int major, int minor, bool compatible, IReadOnlyList<string> breaking, string outcome) =>
        new DataContractRegisterResponse
        {
            Version = ContractJson.ToElement(ContractJson.Label(major, minor)),
            CompatibilityResult = ContractJson.ToElement(new { compatible, breakingChanges = breaking, outcome }),
        };

    private static bool Same(ContractDocument a, ContractDocument b) =>
        (b.Major is null || a.Major == b.Major)
        && a.RequiredFields.Order(StringComparer.Ordinal).SequenceEqual(b.RequiredFields.Order(StringComparer.Ordinal), StringComparer.Ordinal)
        && a.Properties.Count == b.Properties.Count
        && a.Properties.All(p => b.Properties.TryGetValue(p.Key, out var kind) && kind == p.Value);
}

/// <summary>
/// Re-validates the ledger events of one contract in a time window against the contract now in force (idempotent: the ledger is
/// append-only, so a repeat gives the same counts). Scope above 10 000 events is refused (DAT-ERR-002).
/// Valid events are also re-driven through the conformed projector (idempotent: already projected events are skipped).
/// TODO(hardening): an approval for a broader scope; a full rebuild (truncate and re-project in occurred order).
/// </summary>
internal sealed class ReplayIngestionHandler(DataDbContext db, EventIngestion ingestion, ConformedProjector projector) : ICommandHandler<ReplayIngestion, IngestionReplayResponse>
{
    public const int MaxScope = 10_000;
    private const int Batch = 500;

    public async Task<Result<IngestionReplayResponse>> HandleAsync(ReplayIngestion command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var contract = request.Contract is { ValueKind: JsonValueKind.String } c ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(contract))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "contract (e.g. pol.PolicyIssued) is required.");
        }

        if (!TryInstant(request.From, out var from) || !TryInstant(request.To, out var to))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "from and to must be RFC 3339 instants.");
        }

        var query = db.EventLedger.AsNoTracking().Where(e => e.RegistryName == contract);
        if (from is { } f)
        {
            query = query.Where(e => e.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(e => e.OccurredAt <= t);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        if (total > MaxScope)
        {
            return DomainError.Of(ModuleCode.DAT, "002", $"The scope holds {total} events; narrow it to at most {MaxScope}.");
        }

        var valid = 0;
        var projected = 0;
        var invalid = new List<Guid>();
        Guid after = Guid.Empty;
        while (true)
        {
            var page = await query.Where(e => e.EventId > after).OrderBy(e => e.EventId).Take(Batch)
                .Select(e => new { e.EventId, e.Envelope }).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var row in page)
            {
                var problems = JsonNode.Parse(row.Envelope) is JsonObject wire
                    ? await ingestion.WireProblemsAsync(wire, cancellationToken).ConfigureAwait(false)
                    : ["envelope is not an object"];
                if (problems.Count == 0)
                {
                    valid++;
                    if (await projector.ProjectAsync((JsonObject)JsonNode.Parse(row.Envelope)!, cancellationToken).ConfigureAwait(false) == ProjectionOutcome.Projected)
                    {
                        projected++;
                    }
                }
                else
                {
                    invalid.Add(row.EventId);
                }
            }

            after = page[^1].EventId;
        }

        return new IngestionReplayResponse
        {
            JobId = new JobId(Guid.CreateVersion7()),
            Counts = ContractJson.ToElement(new { scanned = total, valid, invalid = invalid.Count, invalidEventIds = invalid.Take(50), ingested = 0, projected }),
        };
    }

    private static bool TryInstant(JsonElement? element, out Instant? instant)
    {
        instant = null;
        if (element is not { } e || e.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (e.ValueKind == JsonValueKind.String && Instant.TryParse(e.GetString(), out var parsed))
        {
            instant = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Re-validates quarantined events (by ids, or by a filter <c>{ "contract": "pol.PolicyIssued" }</c>) against the contract now in
/// force and writes the ones that pass to the ledger. An event that still fails stays quarantined. Outcome per record.
/// </summary>
internal sealed class ReleaseQuarantineHandler(DataDbContext db, EventIngestion ingestion, IClock clock) : ICommandHandler<ReleaseQuarantine, QuarantineReleaseResponse>
{
    public const int MaxPerCall = 500;
    private static readonly string[] EnvelopeNotObject = ["envelope is not an object"];

    public async Task<Result<QuarantineReleaseResponse>> HandleAsync(ReleaseQuarantine command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var ids = request.Ids?.Distinct().ToList() ?? [];
        string? contract = null;
        if (request.Filter is { ValueKind: JsonValueKind.Object } filter && filter.TryGetProperty("contract", out var c) && c.ValueKind == JsonValueKind.String)
        {
            contract = c.GetString();
        }

        if (ids.Count == 0 && string.IsNullOrWhiteSpace(contract))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "ids or filter.contract is required.");
        }

        if (ids.Count > MaxPerCall)
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, $"At most {MaxPerCall} ids per call.");
        }

        var rows = ids.Count > 0
            ? await db.Quarantine.Where(q => ids.Contains(q.QuarantineId)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await db.Quarantine.Where(q => q.RegistryName == contract && q.Status == "QUARANTINED").OrderBy(q => q.QuarantineId).Take(MaxPerCall)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        var outcomes = new List<object>();
        foreach (var missing in ids.Except(rows.Select(r => r.QuarantineId)))
        {
            outcomes.Add(new { id = missing, outcome = "NOT_FOUND" });
        }

        var now = clock.Now;
        foreach (var row in rows)
        {
            if (row.Status == "RELEASED")
            {
                outcomes.Add(new { id = row.QuarantineId, eventId = row.EventId, outcome = "ALREADY_RELEASED" });
                continue;
            }

            if (JsonNode.Parse(row.Envelope) is not JsonObject wire)
            {
                outcomes.Add(new { id = row.QuarantineId, eventId = row.EventId, outcome = "STILL_INVALID", reasons = EnvelopeNotObject });
                continue;
            }

            var problems = await ingestion.WireProblemsAsync(wire, cancellationToken).ConfigureAwait(false);
            if (problems.Count > 0)
            {
                outcomes.Add(new { id = row.QuarantineId, eventId = row.EventId, outcome = "STILL_INVALID", reasons = problems });
                continue;
            }

            var stored = await ingestion.StoreAsync(wire, [], cancellationToken).ConfigureAwait(false);
            row.Status = "RELEASED";
            row.ReleasedAt = now;
            outcomes.Add(new { id = row.QuarantineId, eventId = row.EventId, outcome = stored == IngestOutcome.Ingested ? "RELEASED" : "RELEASED_DUPLICATE" });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new QuarantineReleaseResponse { OutcomePerRecord = ContractJson.ToElement(outcomes) };
    }
}

/// <summary>Reads for <c>dat.DataContract.get</c> / <c>list</c>, and the quarantine and ledger counts used by tests and the UI.</summary>
internal sealed class DataContractReader(DataDbContext db)
{
    /// <summary>Versions of one contract; <paramref name="idOrName"/> is the contract name or the id of any of its versions.</summary>
    public async Task<DataContractGetResponse?> GetAsync(string idOrName, string? producer, CancellationToken cancellationToken)
    {
        var name = idOrName;
        if (Guid.TryParse(idOrName, out var id))
        {
            name = await db.DataContracts.AsNoTracking().Where(c => c.ContractId == id).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (name is null)
        {
            return null;
        }

        var versions = await db.DataContracts.AsNoTracking().Where(c => c.Name == name).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0 || (producer is not null && !string.Equals(versions[0].Producer, producer, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new DataContractGetResponse { ContractVersions = ContractJson.ToElement(Summaries(versions)) };
    }

    /// <summary>One item per contract name, ordered by name; keyset cursor on the name.</summary>
    public async Task<Result<DataContractListPage>> ListAsync(Guid? id, string? producer, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take) || !KeysetPaging.TryDecode(cursor, 1, out var parts))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "cursor is malformed or limit is outside 1..200.");
        }

        var query = db.DataContracts.AsNoTracking().AsQueryable();
        if (id is { } wanted)
        {
            var name = await db.DataContracts.AsNoTracking().Where(c => c.ContractId == wanted).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            query = query.Where(c => c.Name == name);
        }

        if (!string.IsNullOrWhiteSpace(producer))
        {
            var upper = producer.ToUpperInvariant();
            query = query.Where(c => c.Producer == upper);
        }

        // Contract names are few (one per event type): order and page them in memory with an ordinal comparison.
        var after = parts?[0];
        var all = await query.Select(c => c.Name).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = all.Where(n => after is null || string.CompareOrdinal(n, after) > 0).Order(StringComparer.Ordinal).Take(take + 1).ToList();
        var (page, next) = KeysetPaging.Slice(names, take, n => KeysetPaging.Encode(n));
        var rows = await db.DataContracts.AsNoTracking().Where(c => page.Contains(c.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = page.Select(n => new DataContractListItem { ContractVersions = ContractJson.ToElement(Summaries(rows.Where(r => r.Name == n))) }).ToList();
        return new DataContractListPage { Items = items, NextCursor = next, Limit = take };
    }

    private static List<object> Summaries(IEnumerable<DataContractRow> versions) =>
        [.. versions.OrderBy(v => v.Major).ThenBy(v => v.Minor).Select(v => (object)new
        {
            id = v.ContractId,
            name = v.Name,
            producer = v.Producer,
            version = ContractJson.Label(v.Major, v.Minor),
            document = JsonNode.Parse(v.Document),
            compatibility = JsonNode.Parse(v.Compatibility),
            createdAt = v.CreatedAt.ToString(),
        })];
}
