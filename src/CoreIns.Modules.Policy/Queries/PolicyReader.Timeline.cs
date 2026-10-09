using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>pol.Term.timeline and the policy's term list (REQ-POL-002, REQ-POL-085).</summary>
internal sealed partial class PolicyReader
{
    /// <summary>Every term of the policy known at knownAt, by term number.</summary>
    private async Task<IReadOnlyList<TermRecord>> TermsAsync(DynamicParameters args, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. await connection.QueryAsync<TermRecord>(new CommandDefinition(
            $"""
             SELECT term_id AS TermId, policy_id AS PolicyId, term_number AS TermNumber, valid_from AS ValidFrom, valid_to AS ValidTo,
                    recorded_from AS RecordedFrom, state AS State, product_version AS ProductVersion, artefact_hash AS ArtefactHash,
                    rating_artefact_hash AS RatingArtefactHash, resolution_hash AS ResolutionHash, configuration_hash AS ConfigurationHash,
                    currency AS Currency, producer_code AS ProducerCode, payment_plan_ref AS PaymentPlanRef, written_date::text AS WrittenDate,
                    cancelled_at AS CancelledAt
               FROM pol.policy_term
              WHERE legal_entity_id = @le AND policy_id = @policyId AND {Known}
              ORDER BY term_number
             """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];
    }

    /// <summary>
    /// pol.Term.timeline: the term (the requested one, else the one valid at validAt, else the nearest) and its transactions in
    /// sequence order as known at the effective knownAt (clamped to the watermark, D-SL3-03). Null when the policy, or the term
    /// within it, is not known then.
    /// </summary>
    public async Task<TermTimelineResponse?> TimelineAsync(
        LegalEntityId legalEntity, string legalEntityCode, Guid policyId, Guid? termId, Instant validAt, Instant requestedKnownAt, CancellationToken cancellationToken)
    {
        if (await ResolvePolicyAsync(legalEntity, policyId, null, cancellationToken).ConfigureAwait(false) is not var (_, watermark))
        {
            return null;
        }

        var knownAt = EffectiveKnownAt(requestedKnownAt, watermark);
        var args = Args(legalEntity, validAt, knownAt);
        args.Add("policyId", policyId);
        var policy = await PolicyAsync(args, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        TermRecord? term;
        if (termId is { } requested)
        {
            args.Add("termId", requested);
            term = await TermAsync($"policy_id = @policyId AND term_id = @termId AND {Known}", args, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            term = await TermAsync($"policy_id = @policyId AND {Known} AND {Valid}", args, cancellationToken).ConfigureAwait(false)
                ?? await NearestTermAsync(args, cancellationToken).ConfigureAwait(false);
        }

        if (term is null)
        {
            return null;
        }

        args.Add("term", term.TermId);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TimelineRecord>(new CommandDefinition(
            """
            SELECT t.transaction_id AS TransactionId, t.kind AS Kind, t.sequence AS Sequence, t.job_id AS JobId, t.effective_at AS EffectiveAt,
                   t.recorded_at AS RecordedAt, t.premium AS Premium, t.currency AS Currency,
                   EXISTS (SELECT 1 FROM pol.policy_transaction r
                            WHERE r.term_id = t.term_id AND r.sequence > t.sequence AND r.kind IN ('REVERSAL', 'VOID')
                              AND r.recorded_at <= @knownAt) AS Reversed
              FROM pol.policy_transaction t
             WHERE t.term_id = @term AND t.legal_entity_id = @le AND t.recorded_at <= @knownAt
             ORDER BY t.sequence
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new TermTimelineResponse
        {
            Policy = View(policy, term, validAt, legalEntityCode),
            Term = Term(term, validAt),
            Transactions =
            [.. rows.Select(t => new TimelineTransaction
            {
                TransactionId = new PolicyTransactionId(t.TransactionId),
                Kind = t.Kind,
                Sequence = t.Sequence,
                JobId = new JobId(t.JobId),
                EffectiveAt = JobReader.Time(t.EffectiveAt),
                RecordedAt = JobReader.Time(t.RecordedAt),
                PremiumChange = new Money(JobSupport.Exact(t.Premium), Currency.FromCode(t.Currency.Trim())),
                Reversed = t.Reversed,
            })],
            EffectiveKnownAt = knownAt,
        };
    }

    private sealed class TimelineRecord
    {
        public Guid TransactionId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public int Sequence { get; set; }

        public Guid JobId { get; set; }

        public DateTime EffectiveAt { get; set; }

        public DateTime RecordedAt { get; set; }

        public decimal Premium { get; set; }

        public string Currency { get; set; } = string.Empty;

        public bool Reversed { get; set; }
    }
}
