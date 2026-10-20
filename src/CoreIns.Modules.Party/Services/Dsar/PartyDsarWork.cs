using System.Globalization;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;

namespace CoreIns.Modules.Party.Services.Dsar;

/// <summary>
/// PTY's part in a data-subject request (PTY-161, D-SL6-07). Export lists the party master, names, identifiers (display suffix
/// only; the encrypted value is never exported), addresses, contact points and intermediary record as JSON. Erase anonymises
/// only non-primary contact points; everything else is restricted with <see cref="DsarRetention.UnsetF404"/> (retention
/// durations are unset, so the data is retained, not destroyed). A restriction is a row in <c>pty.dsar_restriction</c>.
/// Export outcomes carry the record count with <see cref="DsarOutcomeKind.NotHeld"/> and detail EXPORTED when data exists
/// (the SPI has no "exported" kind).
/// </summary>
internal sealed class PartyDsarWork(DbSession session, DsarRetentionPolicy policy) : IDsarParticipant
{
    public const string PartyRecord = "PARTY_RECORD";
    public const string Address = "ADDRESS";
    public const string ContactPointPrimary = "CONTACT_POINT_PRIMARY";
    public const string ContactPointOther = "CONTACT_POINT";
    public const string Intermediary = "INTERMEDIARY";

    /// <summary>Value written over an erased contact point.</summary>
    public const string ErasedMarker = "[erased]";

    // The party master, addresses, primary contact points and intermediary records are restrict-only in this module whatever the
    // configured table says (policies and fiscal documents reference them): see IsRetained. Erase reports them NOT_ERASED.
    private const string Basis = "Insurance contract and regulatory record keeping; retention period undefined (F-404), data retained and restricted";

    private const string CountParty =
        "SELECT (SELECT count(*) FROM pty.party WHERE party_id = @p) + (SELECT count(*) FROM pty.party_name WHERE party_id = @p) + (SELECT count(*) FROM pty.party_identifier WHERE party_id = @p)";

    public string Module => "PTY";

    /// <summary>Classes that are never erased here: the party master is referenced by policies, invoices and claims.</summary>
    private static readonly string[] AlwaysRetained = [PartyRecord, Address, ContactPointPrimary, Intermediary];

    private bool IsRetained(string recordClass) =>
        AlwaysRetained.Contains(recordClass, StringComparer.Ordinal) || policy.IsRestrictOnly(recordClass);

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var id = subject.PartyId.Value;
        var parties = await DsarSql.JsonArrayAsync(session,
            "SELECT party_id, party_number, party_type, status, jurisdiction, preferred_language, source_channel, created_at, updated_at FROM pty.party WHERE party_id = @p", id, ct).ConfigureAwait(false);
        var names = await DsarSql.JsonArrayAsync(session,
            "SELECT name_id, form, script, given_names, family_name, father_name, mother_name, organisation_name, trade_name, valid_from, valid_to, recorded_from, recorded_to FROM pty.party_name WHERE party_id = @p ORDER BY recorded_from", id, ct).ConfigureAwait(false);
        var identifiers = await DsarSql.JsonArrayAsync(session,
            "SELECT identifier_id, scheme, display_suffix, issuing_country, verification_status, valid_from, valid_to, recorded_from, recorded_to FROM pty.party_identifier WHERE party_id = @p ORDER BY recorded_from", id, ct).ConfigureAwait(false);
        var addresses = await DsarSql.JsonArrayAsync(session,
            "SELECT address_id, types, is_primary, country, street, number, building, floor, unit, postcode, locality, municipality, regional_unit, region, validation_state, valid_from, valid_to, recorded_from, recorded_to FROM pty.party_address WHERE party_id = @p ORDER BY recorded_from", id, ct).ConfigureAwait(false);
        var contacts = await DsarSql.JsonArrayAsync(session,
            "SELECT contact_point_id, type, value, purpose, is_primary, verification_status, valid_from, valid_to, recorded_from, recorded_to FROM pty.party_contact_point WHERE party_id = @p ORDER BY recorded_from", id, ct).ConfigureAwait(false);
        var intermediaries = await DsarSql.JsonArrayAsync(session,
            "SELECT intermediary_id, intermediary_type, status, register_name, register_number, register_status, registration_date FROM pty.intermediary WHERE party_id = @p", id, ct).ConfigureAwait(false);
        var restrictions = await DsarSql.JsonArrayAsync(session,
            "SELECT record_class, legal_basis, retention_end, created_at FROM pty.dsar_restriction WHERE party_id = @p", id, ct).ConfigureAwait(false);

