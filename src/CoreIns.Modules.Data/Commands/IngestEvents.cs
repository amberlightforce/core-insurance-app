using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Domain;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Commands;

/// <summary>What happened to one event offered to the raw layer.</summary>
internal enum IngestOutcome
{
    /// <summary>Written to <c>rpt_raw.event_ledger</c>.</summary>
    Ingested,

    /// <summary>Already in the ledger (same <c>event_id</c>): nothing written.</summary>
    Duplicate,

    /// <summary>Refused by validation and written to <c>rpt_raw.quarantine</c>.</summary>
    Quarantined,
}

/// <summary>
/// The raw-layer write path (BO-DAT-01): validates an event against the envelope rules and the registered data contract, then
/// writes it once to the ledger (unique on <c>event_id</c>) or to the quarantine. Only plain INSERT ... ON CONFLICT DO NOTHING
/// on the handler's own transaction, so a redelivery is a no-op and the outbox of other modules is never touched.
/// </summary>
internal sealed class EventIngestion(DataDbContext db, IClock clock)
{
    /// <summary>The report-relevant event types DAT ingests into the raw layer (producer module code, event type; all major 1).</summary>
    public static readonly IReadOnlyList<(ModuleCode Producer, string EventType)> ReportRelevant =
    [
        (ModuleCode.POL, "PolicyBound"), (ModuleCode.POL, "PolicyIssued"), (ModuleCode.POL, "PolicyChanged"), (ModuleCode.POL, "PolicyCancelled"),
        (ModuleCode.POL, "PolicyReinstated"), (ModuleCode.POL, "PolicyLapsed"), (ModuleCode.POL, "RenewalBound"),
        (ModuleCode.BIL, "InvoiceIssued"), (ModuleCode.BIL, "PaymentReceived"), (ModuleCode.BIL, "PaymentReversed"), (ModuleCode.BIL, "CommissionCalculated"),
        (ModuleCode.CLM, "ClaimReported"), (ModuleCode.CLM, "ReserveChanged"), (ModuleCode.CLM, "PaymentIssued"), (ModuleCode.CLM, "PaymentVoided"),
        (ModuleCode.CLM, "ClaimClosed"), (ModuleCode.CLM, "ClaimReopened"), (ModuleCode.CLM, "RecoveryRecorded"),
        (ModuleCode.RI, "CessionCalculated"), (ModuleCode.RI, "RecoveryCalculated"),
        (ModuleCode.FIN, "JournalPosted"),
    ];

    private static readonly string[] WireKeys = ["eventId", "eventType", "producer", "schemaVersion", "aggregateType", "aggregateId", "occurredAt", "legalEntity", "payload"];

