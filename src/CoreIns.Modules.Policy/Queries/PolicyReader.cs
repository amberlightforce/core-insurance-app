using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>
/// The bitemporal read side (REQ-POL-002, REQ-POL-084): the policy, the term and segment valid at <c>validAt</c> as
/// recorded at <c>knownAt</c>, with the term's transactions and charge lines known by then. A row is visible when
/// <c>valid_from ≤ validAt &lt; valid_to</c> and <c>recorded_from ≤ knownAt &lt; recorded_to</c> (half-open, open end
/// = still current). Filtered by the caller's legal entity.
/// </summary>
internal sealed partial class PolicyReader(DbSession session)
{
    private const string Known = "recorded_from <= @knownAt AND (recorded_to IS NULL OR recorded_to > @knownAt)";
    private const string Valid = "valid_from <= @validAt AND valid_to > @validAt";

    /// <summary>
    /// The effective knownAt (D-SL3-03 a): the requested instant, but never later than the policy's committed record-time
    /// watermark. Every row recorded after the watermark is stamped above it, so an answer given at the effective knownAt can
    /// never change when a concurrent writer commits, whatever the clock skew between replicas.
    /// </summary>
    public static Instant EffectiveKnownAt(Instant requested, Instant watermark) => Instant.Min(PolicyWriteLock.Truncate(requested), watermark);

