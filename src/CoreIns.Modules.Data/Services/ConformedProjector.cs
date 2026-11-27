using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services;

/// <summary>What the projector did with one raw event.</summary>
internal enum ProjectionOutcome
{
    /// <summary>Written to the conformed facts.</summary>
    Projected,

    /// <summary>Already projected earlier (same <c>event_id</c>): nothing written.</summary>
    AlreadyProjected,

    /// <summary>The event type has no conformed fact.</summary>
    NotApplicable,

    /// <summary>The payload could not be conformed; the event is in the quarantine with the reason.</summary>
    Failed,
}

/// <summary>
/// Projects raw ledger events into the bitemporal <c>rpt_conformed</c> facts (BO-DAT-02). Valid time comes from the business
/// event, record time from ingestion (<see cref="IClock"/>). A correction never updates a value: it closes the superseded row's
/// <c>recorded_to</c> and writes a new row. Idempotent on <c>event_id</c> through <c>rpt_conformed.projected_event</c>. A data
/// problem never throws out of the handler: it rolls back to a savepoint and writes the event to <c>rpt_raw.quarantine</c>.
/// </summary>
internal sealed class ConformedProjector(DataDbContext db, IClock clock)
{
    /// <summary>The registry names (producer.EventType) that have a conformed fact.</summary>
    public static readonly IReadOnlySet<string> Handled = new HashSet<string>(StringComparer.Ordinal)
    {
        "pol.PolicyIssued", "pol.PolicyChanged", "pol.PolicyCancelled", "pol.PolicyReinstated", "pol.PolicyLapsed",
        "bil.InvoiceIssued", "clm.ReserveChanged", "clm.PaymentIssued", "clm.PaymentVoided",
    };

    private const string Unknown = "UNKNOWN";

    public async Task<ProjectionOutcome> ProjectAsync(JsonObject wire, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var registry = $"{Text(wire, "producer").ToLowerInvariant()}.{Text(wire, "eventType")}";
        if (!Handled.Contains(registry))
        {
            return ProjectionOutcome.NotApplicable;
        }

        var utc = clock.Now.ToUtcDateTime();
        var now = new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc); // timestamptz keeps microseconds
        var existing = db.Database.CurrentTransaction;
        var own = existing is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        var transaction = existing ?? own!;
        const string savepoint = "dat_project";
        try
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            ProjectionOutcome outcome;
            try
            {
                outcome = await ApplyAsync(registry, wire, now, cancellationToken).ConfigureAwait(false);
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A data problem (or a failed statement) must not abort the outbox transaction of the producing module.
                await transaction.RollbackToSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
                await QuarantineAsync(wire, registry, ex.Message, now, cancellationToken).ConfigureAwait(false);
                outcome = ProjectionOutcome.Failed;
            }