        var payload = $$"""{"module":"PTY","partyId":"{{id}}","party":{{parties}},"names":{{names}},"identifiers":{{identifiers}},"addresses":{{addresses}},"contactPoints":{{contacts}},"intermediaries":{{intermediaries}},"restrictions":{{restrictions}}}""";
        var outcomes = (await CountsAsync(id, ct).ConfigureAwait(false)).Select(c => Held(c.Class, c.Count)).ToList();
        return new DsarModuleResult(Module, outcomes, payload);
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var id = subject.PartyId.Value;
        var end = retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DsarRetention.UnsetF404;
        var outcomes = new List<DsarRecordOutcome>();
        await using var tx = await session.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var (recordClass, count) in await CountsAsync(id, ct).ConfigureAwait(false))
        {
            if (count == 0)
            {
                outcomes.Add(DsarRecordOutcome.NotHeld(recordClass));
                continue;
            }

            await DsarSql.RestrictAsync(session, id, recordClass, legalBasis, end, reason, "dsar", ct).ConfigureAwait(false);
            outcomes.Add(DsarRecordOutcome.Restricted(recordClass, legalBasis, count, end));
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, outcomes);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(context);
        var id = subject.PartyId.Value;
        var outcomes = new List<DsarRecordOutcome>();
        await using var tx = await session.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var (recordClass, count) in await CountsAsync(id, ct).ConfigureAwait(false))
        {
            if (count == 0)
            {
                outcomes.Add(DsarRecordOutcome.NotHeld(recordClass));
            }
            else if (recordClass == ContactPointOther && !IsRetained(recordClass))
            {
                // Contact preferences outside the mandatory retention classes: overwrite the value; the row stays (the app role has no DELETE).
                await DsarSql.ExecuteAsync(session,
                    "UPDATE pty.party_contact_point SET value = @m, verification_status = 'UNVERIFIED' WHERE party_id = @p AND NOT is_primary", id, ct, ("m", ErasedMarker)).ConfigureAwait(false);
                outcomes.Add(new DsarRecordOutcome(recordClass, DsarOutcomeKind.Anonymised, count));
            }
            else
            {
                await DsarSql.RestrictAsync(session, id, recordClass, Basis, DsarRetention.UnsetF404, "erase request: retained", context.RequestedBy, ct).ConfigureAwait(false);
                outcomes.Add(DsarRecordOutcome.Restricted(recordClass, Basis, count) with { Detail = DsarRetentionPolicy.NotErasedDetail(Basis) });
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, outcomes);
    }

    /// <summary>Reads feeding marketing or notification must skip a party whose contact points are restricted.</summary>
    public Task<bool> IsContactRestrictedAsync(Guid partyId, CancellationToken ct = default) =>
        DsarSql.IsRestrictedAsync(session, partyId, ContactPointPrimary, ct);

    private static DsarRecordOutcome Held(string recordClass, int count) =>
        count == 0 ? DsarRecordOutcome.NotHeld(recordClass) : new DsarRecordOutcome(recordClass, DsarOutcomeKind.NotHeld, count, Detail: "EXPORTED");

    private async Task<List<(string Class, int Count)>> CountsAsync(Guid id, CancellationToken ct) =>
    [
        (PartyRecord, await DsarSql.CountAsync(session, CountParty, id, ct).ConfigureAwait(false)),
        (Address, await DsarSql.CountAsync(session, "SELECT count(*) FROM pty.party_address WHERE party_id = @p", id, ct).ConfigureAwait(false)),
        (ContactPointPrimary, await DsarSql.CountAsync(session, "SELECT count(*) FROM pty.party_contact_point WHERE party_id = @p AND is_primary", id, ct).ConfigureAwait(false)),
        (ContactPointOther, await DsarSql.CountAsync(session, "SELECT count(*) FROM pty.party_contact_point WHERE party_id = @p AND NOT is_primary", id, ct).ConfigureAwait(false)),
        (Intermediary, await DsarSql.CountAsync(session, "SELECT count(*) FROM pty.intermediary WHERE party_id = @p", id, ct).ConfigureAwait(false)),
    ];
}