    /// <summary>Ingests one live event (envelope already validated by the outbox writer; checked again here).</summary>
    public async Task<IngestOutcome> IngestAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var problems = new List<string>(envelope.Validate());
        problems.AddRange(await ContractProblemsAsync($"{envelope.Producer.ToLowerCode()}.{envelope.EventType}", envelope.Payload, cancellationToken).ConfigureAwait(false));
        return await StoreAsync(envelope.ToWireJson(), problems, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-validates a stored wire envelope (quarantine release, replay): shape first, then the current contract by name.</summary>
    public async Task<IReadOnlyList<string>> WireProblemsAsync(JsonObject wire, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var problems = new List<string>();
        foreach (var key in WireKeys.Where(k => !wire.TryGetPropertyValue(k, out var v) || v is null))
        {
            problems.Add($"envelope field '{key}' is missing");
        }

        if (problems.Count == 0)
        {
            if (!Guid.TryParse(Text(wire, "eventId"), out _))
            {
                problems.Add("eventId must be a UUID");
            }

            if (wire["payload"] is not JsonObject payload)
            {
                problems.Add("payload must be an object");
            }
            else
            {
                problems.AddRange(await ContractProblemsAsync(RegistryName(wire), payload, cancellationToken).ConfigureAwait(false));
            }
        }

        return problems;
    }

    /// <summary>Writes the event to the ledger when <paramref name="problems"/> is empty, else to the quarantine.</summary>
    public async Task<IngestOutcome> StoreAsync(JsonObject wire, IReadOnlyList<string> problems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(problems);
        var now = clock.Now.ToUtcDateTime();
        var envelopeJson = wire.ToJsonString();
        var registry = RegistryName(wire);
        if (!Guid.TryParse(Text(wire, "eventId"), out var eventId))
        {
            eventId = Guid.CreateVersion7();
        }

        if (problems.Count == 0)
        {
            var occurred = Instant.TryParse(Text(wire, "occurredAt"), out var o) ? o.ToUtcDateTime() : now;
            var written = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO rpt_raw.event_ledger (event_id, registry_name, event_type, producer, schema_version, aggregate_type, aggregate_id, aggregate_sequence, occurred_at, legal_entity, origin, envelope, received_at)
                VALUES ({eventId}, {registry}, {Text(wire, "eventType")}, {Text(wire, "producer")}, {Text(wire, "schemaVersion")}, {Text(wire, "aggregateType")}, {Text(wire, "aggregateId")},
                        {Long(wire, "aggregateSequence")}, {occurred}, {Text(wire, "legalEntity")}, {Text(wire, "origin")}, {envelopeJson}::jsonb, {now})
                ON CONFLICT (event_id) DO NOTHING
                """, cancellationToken).ConfigureAwait(false);
            return written == 1 ? IngestOutcome.Ingested : IngestOutcome.Duplicate;
        }

        if (await db.EventLedger.AsNoTracking().AnyAsync(x => x.EventId == eventId, cancellationToken).ConfigureAwait(false))
        {
            return IngestOutcome.Duplicate;
        }

        var reasons = JsonSerializer.Serialize(problems);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO rpt_raw.quarantine (quarantine_id, event_id, registry_name, reasons, envelope, status, received_at)
            VALUES ({Guid.CreateVersion7()}, {eventId}, {registry}, {reasons}::jsonb, {envelopeJson}::jsonb, 'QUARANTINED', {now})
            ON CONFLICT (event_id) DO UPDATE SET reasons = EXCLUDED.reasons WHERE quarantine.status = 'QUARANTINED'
            """, cancellationToken).ConfigureAwait(false);
        return IngestOutcome.Quarantined;
    }

    /// <summary>Contract name of a wire envelope, e.g. <c>pol.PolicyIssued</c>.</summary>
    public static string RegistryName(JsonObject wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return $"{Text(wire, "producer").ToLowerInvariant()}.{Text(wire, "eventType")}";
    }

    // TODO(hardening): cache the latest contract per name (one query per ingested event today).
    private async Task<IReadOnlyList<string>> ContractProblemsAsync(string registryName, JsonObject payload, CancellationToken cancellationToken)
    {
        var latest = await db.DataContracts.AsNoTracking()
            .Where(c => c.Name == registryName)
            .OrderByDescending(c => c.Major).ThenByDescending(c => c.Minor)
            .Select(c => c.Document)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return [];
        }

        using var parsed = JsonDocument.Parse(latest);
        var (document, parseProblems) = ContractDocument.Parse(parsed.RootElement);
        return document is null ? parseProblems : document.Validate(payload);
    }

    private static string Text(JsonObject wire, string key) => wire.TryGetPropertyValue(key, out var v) && v is not null ? v.ToString() : string.Empty;

    private static long Long(JsonObject wire, string key) =>
        wire.TryGetPropertyValue(key, out var v) && v is not null && long.TryParse(v.ToString(), out var l) ? l : 0;
}

/// <summary>
/// The catch-all raw-layer handler (BO-DAT-01). One registration per report-relevant routing key (handler name
/// <c>DAT.Ingest.&lt;producer&gt;.&lt;EventType&gt;</c>); it only validates and inserts, on the outbox transaction of the event.
/// </summary>
internal sealed class IngestEventsHandler(EventIngestion ingestion) : IEventHandler<JsonObject>
{
    public async Task HandleAsync(EventEnvelope envelope, JsonObject payload, CancellationToken cancellationToken) =>
        _ = await ingestion.IngestAsync(envelope, cancellationToken).ConfigureAwait(false);
}
