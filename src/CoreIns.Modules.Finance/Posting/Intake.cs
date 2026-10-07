using System.Text.Json;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Finance.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Business-event intake (REQ-FIN-001, -030…-036, -040, -080). Every consumed event is recorded once as a
/// <c>fin.business_event</c> (idempotent on the source event id, REQ-FIN-031) with its catalogue relevance:
/// <list type="bullet">
/// <item>POSTING (BIL BillingEntryPosted, D-SLC-12): posted into every active book, or held as Waiting when the policy
/// context it needs has not arrived (REQ-FIN-040), or Suspended as an intake exception when no rule, derivation or
/// account fits (REQ-FIN-080; never a suspense account, REQ-FIN-084). A suspension publishes BusinessEventSuspended.</item>
/// <item>CONTEXT (POL PolicyBound, ChargeDeltaEmitted; BIL InvoiceIssued, PaymentReceived, CashAllocated): stored for
/// lineage and reconciliation, never journalised (REQ-FIN-036).</item>
/// <item>An event type missing from the catalogue, or an envelope whose legal entity this stamp does not serve, is
/// Suspended (UNKNOWN_EVENT, INVALID_ENVELOPE) — FIN's own dead letter, visible in the intake exception queue.</item>
/// </list>
/// Order (D-ARC-26): the aggregate sequence is recorded; an event older than one already seen for its aggregate is
/// flagged out of order. Postings are independent facts dated by their own accounting date, so a late fact posts
/// correctly; context rows keep the newest sequence.
/// </summary>
internal sealed partial class Intake(
    FinanceDbContext db,
    ReferenceData reference,
    JournalWriter writer,
    ILegalEntityDirectory legalEntities,
    IEventPublisher events,
    DbSession session,
    IClock clock,
    ILogger<Intake> logger)
{
    /// <summary>Registry name of the posting source (REQ-FIN-036).</summary>
    public static readonly string BillingEntryPosted = $"bil.{BillingEntryPostedV1.EventType}";

    /// <summary>Records the event; null when it was already recorded (a redelivery).</summary>
    public async Task<BusinessEventRow?> ReceiveAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (await db.BusinessEvents.AnyAsync(b => b.SourceEventId == envelope.EventId.Value, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var registryName = $"{envelope.Producer.ToLowerCode()}.{envelope.EventType.Value}";
        var major = int.Parse(envelope.SchemaVersion.AsSpan(0, envelope.SchemaVersion.IndexOf('.', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);
        var catalogue = await db.Catalogue.AsNoTracking()
            .SingleOrDefaultAsync(c => c.RegistryName == registryName && c.SchemaMajor == major, cancellationToken).ConfigureAwait(false);
        LegalEntityId? legalEntity = null;
        try
        {
            legalEntity = legalEntities.Resolve(envelope.LegalEntity);
        }
        catch (InvalidOperationException)
        {
            // REQ-FIN-032: an envelope for a legal entity this stamp does not serve becomes an intake exception below.
        }

        var newest = await db.BusinessEvents
            .Where(b => b.AggregateType == envelope.AggregateType && b.AggregateId == envelope.AggregateId)
            .MaxAsync(b => (long?)b.AggregateSequence, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var row = new BusinessEventRow
        {
            BusinessEventId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity?.Value,
            LegalEntityCode = envelope.LegalEntity.Value,
            Jurisdiction = envelope.Jurisdiction.Value,
            SourceModule = envelope.Producer.ToString(),
            SourceEventId = envelope.EventId.Value,
            EventType = envelope.EventType.Value,
            RegistryName = registryName,
            SchemaVersion = envelope.SchemaVersion,
            AggregateType = envelope.AggregateType,
            AggregateId = envelope.AggregateId,
            AggregateSequence = envelope.AggregateSequence,
            OutOfOrder = newest is { } seen && seen > envelope.AggregateSequence,
            Origin = envelope.Origin.ToCode(),
            OccurredAt = envelope.OccurredAt,
            SetId = envelope.Set?.SetId,
            SetSize = envelope.Set?.Size,
            SetIndex = envelope.Set?.Index,
            Relevance = catalogue?.Relevance ?? Relevance.Ignored,
            Status = BusinessEventStatus.Received,
            Payload = envelope.Payload.ToJsonString(),
            BusinessKeys = JsonSerializer.Serialize(envelope.BusinessKeys, SharedKernelJson.Options),
            ConfigurationHash = envelope.ConfigurationHash.ToString(),
            CorrelationId = envelope.CorrelationId.Value,
            PolicyId = Key(envelope, "policyId"),
            PolicyTermId = Key(envelope, "policyTermId"),
            BillingAccountId = envelope.AggregateType == "BillingAccount" && Guid.TryParse(envelope.AggregateId, out var account) ? account : Key(envelope, "billingAccountId"),
            ReceivedAt = now,
            UpdatedAt = now,
            RecordVersion = 1,
        };
        db.BusinessEvents.Add(row);
        if (row.OutOfOrder)
        {
            LogOutOfOrder(logger, registryName, envelope.AggregateId, envelope.AggregateSequence, newest!.Value);
        }

        if (catalogue is null)
        {
            await SuspendAsync(row, ExceptionReasons.UnknownEvent, $"{registryName} v{major} is not in the finance event catalogue (REQ-FIN-030).", null, cancellationToken).ConfigureAwait(false);
        }
        else if (legalEntity is null)
        {
            await SuspendAsync(row, ExceptionReasons.InvalidEnvelope, $"Legal entity {envelope.LegalEntity} is not served by this stamp (REQ-FIN-032).", null, cancellationToken).ConfigureAwait(false);
        }
        else if (catalogue.Relevance != Relevance.Posting)
        {
            row.Status = BusinessEventStatus.NoPosting;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Posts a Received POSTING event: parses the source entry, makes sure the policy context of its lines is present
    /// (else Waiting), resolves rules per active book and writes one journal per book in this transaction (REQ-FIN-074).
    /// </summary>
    public async Task PostAsync(BusinessEventRow row, CancellationToken cancellationToken)
    {
        if (row.RegistryName != BillingEntryPosted)
        {
            await SuspendAsync(row, ExceptionReasons.UnknownEvent, $"No posting source mapping exists for {row.RegistryName}.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var entry = Normalise(JsonSerializer.Deserialize<BillingEntryPostedV1>(row.Payload, SharedKernelJson.Options)
            ?? throw new JsonException("BillingEntryPosted payload is empty."));
        row.AccountingDate = entry.AccountingDate;
        var legalEntity = new LegalEntityId(row.LegalEntityId!.Value);

        var contexts = new Dictionary<(Guid?, Guid?), PolicyContext>();
        foreach (var policyRef in EntryPosting.PolicyReferences(entry))
        {
            var dependency = Dependency(policyRef.TermId, policyRef.PolicyId);
            await reference.LockAsync(dependency, cancellationToken).ConfigureAwait(false);
            var context = await reference.PolicyContextAsync(legalEntity, policyRef.TermId, policyRef.PolicyId, cancellationToken).ConfigureAwait(false);
            if (context is null)
            {
                row.Status = BusinessEventStatus.Waiting;
                row.WaitingOn = dependency;
                Touch(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            contexts[policyRef] = context;
        }

        var profile = await reference.BookProfileAsync(row.LegalEntityCode, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            await SuspendAsync(row, ExceptionReasons.NoBookProfile, $"Legal entity {row.LegalEntityCode} has no book profile (REQ-FIN-089).", entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        var glKeys = await reference.GlKeysAsync([.. contexts.Values.Select(c => c.ArtefactHash).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        var drafts = new List<(JournalDraft Draft, Currency Functional)>();
        foreach (var book in profile.ActiveBooks)
        {
            var setup = await reference.BookSetupAsync(profile, book, entry.AccountingDate, cancellationToken).ConfigureAwait(false);
            if (setup is null)
            {
                await SuspendAsync(row, ExceptionReasons.NoRuleSet, $"Book {book} has no active rule set on {entry.AccountingDate}.", entry, cancellationToken).ConfigureAwait(false);
                return;
            }

            var outcome = EntryPosting.Map(BillingEntryPosted, entry, setup,
                line => contexts.TryGetValue((line.Id(LineDimensionKeys.PolicyTermId), line.Id(LineDimensionKeys.PolicyId)), out var c) ? c : null,
                (hash, chargeType) => glKeys.TryGetValue((hash, chargeType), out var key) ? key : null);
            if (outcome.Reason == ExceptionReasons.Empty)
            {
                row.Status = BusinessEventStatus.NoPosting;
                Touch(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (outcome.Journal is null)
            {
                await SuspendAsync(row, outcome.Reason!, outcome.Detail!, entry, cancellationToken).ConfigureAwait(false);
                return;
            }

            drafts.Add((outcome.Journal, setup.FunctionalCurrency));
        }

        var source = new JournalSource(legalEntity, row.LegalEntityCode, row.Jurisdiction, row.SourceModule, row.EventType, [row.SourceEventId], entry.EntryId.ToString("D"));
        var written = new List<WrittenJournal>();
        foreach (var (draft, functional) in drafts)
        {
            written.Add(await writer.WriteAsync(draft, source, functional, null, null, cancellationToken).ConfigureAwait(false));
        }

        row.Status = BusinessEventStatus.Posted;
        row.WaitingOn = null;
        row.JournalIds = [.. written.Select(w => w.JournalId)];
        Touch(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Last step: nothing after this can fail, so a rolled-back savepoint never leaves an event for a journal that does not exist.
        foreach (var journal in written)
        {
            events.Publish(journal.Posted);
        }
    }

    /// <summary>Re-attempts every event waiting on one of <paramref name="dependencies"/> (REQ-FIN-040: "dependency arrived").</summary>
    public async Task ReleaseAsync(IReadOnlyCollection<string> dependencies, CancellationToken cancellationToken)
    {
        foreach (var dependency in dependencies)
        {
            await reference.LockAsync(dependency, cancellationToken).ConfigureAwait(false);
        }

        var waiting = await db.BusinessEvents.AsNoTracking()
            .Where(b => b.Status == BusinessEventStatus.Waiting && b.WaitingOn != null && dependencies.Contains(b.WaitingOn))
            .OrderBy(b => b.ReceivedAt).ThenBy(b => b.AggregateSequence)
            .Select(b => b.BusinessEventId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var transaction = session.Transaction ?? throw new InvalidOperationException("Release runs inside the handler's transaction.");
        foreach (var id in waiting)
        {
            // Each released entry is isolated: a failure rolls back only its own work and suspends it, so the context
            // event that released it still commits and no other waiting entry is stranded.
            var savepoint = "fin_release_" + id.ToString("N");
            await transaction.SaveAsync(savepoint, cancellationToken).ConfigureAwait(false);
            try
            {
                var row = await db.BusinessEvents.SingleAsync(b => b.BusinessEventId == id, cancellationToken).ConfigureAwait(false);
                row.Status = BusinessEventStatus.Received;
                row.WaitingOn = null;
                row.Attempts++;
                await PostAsync(row, cancellationToken).ConfigureAwait(false);
                await transaction.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transaction.RollbackAsync(savepoint, cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                var row = await db.BusinessEvents.SingleAsync(b => b.BusinessEventId == id, cancellationToken).ConfigureAwait(false);
                row.Attempts++;
                await SuspendAsync(row, ExceptionReasons.PostingError, $"{ex.GetType().Name}: {ex.GetBaseException().Message}", TryNormalise(row.Payload), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The dependency key a line's policy context is waited on under.</summary>
    public static string Dependency(Guid? termId, Guid? policyId) => termId is { } term ? $"term:{term:D}" : $"policy:{policyId!.Value:D}";

    private static SourceEntry Normalise(BillingEntryPostedV1 payload) => new(
        payload.EntryId,
        payload.EventTypeValue,
        payload.AccountingDate,
        payload.BusinessDate,
        [.. payload.Lines.Select(l => new SourceLine(l.Account, l.Side.ToString().ToUpperInvariant(), l.Amount, Dimensions(JsonSerializer.SerializeToElement(l.Dimensions, SharedKernelJson.Options))))]);

    private static SourceEntry? TryNormalise(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<BillingEntryPostedV1>(payload, SharedKernelJson.Options) is { } entry ? Normalise(entry) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string?> Dimensions(JsonElement element)
    {
        var dimensions = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return dimensions;
        }

        foreach (var property in element.EnumerateObject())
        {
            dimensions[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => property.Value.GetRawText(),
            };
        }

        return dimensions;
    }

    private async Task SuspendAsync(BusinessEventRow row, string reason, string detail, SourceEntry? entry, CancellationToken cancellationToken)
    {
        row.Status = BusinessEventStatus.Suspended;
        row.WaitingOn = null;
        row.ExceptionReason = reason;
        row.ExceptionDetail = detail.Length <= 1000 ? detail : detail[..1000];
        Touch(row);
        LogSuspended(logger, row.RegistryName, row.SourceEventId, reason);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // BusinessEventSuspended (contract event, R-47) needs an amount; publish it when the fact carries one.
        var amount = entry?.Lines.Where(l => l.Side == Sides.Debit).Select(l => l.Amount).GroupBy(m => m.Currency)
            .Select(g => Money.Sum(g, g.Key)).FirstOrDefault();
        if (amount is { } total && LegalEntityCode.TryParse(row.LegalEntityCode, out var code) && Jurisdiction.TryParse(row.Jurisdiction, out var jurisdiction))
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(BusinessEventSuspendedV1.Descriptor), "LegalEntity", row.LegalEntityCode,
                new BusinessEventSuspendedV1 { BusinessEventId = row.BusinessEventId, Source = row.RegistryName, Reason = reason, Amount = total },
                BusinessKeys.Empty.With("businessEventId", row.BusinessEventId.ToString("D")))
            {
                LegalEntity = code,
                Jurisdiction = jurisdiction,
            });
        }
    }

    private void Touch(BusinessEventRow row)
    {
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
    }

    private static Guid? Key(EventEnvelope envelope, string name) =>
        envelope.BusinessKeys.TryGetValue(name, out var value) && Guid.TryParse(value, out var id) ? id : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "FIN intake: {RegistryName} on aggregate {AggregateId} arrived out of order (sequence {Sequence} after {Newest}).")]
    private static partial void LogOutOfOrder(ILogger logger, string registryName, string aggregateId, long sequence, long newest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FIN intake exception: {RegistryName} event {EventId} suspended ({Reason}).")]
    private static partial void LogSuspended(ILogger logger, string registryName, Guid eventId, string reason);
}
