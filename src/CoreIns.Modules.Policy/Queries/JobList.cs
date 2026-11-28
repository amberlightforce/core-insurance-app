using System.Globalization;
using System.Text;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Dapper;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>Filters of <c>pol.Job.list</c>; every one is optional and they combine with AND.</summary>
internal sealed record JobListFilter(
    string? Account, string? Policy, string? Participant, string? State, string? PartyId, string? ProducerCode, string? ProductCode, string? JobType);

/// <summary>
/// <c>pol.Job.list</c> (REQ-POL-331): jobs of the caller's legal entity, newest first, keyset-paged on (created_at, job_id) so a page
/// never skips or repeats a job when new ones arrive. Slim rows (the current version's total, no risk data); filtered by legal entity
/// in SQL, so another entity's jobs are never visible.
/// </summary>
internal sealed class JobList(DbSession session, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int DefaultLimit = 25;

    public async Task<Result<JobListPage>> ListAsync(JobListFilter filter, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > 200 || !TryDecodeCursor(cursor, out var after))
        {
            return Invalid("cursor is malformed or limit is outside 1..200.");
        }

        if (!TryGuid(filter.Account, out var account) || !TryGuid(filter.Policy, out var policy) || !TryGuid(filter.PartyId, out var party))
        {
            return Invalid("account, policy and partyId must be UUIDs.");
        }

        string[]? states = null;
        if (!string.IsNullOrWhiteSpace(filter.State))
        {
            var known = Enum.GetValues<JobState>().Select(Codes.Of).ToHashSet(StringComparer.Ordinal);
            states = [.. filter.State.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant())];
            if (states.Length == 0 || states.Any(s => !known.Contains(s)))
            {
                return Invalid("state must be a job state code (DRAFT, QUOTED, BOUND, ..., NOT_TAKEN).");
            }
        }

        string? jobType = null;
        if (!string.IsNullOrWhiteSpace(filter.JobType))
        {
            jobType = filter.JobType.Trim().ToUpperInvariant();
            if (!Enum.GetValues<JobType>().Select(Codes.Of).Contains(jobType, StringComparer.Ordinal))
            {
                return Invalid("jobType must be SUBMISSION, POLICY_CHANGE, CANCELLATION or RENEWAL.");
            }
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT j.job_id AS JobId, j.job_number AS JobNumber, j.job_type AS JobType, j.state AS State, j.referred AS Referred,
                   j.policy_id AS PolicyId, j.policyholder_party_id AS PolicyholderPartyId, j.account_id AS AccountId,
                   j.product_code AS ProductCode, j.producer_code AS ProducerCode, j.channel AS Channel, j.quote_type AS QuoteType,
                   j.current_version_no AS CurrentVersionNo, j.record_version AS RecordVersion, j.rating_artefact_hash AS RatingArtefactHash,
                   j.currency AS Currency, j.effective_at AS EffectiveAt, j.created_at AS CreatedAt, j.updated_at AS UpdatedAt, v.total AS Total
              FROM pol.job j
              LEFT JOIN pol.quote_version v ON v.job_id = j.job_id AND v.version_no = j.current_version_no
             WHERE j.legal_entity_id = @le
               AND (@account::uuid IS NULL OR j.account_id = @account)
               AND (@policy::uuid IS NULL OR j.policy_id = @policy)
               AND (@party::uuid IS NULL OR j.policyholder_party_id = @party)
               AND (@participant::text IS NULL OR @participant = ANY (j.participants))
               AND (@states::text[] IS NULL OR j.state = ANY (@states))
               AND (@producerCode::text IS NULL OR j.producer_code = @producerCode)
               AND (@productCode::text IS NULL OR j.product_code = @productCode)
               AND (@jobType::text IS NULL OR j.job_type = @jobType)
               AND (@afterAt::timestamptz IS NULL OR (j.created_at, j.job_id) < (@afterAt, @afterId))
             ORDER BY j.created_at DESC, j.job_id DESC
             LIMIT @fetch
            """,
            new
            {
                le = JobSupport.LegalEntity(context, legalEntities).Value, account, policy, party,
                participant = string.IsNullOrWhiteSpace(filter.Participant) ? null : filter.Participant.Trim(), states,
                producerCode = string.IsNullOrWhiteSpace(filter.ProducerCode) ? null : filter.ProducerCode.Trim(),
                productCode = string.IsNullOrWhiteSpace(filter.ProductCode) ? null : filter.ProductCode.Trim(), jobType,
                afterAt = after?.At, afterId = after?.Id, fetch = take + 1,
            },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var page = rows.Take(take).ToList();
        var items = page.Select(Item).ToList();
        var next = rows.Count > take ? EncodeCursor(page[^1].CreatedAt, page[^1].JobId) : null;
        return new JobListPage { Items = items, NextCursor = next, Limit = take };
    }

    private static JobListItem Item(Row r)
    {
        var currency = Currency.FromCode(r.Currency.Trim());
        return new JobListItem
        {
            JobId = new JobId(r.JobId),
            JobNumber = JobNumber.Parse(r.JobNumber),
            JobType = r.JobType,
            State = Codes.Api(Codes.Parse<JobState>(r.State)),
            Referred = r.Referred,
            PolicyId = new PolicyId(r.PolicyId),
            PolicyholderPartyId = new PartyId(r.PolicyholderPartyId),
            AccountId = r.AccountId is { } account ? new AccountId(account) : null,
            ProductCode = r.ProductCode,
            ProducerCode = r.ProducerCode,
            Channel = r.Channel,
            QuoteType = r.QuoteType == "QUICK" ? JobListItem.QuoteTypeValue.Quick : JobListItem.QuoteTypeValue.Full,
            CurrentVersionNo = r.CurrentVersionNo,
            RecordVersion = r.RecordVersion,
            Total = r.Total is { } total ? new Money(JobSupport.Exact(total), currency) : null,
            RatingArtefactHash = r.RatingArtefactHash is null ? null : Sha256Hash.Parse(r.RatingArtefactHash),
            EffectiveAt = JobReader.Time(r.EffectiveAt),
            CreatedAt = JobReader.Time(r.CreatedAt),
            UpdatedAt = JobReader.Time(r.UpdatedAt),
        };
    }

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.POL, "VALIDATION", message);

    private static bool TryGuid(string? text, out Guid? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!Guid.TryParse(text, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string EncodeCursor(DateTime createdAt, Guid jobId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(createdAt.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + jobId.ToString("D")))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool TryDecodeCursor(string? cursor, out (DateTime At, Guid Id)? after)
    {
        after = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id)
                && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            {
                after = (new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException)
        {
            // falls through to malformed
        }

        return false;
    }

    private sealed class Row
    {
        public Guid JobId { get; set; }

        public string JobNumber { get; set; } = string.Empty;

        public string JobType { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public bool Referred { get; set; }

        public Guid PolicyId { get; set; }

        public Guid PolicyholderPartyId { get; set; }

        public Guid? AccountId { get; set; }

        public string ProductCode { get; set; } = string.Empty;

        public string? ProducerCode { get; set; }

        public string Channel { get; set; } = string.Empty;

        public string QuoteType { get; set; } = string.Empty;

        public int CurrentVersionNo { get; set; }

        public int RecordVersion { get; set; }

        public string? RatingArtefactHash { get; set; }

        public string Currency { get; set; } = string.Empty;

        public DateTime EffectiveAt { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public decimal? Total { get; set; }
    }
}