    /// <summary>The policy (by id or number, in the caller's legal entity) and its committed watermark; null when it does not exist.</summary>
    public async Task<(Guid PolicyId, Instant Watermark)?> ResolvePolicyAsync(
        LegalEntityId legalEntity, Guid? policyId, string? policyNumber, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<WatermarkRecord>(new CommandDefinition(
            """
            SELECT policy_id AS PolicyId, last_recorded_at AS Watermark FROM pol.policy
             WHERE legal_entity_id = @le AND (@id::uuid IS NULL OR policy_id = @id) AND (@number::text IS NULL OR policy_number = @number)
            """,
            new { le = legalEntity.Value, id = policyId, number = policyNumber }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : (row.PolicyId, JobReader.Time(row.Watermark));
    }

    /// <summary>pol.Policy.get; null when the policy did not exist (in this legal entity) as known at the effective knownAt.</summary>
    public async Task<PolicyGetResponse?> GetPolicyAsync(LegalEntityId legalEntity, string legalEntityCode, Guid policyId, Instant validAt, Instant knownAt, CancellationToken cancellationToken) =>
        (await GetPolicyEffectiveAsync(legalEntity, legalEntityCode, policyId, validAt, knownAt, cancellationToken).ConfigureAwait(false))?.Response;

    /// <summary>pol.Policy.get with the effective knownAt it was answered at.</summary>
    public async Task<(PolicyGetResponse Response, Instant EffectiveKnownAt)?> GetPolicyEffectiveAsync(
        LegalEntityId legalEntity, string legalEntityCode, Guid policyId, Instant validAt, Instant requestedKnownAt, CancellationToken cancellationToken)
    {
        if (await ResolvePolicyAsync(legalEntity, policyId, null, cancellationToken).ConfigureAwait(false) is not var (_, watermark))
        {
            return null;
        }

        var knownAt = EffectiveKnownAt(requestedKnownAt, watermark);
        var response = await ReadPolicyAsync(legalEntity, legalEntityCode, policyId, validAt, knownAt, cancellationToken).ConfigureAwait(false);
        return response is null ? null : (response, knownAt);
    }

    private async Task<PolicyGetResponse?> ReadPolicyAsync(LegalEntityId legalEntity, string legalEntityCode, Guid policyId, Instant validAt, Instant knownAt, CancellationToken cancellationToken)
    {
        var args = Args(legalEntity, validAt, knownAt);
        args.Add("policyId", policyId);
        var policy = await PolicyAsync(args, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        var term = await TermAsync($"policy_id = @policyId AND {Known} AND {Valid}", args, cancellationToken).ConfigureAwait(false);
        var content = await ContentAsync(term, args, cancellationToken).ConfigureAwait(false);

        // No term covers validAt: the status and term come from the nearest term known at knownAt (the next one starting
        // after validAt reads Scheduled, else the last one ended by validAt reads Expired); no segment is valid then.
        var nearest = term ?? await NearestTermAsync(args, cancellationToken).ConfigureAwait(false);
        if (term is null && nearest is not null)
        {
            var nearestContent = await ContentAsync(nearest, args, cancellationToken).ConfigureAwait(false);
            content = (null, null, nearestContent.Transactions, nearestContent.Charges);
        }

        return new PolicyGetResponse
        {
            Policy = View(policy, nearest, validAt, legalEntityCode),
            Term = nearest is null ? null : Term(nearest, validAt),
            Segment = content.Segment,
            RiskTree = content.RiskTree,
            Transactions = content.Transactions,
            Charges = content.Charges,
        };
    }

    /// <summary>pol.Term.get; null when the term (or its policy) was not known at the effective knownAt.</summary>
    public async Task<TermGetResponse?> GetTermAsync(LegalEntityId legalEntity, string legalEntityCode, Guid termId, Instant validAt, Instant knownAt, CancellationToken cancellationToken) =>
        (await GetTermEffectiveAsync(legalEntity, legalEntityCode, termId, validAt, knownAt, cancellationToken).ConfigureAwait(false))?.Response;

    /// <summary>pol.Term.get with the effective knownAt it was answered at.</summary>
    public async Task<(TermGetResponse Response, Instant EffectiveKnownAt)?> GetTermEffectiveAsync(
        LegalEntityId legalEntity, string legalEntityCode, Guid termId, Instant validAt, Instant requestedKnownAt, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var owner = await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT policy_id FROM pol.policy_term WHERE term_id = @termId AND legal_entity_id = @le LIMIT 1",
            new { termId, le = legalEntity.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (owner is not { } policyId || await ResolvePolicyAsync(legalEntity, policyId, null, cancellationToken).ConfigureAwait(false) is not var (_, watermark))
        {
            return null;
        }

        var knownAt = EffectiveKnownAt(requestedKnownAt, watermark);
        var response = await ReadTermAsync(legalEntity, legalEntityCode, termId, validAt, knownAt, cancellationToken).ConfigureAwait(false);
        return response is null ? null : (response, knownAt);
    }

    private async Task<TermGetResponse?> ReadTermAsync(LegalEntityId legalEntity, string legalEntityCode, Guid termId, Instant validAt, Instant knownAt, CancellationToken cancellationToken)
    {
        var args = Args(legalEntity, validAt, knownAt);
        args.Add("termId", termId);
        var term = await TermAsync($"term_id = @termId AND {Known}", args, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return null;
        }

        args.Add("policyId", term.PolicyId);
        var policy = await PolicyAsync(args, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        var content = await ContentAsync(term, args, cancellationToken).ConfigureAwait(false);
        return new TermGetResponse
        {
            Policy = View(policy, term, validAt, legalEntityCode),
            Term = Term(term, validAt),
            Segment = content.Segment,
            RiskTree = content.RiskTree,
            Transactions = content.Transactions,
            Charges = content.Charges,
        };
    }

    private static DynamicParameters Args(LegalEntityId legalEntity, Instant validAt, Instant knownAt) =>
        new(new { le = legalEntity.Value, validAt = validAt.ToUtcDateTime(), knownAt = knownAt.ToUtcDateTime() });

    private async Task<PolicyRecord?> PolicyAsync(DynamicParameters args, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PolicyRecord>(new CommandDefinition(
            """
            SELECT p.policy_id AS PolicyId, p.policy_number AS PolicyNumber, p.product_code AS ProductCode,
                   p.policyholder_party_id AS PolicyholderPartyId, p.account_id AS AccountId, p.jurisdiction AS Jurisdiction,
                   p.recorded_at AS RecordedAt
              FROM pol.policy p
             WHERE p.policy_id = @policyId AND p.legal_entity_id = @le AND p.recorded_at <= @knownAt
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<TermRecord?> NearestTermAsync(DynamicParameters args, CancellationToken cancellationToken) =>
        await TermAsync($"policy_id = @policyId AND {Known} AND valid_from > @validAt", args, cancellationToken, "valid_from ASC").ConfigureAwait(false)
        ?? await TermAsync($"policy_id = @policyId AND {Known} AND valid_to <= @validAt", args, cancellationToken, "valid_to DESC").ConfigureAwait(false);

    private async Task<TermRecord?> TermAsync(string where, DynamicParameters args, CancellationToken cancellationToken, string order = "term_number DESC")
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<TermRecord>(new CommandDefinition(
            $"""
             SELECT term_id AS TermId, policy_id AS PolicyId, term_number AS TermNumber, valid_from AS ValidFrom, valid_to AS ValidTo,
                    recorded_from AS RecordedFrom, state AS State, product_version AS ProductVersion, artefact_hash AS ArtefactHash,
                    rating_artefact_hash AS RatingArtefactHash, resolution_hash AS ResolutionHash, configuration_hash AS ConfigurationHash,
                    currency AS Currency, producer_code AS ProducerCode, payment_plan_ref AS PaymentPlanRef, written_date::text AS WrittenDate,
                    cancelled_at AS CancelledAt
               FROM pol.policy_term
              WHERE legal_entity_id = @le AND {where}
              ORDER BY {order}
             """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<(SegmentView? Segment, RiskTree? RiskTree, IReadOnlyList<TransactionView> Transactions, IReadOnlyList<ChargeLine> Charges)> ContentAsync(
        TermRecord? term, DynamicParameters args, CancellationToken cancellationToken)
    {
        if (term is null)
        {
            return (null, null, [], []);
        }

        args.Add("term", term.TermId);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var segment = await connection.QueryFirstOrDefaultAsync<SegmentRecord>(new CommandDefinition(
            $"""
             SELECT segment_id AS SegmentId, transaction_id AS TransactionId, valid_from AS ValidFrom, valid_to AS ValidTo,
                    recorded_from AS RecordedFrom, recorded_to AS RecordedTo, snapshot_hash AS SnapshotHash, snapshot::text AS Snapshot
               FROM pol.segment
              WHERE term_id = @term AND legal_entity_id = @le AND {Known} AND {Valid}
             """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var transactions = await connection.QueryAsync<TransactionRecord>(new CommandDefinition(
            """
            SELECT transaction_id AS TransactionId, kind AS Kind, sequence AS Sequence, job_id AS JobId, effective_at AS EffectiveAt,
                   recorded_at AS RecordedAt, premium AS Premium, taxes AS Taxes, total AS Total, currency AS Currency
              FROM pol.policy_transaction
             WHERE term_id = @term AND legal_entity_id = @le AND recorded_at <= @knownAt
             ORDER BY sequence
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var charges = await connection.QueryAsync<ChargeRecord>(new CommandDefinition(
            """
            SELECT charge_id AS ChargeId, transaction_id AS TransactionId, element_locator AS ElementLocator, coverage_code AS CoverageCode,
                   charge_type AS ChargeType, charge_category AS ChargeCategory, annual_rate AS AnnualRate, amount AS Amount, currency AS Currency,
                   legal_status AS LegalStatus, provisional AS Provisional
              FROM pol.charge_line
             WHERE term_id = @term AND legal_entity_id = @le AND recorded_at <= @knownAt
             ORDER BY recorded_at, set_index
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (
            segment is null ? null : new SegmentView
            {
                SegmentId = new SegmentId(segment.SegmentId),
                TransactionId = new PolicyTransactionId(segment.TransactionId),
                ValidPeriod = InstantRange.Of(JobReader.Time(segment.ValidFrom), JobReader.Time(segment.ValidTo)),
                RecordedPeriod = new InstantRange(JobReader.Time(segment.RecordedFrom), segment.RecordedTo is { } to ? JobReader.Time(to) : null),
                SnapshotHash = Sha256Hash.Parse(segment.SnapshotHash),
            },
            segment is null ? null : JobSupport.FromJson<RiskTree>(segment.Snapshot),
            [.. transactions.Select(t =>
            {
                var currency = Currency.FromCode(t.Currency.Trim());
                return new TransactionView
                {
                    TransactionId = new PolicyTransactionId(t.TransactionId),
                    Kind = t.Kind,
                    Sequence = t.Sequence,
                    JobId = new JobId(t.JobId),
                    EffectiveAt = JobReader.Time(t.EffectiveAt),
                    RecordedAt = JobReader.Time(t.RecordedAt),
                    Premium = new Money(JobSupport.Exact(t.Premium), currency),
                    Taxes = new Money(JobSupport.Exact(t.Taxes), currency),
                    Total = new Money(JobSupport.Exact(t.Total), currency),
                };
            })],
            [.. charges.Select(c => new ChargeLine
            {
                ChargeId = new ChargeId(c.ChargeId),
                TransactionId = new PolicyTransactionId(c.TransactionId),
                ElementLocator = c.ElementLocator,
                CoverageCode = c.CoverageCode,
                ChargeType = c.ChargeType,
                ChargeCategory = c.ChargeCategory,
                AnnualRate = c.AnnualRate,
                Amount = new Money(JobSupport.Exact(c.Amount), Currency.FromCode(c.Currency.Trim())),
                LegalStatus = c.LegalStatus,
                Provisional = c.Provisional,
            })]);
    }

    /// <summary>
    /// The term state at <paramref name="validAt"/>: stored states are facts of bind or later transactions; Scheduled →
    /// InForce and InForce → Expired follow from the period and are derived on read, never written (REQ-POL-131). A cancelled
    /// term was Cancelled only from its cancellation date: before it, the as-of state is the one the period gives (the knownAt
    /// dimension is the caller's, by which term version it passes).
    /// </summary>
    private static PolicyTermState StateAt(TermRecord term, Instant validAt)
    {
        var state = Codes.Parse<PolicyTermState>(term.State);
        if (state == PolicyTermState.Cancelled && term.CancelledAt is { } cancelledAt && validAt < JobReader.Time(cancelledAt))
        {
            state = PolicyTermState.InForce;
        }

        if (state is PolicyTermState.Scheduled or PolicyTermState.InForce)
        {
            state = validAt < JobReader.Time(term.ValidFrom) ? PolicyTermState.Scheduled : PolicyTermState.InForce;
        }

        if (state == PolicyTermState.InForce && validAt >= JobReader.Time(term.ValidTo))
        {
            state = PolicyTermState.Expired;
        }

        return state;
    }

    private static PolicyView View(PolicyRecord policy, TermRecord? term, Instant validAt, string legalEntityCode) => new()
    {
        PolicyId = new PolicyId(policy.PolicyId),
        PolicyNumber = PolicyNumber.Parse(policy.PolicyNumber),
        ProductCode = policy.ProductCode,
        PolicyholderPartyId = new PartyId(policy.PolicyholderPartyId),
        AccountId = policy.AccountId is { } account ? new AccountId(account) : null,
        LegalEntity = legalEntityCode,
        Jurisdiction = policy.Jurisdiction.Trim(),
        Status = term is null ? null : Codes.Api(StateAt(term, validAt)),
        RecordedAt = JobReader.Time(policy.RecordedAt),
    };

    private static TermView Term(TermRecord term, Instant validAt) => new()
    {
        TermId = new PolicyTermId(term.TermId),
        TermNumber = term.TermNumber,
        Period = InstantRange.Of(JobReader.Time(term.ValidFrom), JobReader.Time(term.ValidTo)),
        State = Codes.Api(StateAt(term, validAt)),
        ProductVersion = ProductVersionNumber.Parse(term.ProductVersion),
        ArtefactHash = Sha256Hash.Parse(term.ArtefactHash),
        RatingArtefactHash = term.RatingArtefactHash is null ? null : Sha256Hash.Parse(term.RatingArtefactHash),
        ResolutionHash = ResolutionHash.Parse(term.ResolutionHash),
        ConfigurationHash = ConfigurationHash.Parse(term.ConfigurationHash),
        Currency = Currency.FromCode(term.Currency.Trim()),
        ProducerCode = term.ProducerCode,
        PaymentPlanRef = term.PaymentPlanRef,
        WrittenDate = BusinessDate.Parse(term.WrittenDate),
        RecordedAt = JobReader.Time(term.RecordedFrom),
    };

    private sealed class WatermarkRecord
    {
        public Guid PolicyId { get; set; }

        public DateTime Watermark { get; set; }
    }

    private sealed class PolicyRecord
    {
        public Guid PolicyId { get; set; }

        public string PolicyNumber { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public Guid PolicyholderPartyId { get; set; }

        public Guid? AccountId { get; set; }

        public string Jurisdiction { get; set; } = string.Empty;

        public DateTime RecordedAt { get; set; }
    }

    private sealed class TermRecord
    {
        public Guid TermId { get; set; }

        public Guid PolicyId { get; set; }

        public int TermNumber { get; set; }

        public DateTime ValidFrom { get; set; }

        public DateTime ValidTo { get; set; }

        public DateTime RecordedFrom { get; set; }

        public string State { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = string.Empty;

        public string ArtefactHash { get; set; } = string.Empty;

        public string? RatingArtefactHash { get; set; }

        public string ResolutionHash { get; set; } = string.Empty;

        public string ConfigurationHash { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;

        public string? ProducerCode { get; set; }

        public DateTime? CancelledAt { get; set; }

        public string PaymentPlanRef { get; set; } = string.Empty;

        public string WrittenDate { get; set; } = string.Empty;
    }

    private sealed class SegmentRecord
    {
        public Guid SegmentId { get; set; }

        public Guid TransactionId { get; set; }

        public DateTime ValidFrom { get; set; }

        public DateTime ValidTo { get; set; }

        public DateTime RecordedFrom { get; set; }

        public DateTime? RecordedTo { get; set; }

        public string SnapshotHash { get; set; } = string.Empty;

        public string Snapshot { get; set; } = "{}";
    }

    private sealed class TransactionRecord
    {
        public Guid TransactionId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public int Sequence { get; set; }

        public Guid JobId { get; set; }

        public DateTime EffectiveAt { get; set; }

        public DateTime RecordedAt { get; set; }

        public decimal Premium { get; set; }

        public decimal Taxes { get; set; }

        public decimal Total { get; set; }

        public string Currency { get; set; } = string.Empty;
    }

    private sealed class ChargeRecord
    {
        public Guid ChargeId { get; set; }

        public Guid TransactionId { get; set; }

        public string ElementLocator { get; set; } = string.Empty;

        public string CoverageCode { get; set; } = string.Empty;

        public string ChargeType { get; set; } = string.Empty;

        public string ChargeCategory { get; set; } = string.Empty;

        public decimal AnnualRate { get; set; }

        public decimal Amount { get; set; }

        public string Currency { get; set; } = string.Empty;

        public string? LegalStatus { get; set; }

        public bool? Provisional { get; set; }
    }
}
