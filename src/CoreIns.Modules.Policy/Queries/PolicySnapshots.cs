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
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Dapper;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>What a snapshot request names: exactly one of a policy id, a policy number or a snapshot reference.</summary>
internal sealed record SnapshotQuery(Guid? PolicyId, string? PolicyNumber, string? SnapshotRef, Instant? ValidAt, Instant? KnownAt);

/// <summary>A snapshot answer with the effective knownAt it was given at and its live supersession metadata.</summary>
internal sealed record SnapshotResult(SnapshotGetResponse Snapshot, Instant EffectiveKnownAt, SnapshotSupersession Supersession);

/// <summary>
/// pol.Snapshot.get (REQ-POL-007): the immutable view of the policy, term and segment in force at a valid-time instant as
/// known at a record-time instant. The reference is <c>PS1.{policyId}.{segmentId|0}.{validAt}.{knownAt}</c> with the
/// instants in microseconds since the Unix epoch.
/// <para>
/// The knownAt is always the <b>effective</b> one, <c>min(requested or now, the policy's committed record-time watermark)</c>
/// (D-SL3-03 a): every row that commits later is stamped above the watermark, so the same reference always yields the same
/// bytes (POL P5), whatever the clock skew between replicas and however long a concurrent writer takes. A reference whose
/// knownAt is above the current watermark was never issued by POL and is refused as forged; a requested knownAt after both
/// the clock and the watermark is refused too (PITFALLS 13).
/// </para>
/// <para>
/// <see cref="SnapshotResult.Supersession"/> is computed on read by comparing the content at (validAt, current watermark) with
/// the content of the reference: a new segment id with identical content is not superseded (a split elsewhere in the term
/// leaves the days before it unchanged).
/// </para>
/// </summary>
internal sealed class PolicySnapshots(PolicyReader reader, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    private const string RefPrefix = "PS1";
    private const long TicksPerMicrosecond = 10;

    public async Task<Result<SnapshotGetResponse>> GetAsync(SnapshotQuery query, CancellationToken cancellationToken)
    {
        var result = await GetDetailedAsync(query, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result.Error! : result.Value.Snapshot with { EffectiveKnownAt = result.Value.EffectiveKnownAt, Supersession = result.Value.Supersession };
    }

    public async Task<Result<SnapshotResult>> GetDetailedAsync(SnapshotQuery query, CancellationToken cancellationToken)
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
        Instant? referenceKnownAt = null;
        var now = PolicyWriteLock.Truncate(clock.Now);
        Instant requestedKnownAt = now;
        if (query.SnapshotRef is { } snapshotRef)
        {
            if (query.ValidAt is not null || query.KnownAt is not null)
            {
                return Invalid("validAt and knownAt are encoded in the snapshotRef; do not give them with it.");
            }

            if (!TryParseRef(snapshotRef, out var parsedPolicy, out expectedSegment, out validAt, out var refKnownAt))
            {
                return Invalid("The snapshotRef is malformed.");
            }

            policyId = parsedPolicy;
            referenceKnownAt = refKnownAt;
        }
        else
        {
            validAt = query.ValidAt is { } valid ? PolicyWriteLock.Truncate(valid) : now;
            if (query.KnownAt is { } known)
            {
                requestedKnownAt = PolicyWriteLock.Truncate(known);
            }
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        if (await reader.ResolvePolicyAsync(legalEntity, policyId, query.PolicyNumber, cancellationToken).ConfigureAwait(false) is not var (resolvedPolicy, watermark))
        {
            return JobSupport.NotFound("policy");
        }

        Instant knownAt;
        if (referenceKnownAt is { } fromReference)
        {
            // Only POL issues references and only at an effective knownAt, which never exceeds the watermark. One above it is forged.
            if (fromReference > watermark)
            {
                return Invalid("The snapshotRef is above the policy's record-time watermark: it was not issued by this system.");
            }

            knownAt = fromReference;
        }
        else
        {
            // A requested knownAt after both the clock and everything recorded is the one question that could get a different answer later.
            if (requestedKnownAt > now && requestedKnownAt > watermark)
            {
                return Invalid("knownAt cannot be in the future: a snapshot must stay the same when it is read again.");
            }

            knownAt = PolicyReader.EffectiveKnownAt(requestedKnownAt, watermark);
        }

        var found = await reader.ReadSnapshotAsync(legalEntity, context.LegalEntity!.Value.Value, resolvedPolicy, validAt, knownAt, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return JobSupport.NotFound("policy");
        }

        var segmentId = found.Content?.Segment.SegmentId.Value;
        if (expectedSegment is { } expected && expected != (segmentId ?? Guid.Empty))
        {
            return Invalid("The snapshotRef does not match the recorded segment.");
        }

        var snapshot = found with { SnapshotRef = MakeRef(found.Policy.PolicyId.Value, segmentId, validAt, knownAt) };
        var supersession = await SupersessionAsync(legalEntity, snapshot, resolvedPolicy, watermark, cancellationToken).ConfigureAwait(false);
        return new SnapshotResult(snapshot, knownAt, supersession);
    }

    /// <summary>
    /// Compares the content at the same valid-time instant as known at the current watermark with the content of
    /// <paramref name="snapshot"/>. Different content is superseded; <c>supersededAt</c> is the first record time, after the
    /// snapshot's knownAt, at which the content differed.
    /// </summary>
    private async Task<SnapshotSupersession> SupersessionAsync(
        LegalEntityId legalEntity, SnapshotGetResponse snapshot, Guid policyId, Instant watermark, CancellationToken cancellationToken)
    {
        if (snapshot.KnownAt >= watermark)
        {
            return new SnapshotSupersession { Superseded = false };
        }

        var code = context.LegalEntity!.Value.Value;
        var current = await reader.ReadSnapshotAsync(legalEntity, code, policyId, snapshot.ValidAt, watermark, cancellationToken).ConfigureAwait(false);
        var hash = SnapshotContentHash(snapshot);
        if (current is null || SnapshotContentHash(current) == hash)
        {
            return new SnapshotSupersession { Superseded = false };
        }

        // The first record instant after the snapshot's knownAt at which the content differs (the last candidate is the watermark itself).
        var supersededAt = watermark;
        foreach (var candidate in await reader.RecordInstantsAsync(legalEntity, policyId, snapshot.KnownAt, watermark, cancellationToken).ConfigureAwait(false))
        {
            var at = await reader.ReadSnapshotAsync(legalEntity, code, policyId, snapshot.ValidAt, candidate, cancellationToken).ConfigureAwait(false);
            if (at is null || SnapshotContentHash(at) != hash)
            {
                supersededAt = candidate;
                break;
            }
        }

        var successor = MakeRef(policyId, current.Content?.Segment.SegmentId.Value, snapshot.ValidAt, watermark);
        return new SnapshotSupersession { Superseded = true, SuccessorRef = successor, SupersededAt = supersededAt };
    }

    /// <summary>
    /// SHA-256 of what a snapshot says about the risk, independent of how the record is cut: the term facts, the status and the
    /// hash of the segment's risk tree, but not segment or transaction ids, the segment's own valid period or any record time.
    /// </summary>
    internal static string SnapshotContentHash(SnapshotGetResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var content = snapshot.Content;
        return CanonicalJson.HashOf(new
        {
            snapshot.InForce,
            snapshot.Status,
            snapshot.NotInForceReason,
            Term = content is null ? null : content.Term with { RecordedAt = default },
            content?.ProductVersion,
            RiskHash = content?.Segment.SnapshotHash,
            content?.Vehicles,
            content?.Drivers,
            content?.Coverages,
        }).Value;
    }

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.POL, PolicyErrorNames.Validation, message);

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
    /// The snapshot at <paramref name="validAt"/> as known at <paramref name="knownAt"/> (the caller passes the effective knownAt,
    /// never above the policy's watermark); null when the policy (in the caller's legal entity) was not known then. The
    /// reference is filled in by <see cref="PolicySnapshots"/>.
    /// </summary>
    public async Task<SnapshotGetResponse?> ReadSnapshotAsync(
        LegalEntityId legalEntity, string legalEntityCode, Guid policyId, Instant validAt, Instant knownAt, CancellationToken cancellationToken)
    {
        var args = Args(legalEntity, validAt, knownAt);
        args.Add("policyId", policyId);
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
    /// The distinct record instants of the policy's term versions, segments and transactions in the half-open range
    /// (<paramref name="after"/>, <paramref name="upTo"/>], ascending, always ending with <paramref name="upTo"/>: the instants at
    /// which what is known about the policy changed.
    /// </summary>
    public async Task<IReadOnlyList<Instant>> RecordInstantsAsync(
        LegalEntityId legalEntity, Guid policyId, Instant after, Instant upTo, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var instants = (await connection.QueryAsync<DateTime>(new CommandDefinition(
            """
            SELECT recorded_from FROM pol.policy_term WHERE policy_id = @policyId AND legal_entity_id = @le AND recorded_from > @after AND recorded_from <= @upTo
            UNION SELECT recorded_from FROM pol.segment WHERE policy_id = @policyId AND legal_entity_id = @le AND recorded_from > @after AND recorded_from <= @upTo
            UNION SELECT recorded_at FROM pol.policy_transaction WHERE policy_id = @policyId AND legal_entity_id = @le AND recorded_at > @after AND recorded_at <= @upTo
            ORDER BY 1
            """,
            new { policyId, le = legalEntity.Value, after = after.ToUtcDateTime(), upTo = upTo.ToUtcDateTime() }, session.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).Select(JobReader.Time).ToList();
        if (instants.Count == 0 || instants[^1] != upTo)
        {
            instants.Add(upTo);
        }

        return instants;
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
            // Each policy is read as known at min(knownAt, its own watermark) (D-SL3-03 a).
            termRows = await connection.QueryAsync<TermRecord>(new CommandDefinition(
                """
                SELECT t.term_id AS TermId, t.policy_id AS PolicyId, t.term_number AS TermNumber, t.valid_from AS ValidFrom, t.valid_to AS ValidTo,
                       t.recorded_from AS RecordedFrom, t.state AS State, t.product_version AS ProductVersion, t.artefact_hash AS ArtefactHash,
                       t.rating_artefact_hash AS RatingArtefactHash, t.resolution_hash AS ResolutionHash, t.configuration_hash AS ConfigurationHash,
                       t.currency AS Currency, t.producer_code AS ProducerCode, t.payment_plan_ref AS PaymentPlanRef, t.written_date::text AS WrittenDate
                  FROM pol.policy_term t
                  JOIN pol.policy p ON p.policy_id = t.policy_id
                 WHERE t.legal_entity_id = @le AND t.policy_id = ANY(@ids)
                   AND t.recorded_from <= LEAST(@knownAt, p.last_recorded_at)
                   AND (t.recorded_to IS NULL OR t.recorded_to > LEAST(@knownAt, p.last_recorded_at))
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
