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
/// <c>pol.Job.get</c> (REQ-POL-331): the job with every quote version, read with Dapper over the scope's connection and
/// filtered by the caller's legal entity (another entity's job is "not found").
/// </summary>
internal sealed class JobReader(DbSession session)
{
    public async Task<JobView?> GetAsync(LegalEntityId legalEntity, Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var job = await connection.QuerySingleOrDefaultAsync<JobRecord>(new CommandDefinition(
            """
            SELECT j.job_id AS JobId, j.job_number AS JobNumber, j.job_type AS JobType, j.state AS State, j.referred AS Referred,
                   j.policy_id AS PolicyId, p.policy_number AS PolicyNumber, j.policyholder_party_id AS PolicyholderPartyId,
                   j.account_id AS AccountId, j.product_code AS ProductCode, j.product_version AS ProductVersion, j.channel AS Channel,
                   j.producer_code AS ProducerCode, j.quote_type AS QuoteType, j.effective_at AS EffectiveAt, j.expiration_at AS ExpirationAt,
                   j.currency AS Currency, j.current_version_no AS CurrentVersionNo, j.bound_transaction_id AS BoundTransactionId,
                   j.created_at AS CreatedAt
              FROM pol.job j LEFT JOIN pol.policy p ON p.policy_id = j.policy_id
             WHERE j.job_id = @jobId AND j.legal_entity_id = @le
            """, new { jobId, le = legalEntity.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        var versions = await connection.QueryAsync<VersionRecord>(new CommandDefinition(
            """
            SELECT quote_id AS QuoteId, version_no AS VersionNo, state AS State, draft_version AS DraftVersion, risk_tree::text AS RiskTree,
                   charges::text AS Charges, issues::text AS Issues, premium AS Premium, taxes AS Taxes, total AS Total,
                   worksheet_id AS WorksheetId, quoted_at AS QuotedAt, valid_until AS ValidUntil
              FROM pol.quote_version WHERE job_id = @jobId ORDER BY version_no
            """, new { jobId }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var currency = Currency.FromCode(job.Currency.Trim());
        return new JobView
        {
            JobId = new JobId(job.JobId),
            JobNumber = JobNumber.Parse(job.JobNumber),
            JobType = job.JobType,
            State = Codes.Api(Codes.Parse<JobState>(job.State)),
            Referred = job.Referred,
            PolicyId = new PolicyId(job.PolicyId),
            PolicyNumber = job.PolicyNumber is null ? null : CoreIns.SharedKernel.Identifiers.PolicyNumber.Parse(job.PolicyNumber),
            PolicyholderPartyId = new PartyId(job.PolicyholderPartyId),
            AccountId = job.AccountId is { } account ? new AccountId(account) : null,
            ProductCode = job.ProductCode,
            ProductVersion = ProductVersionNumber.Parse(job.ProductVersion),
            Channel = job.Channel,
            ProducerCode = job.ProducerCode,
            QuoteType = job.QuoteType == "QUICK" ? JobView.QuoteTypeValue.Quick : JobView.QuoteTypeValue.Full,
            EffectiveAt = Time(job.EffectiveAt),
            ExpirationAt = Time(job.ExpirationAt),
            Currency = currency,
            CurrentVersionNo = job.CurrentVersionNo,
            TransactionId = job.BoundTransactionId is { } tx ? new PolicyTransactionId(tx) : null,
            CreatedAt = Time(job.CreatedAt),
            Versions = [.. versions.Select(v => new QuoteVersionView
            {
                QuoteId = new QuoteId(v.QuoteId),
                VersionNo = v.VersionNo,
                State = Enum.Parse<QuoteVersionView.StateValue>(Codes.Parse<QuoteState>(v.State).ToString()),
                DraftVersion = v.DraftVersion,
                RiskTree = JobSupport.FromJson<RiskTree>(v.RiskTree),
                Premium = v.Premium is { } premium ? new Money(JobSupport.Exact(premium), currency) : null,
                Taxes = v.Taxes is { } taxes ? new Money(JobSupport.Exact(taxes), currency) : null,
                Total = v.Total is { } total ? new Money(JobSupport.Exact(total), currency) : null,
                Charges = v.Charges is null ? [] : JobSupport.FromJson<List<ChargeLine>>(v.Charges),
                Issues = v.Issues is null ? [] : JobSupport.FromJson<List<UwIssue>>(v.Issues),
                WorksheetId = v.WorksheetId is null ? null : Sha256Hash.Parse(v.WorksheetId),
                QuotedAt = v.QuotedAt is { } quotedAt ? Time(quotedAt) : null,
                ValidUntil = v.ValidUntil is { } validUntil ? Time(validUntil) : null,
            })],
        };
    }

    internal static Instant Time(DateTime value) => Instant.FromUtcDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class JobRecord
    {
        public Guid JobId { get; set; }

        public string JobNumber { get; set; } = string.Empty;

        public string JobType { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public bool Referred { get; set; }

        public Guid PolicyId { get; set; }

        public string? PolicyNumber { get; set; }

        public Guid PolicyholderPartyId { get; set; }

        public Guid? AccountId { get; set; }

        public string ProductCode { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = string.Empty;

        public string Channel { get; set; } = string.Empty;

        public string? ProducerCode { get; set; }

        public string QuoteType { get; set; } = string.Empty;

        public DateTime EffectiveAt { get; set; }

        public DateTime ExpirationAt { get; set; }

        public string Currency { get; set; } = string.Empty;

        public int CurrentVersionNo { get; set; }

        public Guid? BoundTransactionId { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    private sealed class VersionRecord
    {
        public Guid QuoteId { get; set; }

        public int VersionNo { get; set; }

        public string State { get; set; } = string.Empty;

        public int DraftVersion { get; set; }

        public string RiskTree { get; set; } = "{}";

        public string? Charges { get; set; }

        public string? Issues { get; set; }

        public decimal? Premium { get; set; }

        public decimal? Taxes { get; set; }

        public decimal? Total { get; set; }

        public string? WorksheetId { get; set; }

        public DateTime? QuotedAt { get; set; }

        public DateTime? ValidUntil { get; set; }
    }
}
