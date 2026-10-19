using System.Globalization;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;

namespace CoreIns.Modules.Policy.Services.Dsar;

/// <summary>
/// POL's part in a data-subject request (POL-339, D-SL6-07). Subject = the policyholder party. Export lists jobs, quote versions,
/// policies, terms, transactions and charge lines as JSON (ids, dates, amounts). Erase clears the draft risk data and issues of
/// quote versions of unbound jobs (class <see cref="QuoteDraft"/>, anonymised; the app role has no DELETE) and restricts bound
/// policy records (<see cref="DsarRetentionPolicy.PolicyDocument"/>) and charge lines (<see cref="DsarRetentionPolicy.FiscalDocument"/>)
/// with retention end <see cref="DsarRetention.UnsetF404"/>. A restriction is a row in <c>pol.dsar_restriction</c>.
/// Export outcomes carry the record count with <see cref="DsarOutcomeKind.NotHeld"/> and detail EXPORTED when data exists.
/// </summary>
internal sealed class PolicyDsarWork(DbSession session, DsarRetentionPolicy policy) : IDsarParticipant
{
    /// <summary>Record class: draft risk data of quotes that were never bound.</summary>
    public const string QuoteDraft = "QUOTE_DRAFT";

    private const string Basis = "Insurance contract, supervisory and fiscal record keeping; retention period undefined (F-404), data retained and restricted";

    private const string CountDrafts =
        "SELECT count(*) FROM pol.quote_version q JOIN pol.job j ON j.job_id = q.job_id WHERE j.policyholder_party_id = @p AND j.bound_transaction_id IS NULL";

    private const string CountPolicyRecords =
        "SELECT (SELECT count(*) FROM pol.policy WHERE policyholder_party_id = @p) + (SELECT count(*) FROM pol.job WHERE policyholder_party_id = @p AND bound_transaction_id IS NOT NULL)";

    private const string CountCharges =
        "SELECT count(*) FROM pol.charge_line c JOIN pol.policy p ON p.policy_id = c.policy_id WHERE p.policyholder_party_id = @p";

    public string Module => "POL";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var id = subject.PartyId.Value;
        var jobs = await DsarSql.JsonArrayAsync(session,
            "SELECT job_id, job_number, job_type, state, policy_id, product_code, effective_at, expiration_at, currency, channel, bound_transaction_id, created_at FROM pol.job WHERE policyholder_party_id = @p ORDER BY created_at", id, ct).ConfigureAwait(false);
        var quotes = await DsarSql.JsonArrayAsync(session,
            "SELECT q.quote_id, q.job_id, q.version_no, q.state, q.risk_tree, q.premium, q.taxes, q.total, q.quoted_at, q.valid_until, q.created_at FROM pol.quote_version q JOIN pol.job j ON j.job_id = q.job_id WHERE j.policyholder_party_id = @p ORDER BY q.created_at", id, ct).ConfigureAwait(false);
        var policies = await DsarSql.JsonArrayAsync(session,
            "SELECT policy_id, policy_number, product_code, recorded_at FROM pol.policy WHERE policyholder_party_id = @p ORDER BY recorded_at", id, ct).ConfigureAwait(false);
        var terms = await DsarSql.JsonArrayAsync(session,
            "SELECT t.term_id, t.policy_id, t.term_number, t.state, t.valid_from, t.valid_to, t.recorded_from, t.recorded_to, t.currency, t.written_date, t.cancelled_at FROM pol.policy_term t JOIN pol.policy p ON p.policy_id = t.policy_id WHERE p.policyholder_party_id = @p ORDER BY t.recorded_from", id, ct).ConfigureAwait(false);
        var transactions = await DsarSql.JsonArrayAsync(session,
            "SELECT x.transaction_id, x.policy_id, x.term_id, x.job_id, x.kind, x.effective_at, x.recorded_at, x.premium, x.taxes, x.total, x.currency FROM pol.policy_transaction x JOIN pol.policy p ON p.policy_id = x.policy_id WHERE p.policyholder_party_id = @p ORDER BY x.recorded_at", id, ct).ConfigureAwait(false);
        var charges = await DsarSql.JsonArrayAsync(session,
            "SELECT c.charge_id, c.transaction_id, c.policy_id, c.charge_type, c.charge_category, c.amount, c.currency, c.valid_from, c.valid_to, c.booking_date FROM pol.charge_line c JOIN pol.policy p ON p.policy_id = c.policy_id WHERE p.policyholder_party_id = @p ORDER BY c.recorded_at", id, ct).ConfigureAwait(false);
        var restrictions = await DsarSql.JsonArrayAsync(session,
            "SELECT record_class, legal_basis, retention_end, created_at FROM pol.dsar_restriction WHERE party_id = @p", id, ct).ConfigureAwait(false);

        var payload = $$"""{"module":"POL","partyId":"{{id}}","jobs":{{jobs}},"quoteVersions":{{quotes}},"policies":{{policies}},"terms":{{terms}},"transactions":{{transactions}},"chargeLines":{{charges}},"restrictions":{{restrictions}}}""";
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
            else if (policy.RestrictInsteadOfErase(recordClass, Basis, count) is { } restricted)
            {
                await DsarSql.RestrictAsync(session, id, recordClass, Basis, restricted.RetentionEnd ?? DsarRetention.UnsetF404, "erase request: retained", context.RequestedBy, ct).ConfigureAwait(false);
                outcomes.Add(restricted);
            }
            else if (recordClass == QuoteDraft)
            {
                // TODO(hardening): outbox/audit copies and the job resolution manifest are not scrubbed.
                await DsarSql.ExecuteAsync(session,
                    "UPDATE pol.quote_version q SET risk_tree = '{}'::jsonb, issues = NULL FROM pol.job j WHERE j.job_id = q.job_id AND j.policyholder_party_id = @p AND j.bound_transaction_id IS NULL",
                    id, ct).ConfigureAwait(false);
                outcomes.Add(new DsarRecordOutcome(recordClass, DsarOutcomeKind.Anonymised, count));
            }
            else
            {
                // Unknown class that is not configured restrict-only: fail closed.
                await DsarSql.RestrictAsync(session, id, recordClass, Basis, DsarRetention.UnsetF404, "erase request: retained", context.RequestedBy, ct).ConfigureAwait(false);
                outcomes.Add(DsarRecordOutcome.Restricted(recordClass, Basis, count));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, outcomes);
    }

    private static DsarRecordOutcome Held(string recordClass, int count) =>
        count == 0 ? DsarRecordOutcome.NotHeld(recordClass) : new DsarRecordOutcome(recordClass, DsarOutcomeKind.NotHeld, count, Detail: "EXPORTED");

    private async Task<List<(string Class, int Count)>> CountsAsync(Guid id, CancellationToken ct) =>
    [
        (QuoteDraft, await DsarSql.CountAsync(session, CountDrafts, id, ct).ConfigureAwait(false)),
        (DsarRetentionPolicy.PolicyDocument, await DsarSql.CountAsync(session, CountPolicyRecords, id, ct).ConfigureAwait(false)),
        (DsarRetentionPolicy.FiscalDocument, await DsarSql.CountAsync(session, CountCharges, id, ct).ConfigureAwait(false)),
    ];
}