            if (own is not null)
            {
                await own.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ProjectionOutcome> ApplyAsync(string registry, JsonObject wire, DateTime now, CancellationToken ct)
    {
        if (!Guid.TryParse(Text(wire, "eventId"), out var eventId))
        {
            throw new InvalidDataException("eventId is not a UUID.");
        }

        var legalEntity = Text(wire, "legalEntity");
        var payload = wire["payload"] as JsonObject ?? throw new InvalidDataException("payload is not an object.");
        var keys = wire["businessKeys"] as JsonObject;
        var occurred = Instant.TryParse(Text(wire, "occurredAt"), out var o) ? o.ToUtcDateTime() : now;

        // Parse first (no SQL) so a malformed payload leaves no guard row behind.
        Plan plan = registry switch
        {
            "pol.PolicyIssued" => PolicyPlan(payload, keys, wire, "IN_FORCE", ValidInstant(payload, "issuedAt", occurred), null),
            "pol.PolicyChanged" => PolicyPlan(payload, keys, wire, ChangedStatus(payload), ValidDate(payload, "effectiveDate", occurred), Opt(payload, "kind")),
            "pol.PolicyCancelled" => PolicyPlan(payload, keys, wire, "CANCELLED", ValidDate(payload, "effectiveDate", occurred), Opt(payload, "reason")),
            "pol.PolicyReinstated" => PolicyPlan(payload, keys, wire, "IN_FORCE", ValidDate(payload, "effectiveDate", occurred), "REINSTATED"),
            "pol.PolicyLapsed" => PolicyPlan(payload, keys, wire, "LAPSED", ValidDate(payload, "effectiveDate", occurred), null),
            "bil.InvoiceIssued" => InvoicePlan(payload, occurred),
            "clm.ReserveChanged" => ReservePlan(payload, keys, wire, eventId, occurred),
            "clm.PaymentIssued" => PaymentPlan(payload, keys, wire, occurred, isVoid: false),
            "clm.PaymentVoided" => PaymentPlan(payload, keys, wire, occurred, isVoid: true),
            _ => throw new InvalidDataException("unsupported event."),
        };

        var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO rpt_conformed.projected_event (event_id, projected_at) VALUES ({eventId}, {now}) ON CONFLICT (event_id) DO NOTHING", ct).ConfigureAwait(false);
        if (claimed == 0)
        {
            return ProjectionOutcome.AlreadyProjected;
        }

        await plan(legalEntity, eventId, now, ct).ConfigureAwait(false);
        return ProjectionOutcome.Projected;
    }

    private delegate Task Plan(string legalEntity, Guid eventId, DateTime now, CancellationToken ct);

    // ---- policy term -----------------------------------------------------------------------------------------------------

    private Plan PolicyPlan(JsonObject payload, JsonObject? keys, JsonObject wire, string status, DateTime validFrom, string? reason)
    {
        var termId = GuidOrNull(payload, "termId") ?? (keys is null ? null : GuidOrNull(keys, "policyTermId"));
        var policyId = (keys is null ? null : GuidOrNull(keys, "policyId")) ?? (Guid.TryParse(Text(wire, "aggregateId"), out var agg) ? (Guid?)agg : null)
            ?? throw new InvalidDataException("policyId is missing.");
        var product = Opt(payload, "productCode") ?? (keys is null ? null : Opt(keys, "productCode"));
        return async (le, eventId, now, ct) =>
        {
            var term = termId ?? await db.FactPolicyTerms.AsNoTracking().Where(f => f.LegalEntity == le && f.PolicyId == policyId)
                .OrderByDescending(f => f.ValidFrom).Select(f => (Guid?)f.TermId).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("termId is missing and the policy has no known term.");
            var current = await db.FactPolicyTerms.AsNoTracking()
                .Where(f => f.LegalEntity == le && f.TermId == term && f.RecordedTo == null && f.ValidTo == null)
                .Select(f => new { f.ValidFrom, f.ProductCode }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var code = product ?? (current is { } c && c.ProductCode != Unknown ? c.ProductCode : Unknown);
            if (current is { } prior)
            {
                // Close the superseded record; when the change is later in valid time keep the earlier state up to it.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rpt_conformed.fact_policy_term SET recorded_to = {now} WHERE legal_entity = {le} AND term_id = {term} AND recorded_to IS NULL AND valid_to IS NULL", ct).ConfigureAwait(false);
                var priorFrom = prior.ValidFrom.ToUtcDateTime();
                if (validFrom > priorFrom)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO rpt_conformed.fact_policy_term (row_id, legal_entity, product_code, valid_from, valid_to, recorded_from, recorded_to, source_event_id, policy_id, term_id, status, reason)
                        SELECT gen_random_uuid(), legal_entity, product_code, valid_from, {validFrom}, {now}, NULL, source_event_id, policy_id, term_id, status, reason
                        FROM rpt_conformed.fact_policy_term WHERE legal_entity = {le} AND term_id = {term} AND recorded_to = {now} AND valid_to IS NULL
                        """, ct).ConfigureAwait(false);
                }
                else
                {
                    validFrom = priorFrom;
                }
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO rpt_conformed.fact_policy_term (row_id, legal_entity, product_code, valid_from, valid_to, recorded_from, recorded_to, source_event_id, policy_id, term_id, status, reason)
                VALUES ({Guid.CreateVersion7()}, {le}, {code}, {validFrom}, NULL, {now}, NULL, {eventId}, {policyId}, {term}, {status}, {reason})
                """, ct).ConfigureAwait(false);
        };
    }

    private static string ChangedStatus(JsonObject payload) => Opt(payload, "kind") == "SUSPENSION" ? "SUSPENDED" : "IN_FORCE";

    // ---- premium ---------------------------------------------------------------------------------------------------------

    private Plan InvoicePlan(JsonObject payload, DateTime occurred)
    {
        var invoiceId = GuidOrNull(payload, "invoiceId") ?? throw new InvalidDataException("invoiceId is missing.");
        var kind = Opt(payload, "kind") ?? "INVOICE";
        var sign = kind == "CREDIT_NOTE" ? -1m : 1m;
        Guid? termId = payload["termIds"] is JsonArray { Count: > 0 } ids && Guid.TryParse(ids[0]?.ToString(), out var t) ? t : null;
        var lines = (payload["totalsByCategory"] as JsonArray ?? throw new InvalidDataException("totalsByCategory is missing."))
            .Select(n => n as JsonObject ?? throw new InvalidDataException("totalsByCategory item is not an object."))
            .Select(n => (Category: Opt(n, "category") ?? throw new InvalidDataException("category is missing."), Money: MoneyOf(n["amount"])))
            .ToList();
        if (lines.Count == 0)
        {
            throw new InvalidDataException("totalsByCategory is empty.");
        }

        return async (le, eventId, now, ct) =>
        {
            var product = termId is { } term
                ? await db.FactPolicyTerms.AsNoTracking().Where(f => f.LegalEntity == le && f.TermId == term && f.RecordedTo == null && f.ProductCode != Unknown)
                    .Select(f => f.ProductCode).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? Unknown
                : Unknown;
            foreach (var (category, money) in lines)
            {
                var amount = sign * money.Amount;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rpt_conformed.fact_premium SET recorded_to = {now} WHERE legal_entity = {le} AND invoice_id = {invoiceId} AND category = {category} AND recorded_to IS NULL", ct).ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO rpt_conformed.fact_premium (row_id, legal_entity, product_code, valid_from, valid_to, recorded_from, recorded_to, source_event_id, invoice_id, term_id, category, kind, amount, currency)
                    VALUES ({Guid.CreateVersion7()}, {le}, {product}, {occurred}, NULL, {now}, NULL, {eventId}, {invoiceId}, {termId}, {category}, {kind}, {amount}, {money.Currency})
                    """, ct).ConfigureAwait(false);
            }
        };
    }

    // ---- claim movements -------------------------------------------------------------------------------------------------

    private Plan ReservePlan(JsonObject payload, JsonObject? keys, JsonObject wire, Guid eventId, DateTime occurred)
    {
        var claimId = ClaimId(payload, keys, wire);
        var kind = Opt(payload, "kind") ?? "RESERVE";
        var delta = MoneyOf((payload["delta"] as JsonObject ?? throw new InvalidDataException("delta is missing."))["functional"]);
        var product = Opt(payload, "productCode") ?? Unknown;
        var termId = GuidOrNull(payload, "policyTermId");
        var valid = Opt(payload, "accountingDate") is { } d && TryDate(d, out var dt) ? dt : occurred;
        var type = kind == "RESERVE" ? "RESERVE_CHANGE" : "RECOVERY_RESERVE_CHANGE";
        return (le, _, now, ct) => UpsertMovementAsync(le, eventId, now, claimId, $"reserve:{eventId:D}", type, delta.Amount, delta.Currency, product, termId, valid, ct);
    }

    private Plan PaymentPlan(JsonObject payload, JsonObject? keys, JsonObject wire, DateTime occurred, bool isVoid)
    {
        var claimId = ClaimId(payload, keys, wire);
        var paymentId = GuidOrNull(payload, "paymentId") ?? throw new InvalidDataException("paymentId is missing.");
        (decimal Amount, string Currency)? money = payload["amount"] is JsonObject a ? MoneyOf(a["functional"]) : null;
        var valid = Opt(payload, "accountingDate") is { } d && TryDate(d, out var dt) ? dt : occurred;
        return async (le, eventId, now, ct) =>
        {
            var product = await db.FactClaimMovements.AsNoTracking().Where(f => f.LegalEntity == le && f.ClaimId == claimId && f.RecordedTo == null && f.ProductCode != Unknown)
                .Select(f => f.ProductCode).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? Unknown;
            if (!isVoid)
            {
                var paid = money ?? throw new InvalidDataException("amount is missing.");
                await UpsertMovementAsync(le, eventId, now, claimId, $"pay:{paymentId:D}", "PAYMENT", paid.Amount, paid.Currency, product, null, valid, ct).ConfigureAwait(false);
                return;
            }

            var key = $"pay:{paymentId:D}";
            var original = await db.FactClaimMovements.AsNoTracking().Where(f => f.LegalEntity == le && f.MovementKey == key && f.RecordedTo == null)
                .Select(f => new { f.Amount, f.Currency }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            (decimal Amount, string Currency) voided = money
                ?? (original is null ? throw new InvalidDataException("voided amount is unknown (payment not projected).") : (original.Amount, original.Currency));
            await UpsertMovementAsync(le, eventId, now, claimId, $"void:{paymentId:D}", "PAYMENT_VOID", -voided.Amount, voided.Currency, product, null, valid, ct).ConfigureAwait(false);
        };
    }

    private async Task UpsertMovementAsync(string le, Guid eventId, DateTime now, Guid claimId, string key, string type, decimal amount, string currency, string product, Guid? termId, DateTime valid, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE rpt_conformed.fact_claim_movement SET recorded_to = {now} WHERE legal_entity = {le} AND movement_key = {key} AND recorded_to IS NULL", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO rpt_conformed.fact_claim_movement (row_id, legal_entity, product_code, valid_from, valid_to, recorded_from, recorded_to, source_event_id, claim_id, movement_key, movement_type, amount, currency, policy_term_id)
            VALUES ({Guid.CreateVersion7()}, {le}, {product}, {valid}, NULL, {now}, NULL, {eventId}, {claimId}, {key}, {type}, {amount}, {currency}, {termId})
            """, ct).ConfigureAwait(false);
    }

    // ---- quarantine of a projection failure ------------------------------------------------------------------------------

    private async Task QuarantineAsync(JsonObject wire, string registry, string reason, DateTime now, CancellationToken ct)
    {
        if (!Guid.TryParse(Text(wire, "eventId"), out var eventId))
        {
            return;
        }

        var reasons = JsonSerializer.Serialize(new[] { $"projection: {reason}" });
        var envelope = wire.ToJsonString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO rpt_raw.quarantine (quarantine_id, event_id, registry_name, reasons, envelope, status, received_at)
            VALUES ({Guid.CreateVersion7()}, {eventId}, {registry}, {reasons}::jsonb, {envelope}::jsonb, 'QUARANTINED', {now})
            ON CONFLICT (event_id) DO UPDATE SET reasons = EXCLUDED.reasons WHERE quarantine.status = 'QUARANTINED'
            """, ct).ConfigureAwait(false);
    }

    // ---- parsing helpers -------------------------------------------------------------------------------------------------

    private static Guid ClaimId(JsonObject payload, JsonObject? keys, JsonObject wire) =>
        GuidOrNull(payload, "claimId") ?? (keys is null ? null : GuidOrNull(keys, "claimId"))
        ?? (Guid.TryParse(Text(wire, "aggregateId"), out var g) ? g : throw new InvalidDataException("claimId is missing."));

    private static (decimal Amount, string Currency) MoneyOf(JsonNode? node)
    {
        if (node is not JsonObject m || Opt(m, "amount") is not { } text || Opt(m, "currency") is not { } currency
            || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            throw new InvalidDataException("money value is malformed.");
        }

        return (amount, currency);
    }

    private static DateTime ValidInstant(JsonObject payload, string key, DateTime fallback) =>
        Opt(payload, key) is { } text && Instant.TryParse(text, out var i) ? i.ToUtcDateTime() : fallback;

    private static DateTime ValidDate(JsonObject payload, string key, DateTime fallback) =>
        Opt(payload, key) is { } text && TryDate(text, out var d) ? d : fallback;

    private static bool TryDate(string text, out DateTime value)
    {
        if (Instant.TryParse(text, out var instant))
        {
            value = instant.ToUtcDateTime();
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            return true;
        }

        value = default;
        return false;
    }

    private static Guid? GuidOrNull(JsonObject o, string key) => Opt(o, key) is { } t && Guid.TryParse(t, out var g) ? g : null;

    private static string? Opt(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null)
        {
            return null;
        }

        // A JsonValue built in-process (Guid, number) prints as JSON text; parsed wire values are strings.
        if (v is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<Guid>(out var guid))
            {
                return guid.ToString("D");
            }
        }

        return v.ToString();
    }

    private static string Text(JsonObject o, string key) => Opt(o, key) ?? string.Empty;
}
