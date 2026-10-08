using System.Globalization;
using System.Text;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Dapper;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>What a snapshot request names: exactly one of a policy id, a policy number or a snapshot reference.</summary>
internal sealed record SnapshotQuery(Guid? PolicyId, string? PolicyNumber, string? SnapshotRef, Instant? ValidAt, Instant? KnownAt);

/// <summary>
/// pol.Snapshot.get (REQ-POL-007): the immutable view of the policy, term and segment in force at a valid-time instant as
/// known at a record-time instant. The reference is <c>PS1.{policyId}.{segmentId|0}.{validAt}.{knownAt}</c> with the
/// instants in microseconds since the Unix epoch; every row read is immutable once recorded at or before knownAt, so the same
/// reference always yields the same bytes (POL P5). A knownAt in the future is refused: it is the one case where the same
/// question could get a different answer later.
/// </summary>
internal sealed class PolicySnapshots(PolicyReader reader, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    private const string RefPrefix = "PS1";
    private const long TicksPerMicrosecond = 10;

    public async Task<Result<SnapshotGetResponse>> GetAsync(SnapshotQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var named = (query.PolicyId is null ? 0 : 1) + (query.PolicyNumber is null ? 0 : 1) + (query.SnapshotRef is null ? 0 : 1);
        if (named != 1)
        {
            return Invalid("Give exactly one of policyId, policyNumber or snapshotRef.");
        }

        if (query.PolicyNumber is not null && !PolicyNumber.TryParse(query.PolicyNumber, out _))
        {
            return Invalid("The policy number is malformed.");
        }

        Guid? policyId = query.PolicyId;
        Guid? expectedSegment = null;
        Instant validAt;
        Instant knownAt;
        if (query.SnapshotRef is { } snapshotRef)
        {
            if (query.ValidAt is not null || query.KnownAt is not null)
            {
                return Invalid("validAt and knownAt are encoded in the snapshotRef; do not give them with it.");
            }

            if (!TryParseRef(snapshotRef, out var parsedPolicy, out expectedSegment, out validAt, out knownAt))
            {
                return Invalid("The snapshotRef is malformed.");
            }

            policyId = parsedPolicy;
        }
        else
        {
            var now = Truncate(clock.Now);
            validAt = query.ValidAt is { } valid ? Truncate(valid) : now;
            knownAt = query.KnownAt is { } known ? Truncate(known) : now;
        }

        // Also for a (possibly forged) reference: a future knownAt could answer differently once later changes are recorded.
        if (knownAt > Truncate(clock.Now))
        {
            return Invalid("knownAt cannot be in the future: a snapshot must stay the same when it is read again.");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var found = await reader.ReadSnapshotAsync(
            legalEntity, context.LegalEntity!.Value.Value, policyId, query.PolicyNumber, validAt, knownAt, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return JobSupport.NotFound("policy");
        }

        var segmentId = found.Content?.Segment.SegmentId.Value;
        if (expectedSegment is { } expected && expected != (segmentId ?? Guid.Empty))
        {
            return Invalid("The snapshotRef does not match the recorded segment.");
        }

        return found with { SnapshotRef = MakeRef(found.Policy.PolicyId.Value, segmentId, validAt, knownAt) };
    }

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.POL, "VALIDATION", message);

    /// <summary>Instants are compared and stored to the microsecond (the column precision).</summary>
    private static Instant Truncate(Instant instant) =>
        Instant.FromUtcDateTime(new DateTime(instant.ToUtcDateTime().Ticks / TicksPerMicrosecond * TicksPerMicrosecond, DateTimeKind.Utc));

    private static long Micros(Instant instant) => (instant.ToUtcDateTime().Ticks - DateTime.UnixEpoch.Ticks) / TicksPerMicrosecond;

    private static Instant FromMicros(long micros) => Instant.FromUtcDateTime(new DateTime((micros * TicksPerMicrosecond) + DateTime.UnixEpoch.Ticks, DateTimeKind.Utc));

    private static string MakeRef(Guid policyId, Guid? segmentId, Instant validAt, Instant knownAt) =>
        string.Create(CultureInfo.InvariantCulture, $"{RefPrefix}.{policyId:N}.{(segmentId ?? Guid.Empty):N}.{Micros(validAt)}.{Micros(knownAt)}");

    private static bool TryParseRef(string text, out Guid policyId, out Guid? segmentId, out Instant validAt, out Instant knownAt)
    {
        policyId = default;
        segmentId = null;
        validAt = default;
        knownAt = default;
        var parts = text.Split('.');
        if (parts.Length != 5 || parts[0] != RefPrefix
            || !Guid.TryParseExact(parts[1], "N", out policyId) || !Guid.TryParseExact(parts[2], "N", out var segment)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var valid)
            || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var known)
            || valid > 253_402_300_799_000_000 || known > 253_402_300_799_000_000)
        {
            return false;
        }

        segmentId = segment;
        validAt = FromMicros(valid);
        knownAt = FromMicros(known);
        return true;
    }
}

internal sealed partial class PolicyReader
{
    /// <summary>
    /// The snapshot at <paramref name="validAt"/> as known at <paramref name="knownAt"/>; null when the policy (by id or number, in
    /// the caller's legal entity) was not known then. The reference is filled in by <see cref="PolicySnapshots"/>.
    /// </summary>
    public async Task<SnapshotGetResponse?> ReadSnapshotAsync(
        LegalEntityId legalEntity, string legalEntityCode, Guid? policyId, string? policyNumber, Instant validAt, Instant knownAt, CancellationToken cancellationToken)
    {
        var args = Args(legalEntity, validAt, knownAt);
        if (policyId is null)
        {
            args.Add("number", policyNumber);
            var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            policyId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT policy_id FROM pol.policy WHERE legal_entity_id = @le AND policy_number = @number AND recorded_at <= @knownAt",
                args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (policyId is null)
            {
                return null;
            }
        }

        args.Add("policyId", policyId.Value);
        var policy = await PolicyAsync(args, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        var term = await TermAsync($"policy_id = @policyId AND {Known} AND {Valid}", args, cancellationToken).ConfigureAwait(false);
        SegmentRecord? segment = null;
        if (term is not null)
        {
            args.Add("term", term.TermId);
            var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            segment = await connection.QueryFirstOrDefaultAsync<SegmentRecord>(new CommandDefinition(
                $"""
                 SELECT segment_id AS SegmentId, transaction_id AS TransactionId, valid_from AS ValidFrom, valid_to AS ValidTo,
                        recorded_from AS RecordedFrom, recorded_to AS RecordedTo, snapshot_hash AS SnapshotHash, snapshot::text AS Snapshot
                   FROM pol.segment
                  WHERE term_id = @term AND legal_entity_id = @le AND {Known} AND {Valid}
                 """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var nearest = term ?? await NearestTermAsync(args, cancellationToken).ConfigureAwait(false);
        var status = nearest is null ? (PolicyTermState?)null : StateAt(nearest, validAt);
        var covered = term is not null && segment is not null;
        var inForce = covered && status is PolicyTermState.InForce or PolicyTermState.PendingCancellation;

        var response = new SnapshotGetResponse
        {
            SnapshotRef = string.Empty,
            ValidAt = validAt,
            KnownAt = knownAt,
            InForce = inForce,
            Status = status is { } s ? Codes.Api(s) : null,
            NotInForceReason = inForce ? null
                : covered ? SnapshotGetResponse.NotInForceReasonValue.TermNotInForce
                : SnapshotGetResponse.NotInForceReasonValue.NoTermAtInstant,
            Policy = new SnapshotPolicy
            {
                PolicyId = new PolicyId(policy.PolicyId),
                PolicyNumber = PolicyNumber.Parse(policy.PolicyNumber),
                ProductCode = policy.ProductCode,
                InsuredPartyId = policy.PolicyholderPartyId,
                LegalEntity = legalEntityCode,
                Jurisdiction = policy.Jurisdiction.Trim(),
            },
        };
        if (!inForce || term is null || segment is null)
        {
            return response;
        }

        var tree = JobSupport.FromJson<RiskTree>(segment.Snapshot);
        return response with
        {
            Content = new SnapshotContent
            {
                Term = Term(term, validAt),
                ProductVersion = ProductVersionNumber.Parse(term.ProductVersion),
                Segment = new SegmentView
                {
                    SegmentId = new SegmentId(segment.SegmentId),
                    TransactionId = new PolicyTransactionId(segment.TransactionId),
                    ValidPeriod = InstantRange.Of(JobReader.Time(segment.ValidFrom), JobReader.Time(segment.ValidTo)),
                    // Open by construction: a record closed after knownAt is still current as known at knownAt, and this keeps re-reads byte-identical.
                    RecordedPeriod = InstantRange.Open(JobReader.Time(segment.RecordedFrom)),
                    SnapshotHash = Sha256Hash.Parse(segment.SnapshotHash),
                },
                Vehicles = tree.Vehicles,
                Drivers = tree.Drivers,
                Coverages = tree.Coverages,
            },
        };
    }

    /// <summary>
    /// pol.Policy.search subset (REQ-POL-014): by policy number and/or insured (policyholder) party, ordered by policy number,
    /// keyset-paged after <paramref name="afterNumber"/>. Status is as of validAt, known at knownAt.
    /// </summary>
    public async Task<(IReadOnlyList<PolicySearchItem> Items, string? Next)> SearchAsync(
        LegalEntityId legalEntity, string? policyNumber, Guid? insuredPartyId, string? afterNumber, int limit, Instant validAt, Instant knownAt,
        CancellationToken cancellationToken)
    {
        var args = Args(legalEntity, validAt, knownAt);
        args.Add("number", policyNumber);
        args.Add("party", insuredPartyId);
        args.Add("after", afterNumber);
        args.Add("take", limit + 1);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var policies = (await connection.QueryAsync<PolicyRecord>(new CommandDefinition(
            """
            SELECT p.policy_id AS PolicyId, p.policy_number AS PolicyNumber, p.product_code AS ProductCode,
                   p.policyholder_party_id AS PolicyholderPartyId, p.account_id AS AccountId, p.jurisdiction AS Jurisdiction,
                   p.recorded_at AS RecordedAt
              FROM pol.policy p
             WHERE p.legal_entity_id = @le AND p.recorded_at <= @knownAt
               AND (@number::text IS NULL OR p.policy_number = @number)
               AND (@party::uuid IS NULL OR p.policyholder_party_id = @party)
               AND (@after::text IS NULL OR p.policy_number > @after)
             ORDER BY p.policy_number
             LIMIT @take
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var more = policies.Count > limit;
        if (more)
        {
            policies.RemoveAt(policies.Count - 1);
        }

        IEnumerable<TermRecord> termRows = [];
        if (policies.Count > 0)
        {
            termRows = await connection.QueryAsync<TermRecord>(new CommandDefinition(
                $"""
                 SELECT term_id AS TermId, policy_id AS PolicyId, term_number AS TermNumber, valid_from AS ValidFrom, valid_to AS ValidTo,
                        recorded_from AS RecordedFrom, state AS State, product_version AS ProductVersion, artefact_hash AS ArtefactHash,
                        rating_artefact_hash AS RatingArtefactHash, resolution_hash AS ResolutionHash, configuration_hash AS ConfigurationHash,
                        currency AS Currency, producer_code AS ProducerCode, payment_plan_ref AS PaymentPlanRef, written_date::text AS WrittenDate
                   FROM pol.policy_term
                  WHERE legal_entity_id = @le AND policy_id = ANY(@ids) AND {Known}
                 """,
                new DynamicParameters(args).AddValue("ids", policies.Select(p => p.PolicyId).ToArray()), session.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var terms = termRows.ToLookup(t => t.PolicyId);

        var items = policies.Select(policy =>
        {
            var own = terms[policy.PolicyId].ToList();
            // The term covering validAt, else the next one starting after it, else the last one ended by it (as pol.Policy.get).
            var term = own.FirstOrDefault(t => JobReader.Time(t.ValidFrom) <= validAt && JobReader.Time(t.ValidTo) > validAt)
                       ?? own.Where(t => JobReader.Time(t.ValidFrom) > validAt).OrderBy(t => t.ValidFrom).FirstOrDefault()
                       ?? own.Where(t => JobReader.Time(t.ValidTo) <= validAt).OrderByDescending(t => t.ValidTo).FirstOrDefault();
            return new PolicySearchItem
            {
                PolicyId = new PolicyId(policy.PolicyId),
                PolicyNumber = PolicyNumber.Parse(policy.PolicyNumber),
                ProductCode = policy.ProductCode,
                InsuredPartyId = policy.PolicyholderPartyId,
                Status = term is null ? null : Codes.Api(StateAt(term, validAt)),
                TermPeriod = term is null ? null : InstantRange.Of(JobReader.Time(term.ValidFrom), JobReader.Time(term.ValidTo)),
            };
        }).ToList();

        return (items, more ? EncodeCursor(policies[^1].PolicyNumber) : null);
    }

    internal static string EncodeCursor(string lastNumber) => Convert.ToBase64String(Encoding.UTF8.GetBytes(lastNumber)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a cursor: null cursor → (true, null); malformed → (false, null).</summary>
    internal static bool TryDecodeCursor(string? cursor, out string? lastNumber)
    {
        lastNumber = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            lastNumber = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return PolicyNumber.TryParse(lastNumber, out _);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal static class DynamicParametersExtensions
{
    public static DynamicParameters AddValue(this DynamicParameters parameters, string name, object value)
    {
        parameters.Add(name, value);
        return parameters;
    }
}
