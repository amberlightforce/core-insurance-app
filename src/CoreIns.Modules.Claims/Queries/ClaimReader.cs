using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Claims.Queries;

/// <summary>
/// The read side of CLM: Dapper over the scope's <see cref="DbSession"/> connection (inside the open transaction when
/// there is one, so a command reads its own writes). Every query is filtered by the caller's legal entity: another
/// entity's claim is "not found", never an error that reveals it. Lists never carry free text (description, loss
/// location); only <see cref="GetAsync"/> and <see cref="GetFnolAsync"/> decrypt it.
/// </summary>
internal sealed class ClaimReader(DbSession session, ClaimProtection protection, IClock clock, IOptions<ClaimsOptions> options)
{
    private const string SummaryColumns =
        """
        c.claim_id AS ClaimId, c.claim_number AS ClaimNumber, c.policy_id AS PolicyId, c.policy_number AS PolicyNumber,
        c.insured_party_id AS InsuredPartyId, c.snapshot_ref AS SnapshotRef, c.snapshot_known_at AS SnapshotKnownAt,
        c.snapshot_status AS SnapshotStatus, c.policy_in_force_at_loss AS PolicyInForceAtLoss, c.policy_status_at_loss AS PolicyStatusAtLoss,
        c.product_code AS ProductCode, c.product_version AS ProductVersion, c.line_of_business AS LineOfBusiness, c.loss_at AS LossAt,
        c.loss_date::text AS LossDate, c.notice_on::text AS NoticeOn, c.loss_cause AS LossCause, c.channel AS Channel,
        c.handling_segment AS HandlingSegment, c.status AS Status, c.sub_status AS SubStatus, c.outcome AS Outcome,
        c.coverage_in_question AS CoverageInQuestion, c.duplicate_of_claim_id AS DuplicateOfClaimId, c.handler AS Handler,
        c.record_version AS RecordVersion, c.created_at AS CreatedAt, c.closed_at AS ClosedAt, c.jurisdiction AS Jurisdiction
        """;

    /// <summary>The claim header without free text (command results, lists); null when not found.</summary>
    public async Task<ClaimSummary?> GetSummaryAsync(LegalEntityId legalEntity, ClaimId claimId, CancellationToken cancellationToken)
    {
        var record = await ClaimAsync(legalEntity, claimId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : Summary(record);
    }

    /// <summary><c>clm.Claim.get</c>: the claim with exposures, claimants, incidents and its decrypted free text (REQ-CLM-061).</summary>
    public async Task<ClaimView?> GetAsync(LegalEntityId legalEntity, string legalEntityCode, ClaimId claimId, CancellationToken cancellationToken)
    {
        var record = await ClaimAsync(legalEntity, claimId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new { claim = claimId.Value };
        var texts = await connection.QuerySingleAsync<TextRecord>(new CommandDefinition(
            "SELECT loss_location_encrypted AS LossLocation, description_encrypted AS Description FROM clm.claim WHERE claim_id = @claim",
            args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var exposures = await ExposuresAsync(connection, claimId, cancellationToken).ConfigureAwait(false);
        var claimants = await connection.QueryAsync<ClaimantRecord>(new CommandDefinition(
            "SELECT claimant_id AS ClaimantId, party_id AS PartyId, claimant_type AS ClaimantType FROM clm.claimant WHERE claim_id = @claim ORDER BY created_at, claimant_id",
            args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var incidents = await connection.QueryAsync<IncidentRecord>(new CommandDefinition(
            """
            SELECT incident_id AS IncidentId, incident_type AS IncidentType, vehicle_ref AS VehicleRef, drivable AS Drivable, damage_areas AS DamageAreas
              FROM clm.incident WHERE claim_id = @claim ORDER BY created_at, incident_id
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // REQ-CLM-057/058: while a re-verification is open the view carries the latest demand (old and new ref) for the UI.
        var pending = record.SnapshotStatus == Codes.Of(SnapshotStatus.ReverificationRequired)
            ? await connection.QueryFirstOrDefaultAsync<PendingRecord>(new CommandDefinition(
                """
                SELECT old_snapshot_ref AS OldSnapshotRef, new_snapshot_ref AS NewSnapshotRef, cause_event_id AS CauseEventId, raised_at AS RaisedAt
                  FROM clm.reverification WHERE claim_id = @claim AND status = 'OPEN' ORDER BY raised_at DESC, reverification_id DESC LIMIT 1
                """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)
            : null;

        return new ClaimView
        {
            PendingReverification = pending is null
                ? null
                : new ClaimView.PendingReverificationDetail
                {
                    OldSnapshotRef = pending.OldSnapshotRef,
                    NewSnapshotRef = pending.NewSnapshotRef,
                    CauseEventId = pending.CauseEventId,
                    RaisedAt = Instant.FromUtcDateTime(pending.RaisedAt),
                },
            Summary = Summary(record),
            LegalEntity = legalEntityCode,
            Jurisdiction = record.Jurisdiction,
            LossLocation = await protection.DecryptAsync(legalEntity, ClaimProtection.LossLocationField, claimId.Value, texts.LossLocation, cancellationToken).ConfigureAwait(false),
            Description = await protection.DecryptAsync(legalEntity, ClaimProtection.DescriptionField, claimId.Value, texts.Description, cancellationToken).ConfigureAwait(false),
            Exposures = exposures,
            Claimants = [.. claimants.Select(c => new ClaimantView
            {
                ClaimantId = new ClaimantId(c.ClaimantId),
                PartyId = new PartyId(c.PartyId),
                ClaimantType = Codes.Map<ClaimantType, ClaimantView.ClaimantTypeValue>(Codes.Parse<ClaimantType>(c.ClaimantType)),
            })],
            Incidents = [.. incidents.Select(i => new IncidentView
            {
                IncidentId = i.IncidentId,
                IncidentType = IncidentView.IncidentTypeValue.Vehicle,
                VehicleRef = i.VehicleRef,
                Drivable = i.Drivable,
                DamageAreas = i.DamageAreas ?? [],
            })],
        };
    }

    /// <summary>The exposures of a claim in sequence order.</summary>
    public async Task<IReadOnlyList<ExposureView>> ExposuresAsync(ClaimId claimId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ExposuresAsync(connection, claimId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>clm.Fnol.get</c>: the FNOL snapshot as submitted (REQ-CLM-044), by FNOL id or claim id; null when not found.</summary>
    public async Task<FnolGetResponse?> GetFnolAsync(LegalEntityId legalEntity, Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await connection.QuerySingleOrDefaultAsync<FnolRecord>(new CommandDefinition(
            """
            SELECT f.fnol_id AS FnolId, f.claim_id AS ClaimId, c.claim_number AS ClaimNumber, f.channel AS Channel,
                   f.reporter_party_id AS ReporterPartyId, f.submitted_at AS SubmittedAt, f.created_by AS SubmittedBy,
                   f.payload_encrypted AS Payload
              FROM clm.fnol_snapshot f JOIN clm.claim c ON c.claim_id = f.claim_id
             WHERE f.legal_entity_id = @le AND (f.fnol_id = @id OR f.claim_id = @id)
            """, new { le = legalEntity.Value, id }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var json = await protection.DecryptAsync(legalEntity, ClaimProtection.FnolPayloadField, record.FnolId, record.Payload, cancellationToken).ConfigureAwait(false);
        return new FnolGetResponse
        {
            FnolId = record.FnolId,
            ClaimId = new ClaimId(record.ClaimId),
            ClaimNumber = ClaimNumber.Parse(record.ClaimNumber),
            Channel = record.Channel,
            ReporterPartyId = record.ReporterPartyId,
            SubmittedAt = Instant.FromUtcDateTime(record.SubmittedAt),
            SubmittedBy = record.SubmittedBy,
            Payload = JsonSerializer.Deserialize<FnolSubmitRequest>(json, SharedKernelJson.Options)!,
        };
    }

    /// <summary>
    /// <c>clm.Claim.search</c> (REQ-CLM-011 subset): exact claim number, policy number and insured party id, combined with
    /// AND, newest claim number first; keyset cursor = the last claim number of the page. Free text is never returned.
    /// </summary>
    public async Task<ClaimSearchPage> SearchAsync(
        LegalEntityId legalEntity, string? claimNumber, string? policyNumber, Guid? insuredPartyId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<ClaimRecord>(new CommandDefinition(
            $"""
             SELECT {SummaryColumns}
               FROM clm.claim c
              WHERE c.legal_entity_id = @le
                AND (@claimNumber::text IS NULL OR c.claim_number = @claimNumber)
                AND (@policyNumber::text IS NULL OR c.policy_number = @policyNumber)
                AND (@insured::uuid IS NULL OR c.insured_party_id = @insured)
                AND (@cursor::text IS NULL OR c.claim_number < @cursor)
              ORDER BY c.claim_number DESC
              LIMIT @take
             """,
            new { le = legalEntity.Value, claimNumber, policyNumber, insured = insuredPartyId, cursor, take = limit + 1 },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var page = rows.Take(limit).ToList();
        return new ClaimSearchPage
        {
            Items = [.. page.Select(r => new ClaimSearchItem { Claim = Summary(r) })],
            NextCursor = rows.Count > limit ? page[^1].ClaimNumber : null,
            Limit = limit,
        };
    }

    /// <summary>Probable duplicates (REQ-CLM-041): same policy, loss date within the window, same loss cause.</summary>
    public async Task<IReadOnlyList<DuplicateCandidate>> DuplicateCandidatesAsync(
        LegalEntityId legalEntity, Guid policyId, BusinessDate lossDate, string lossCause, int windowDays, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(Guid ClaimId, string ClaimNumber, string LossDate)>(new CommandDefinition(
            """
            SELECT claim_id, claim_number, loss_date::text
              FROM clm.claim
             WHERE legal_entity_id = @le AND policy_id = @policy AND loss_cause = @cause AND status <> 'DRAFT'
               AND loss_date BETWEEN @from::date AND @to::date
             ORDER BY claim_number
             LIMIT 20
            """,
            new
            {
                le = legalEntity.Value, policy = policyId, cause = lossCause,
                from = lossDate.AddDays(-windowDays).ToString(), to = lossDate.AddDays(windowDays).ToString(),
            },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new DuplicateCandidate
        {
            ClaimId = new ClaimId(r.ClaimId),
            ClaimNumber = ClaimNumber.Parse(r.ClaimNumber),
            Reasons = r.LossDate == lossDate.ToString()
                ? [ReferenceCodes.SamePolicy, ReferenceCodes.SameLossDate, ReferenceCodes.SameLossCause]
                : [ReferenceCodes.SamePolicy, ReferenceCodes.SameLossCause],
        })];
    }

    private async Task<ClaimRecord?> ClaimAsync(LegalEntityId legalEntity, ClaimId claimId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ClaimRecord>(new CommandDefinition(
            $"SELECT {SummaryColumns} FROM clm.claim c WHERE c.legal_entity_id = @le AND c.claim_id = @claim",
            new { le = legalEntity.Value, claim = claimId.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ExposureView>> ExposuresAsync(NpgsqlConnection connection, ClaimId claimId, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<ExposureRecord>(new CommandDefinition(
            """
            SELECT e.exposure_id AS ExposureId, e.exposure_number AS ExposureNumber, e.kind AS Kind, e.coverage_code AS CoverageCode,
                   e.claimant_id AS ClaimantId, cl.party_id AS ClaimantPartyId, e.incident_id AS IncidentId, e.status AS Status,
                   e.outcome AS Outcome, e.coverage_indication AS CoverageIndication, e.coverage_decision AS CoverageDecision,
                   e.duplicate_reason AS DuplicateReason, e.created_at AS CreatedAt
              FROM clm.exposure e JOIN clm.claimant cl ON cl.claimant_id = e.claimant_id
             WHERE e.claim_id = @claim
             ORDER BY e.sequence
            """, new { claim = claimId.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(Exposure)];
    }

    internal static ExposureView Exposure(ExposureRecord e) => new()
    {
        ExposureId = new ExposureId(e.ExposureId),
        ExposureNumber = ExposureNumber.Parse(e.ExposureNumber),
        Kind = Enum.GetValues<ExposureKind>().First(k => Codes.Of(k) == e.Kind),
        CoverageCode = e.CoverageCode,
        ClaimantId = new ClaimantId(e.ClaimantId),
        ClaimantPartyId = new PartyId(e.ClaimantPartyId),
        IncidentId = e.IncidentId,
        Status = e.Status == ClaimStates.Closed ? ExposureView.StatusValue.Closed : ExposureView.StatusValue.Open,
        Outcome = e.Outcome,
        CoverageIndication = Codes.Map<CoverageIndicationCode, ExposureView.CoverageIndicationValue>(Codes.Parse<CoverageIndicationCode>(e.CoverageIndication)),
        CoverageDecision = Codes.Map<CoverageDecisionCode, ExposureView.CoverageDecisionValue>(Codes.Parse<CoverageDecisionCode>(e.CoverageDecision)),
        DuplicateReason = e.DuplicateReason,
        CreatedAt = Instant.FromUtcDateTime(e.CreatedAt),
    };

    private ClaimSummary Summary(ClaimRecord r)
    {
        var noticeOn = BusinessDate.Parse(r.NoticeOn);
        var closedAt = r.ClosedAt is { } closed ? Instant.FromUtcDateTime(closed) : (Instant?)null;
        var asOf = options.Value.DateOf(closedAt ?? clock.Now);
        var state = ClaimStates.FromColumns(r.Status, r.SubStatus);
        return new ClaimSummary
        {
            ClaimId = new ClaimId(r.ClaimId),
            ClaimNumber = ClaimNumber.Parse(r.ClaimNumber),
            PolicyId = new PolicyId(r.PolicyId),
            PolicyNumber = PolicyNumber.Parse(r.PolicyNumber),
            InsuredPartyId = new PartyId(r.InsuredPartyId),
            SnapshotRef = r.SnapshotRef,
            SnapshotKnownAt = Instant.FromUtcDateTime(r.SnapshotKnownAt),
            SnapshotStatus = Codes.Map<SnapshotStatus, ClaimSummary.SnapshotStatusValue>(Codes.Parse<SnapshotStatus>(r.SnapshotStatus)),
            PolicyInForceAtLoss = r.PolicyInForceAtLoss,
            PolicyStatusAtLoss = r.PolicyStatusAtLoss,
            ProductCode = r.ProductCode,
            ProductVersion = r.ProductVersion,
            LineOfBusiness = r.LineOfBusiness,
            LossAt = Instant.FromUtcDateTime(r.LossAt),
            LossDate = BusinessDate.Parse(r.LossDate),
            NoticeOn = noticeOn,
            LossCause = r.LossCause,
            Channel = r.Channel,
            HandlingSegment = r.HandlingSegment,
            Status = state switch
            {
                ClaimState.Draft => ClaimSummary.StatusValue.Draft,
                ClaimState.Closed => ClaimSummary.StatusValue.Closed,
                _ => ClaimSummary.StatusValue.Open,
            },
            SubStatus = state is ClaimState.Draft or ClaimState.Closed ? null : Codes.Map<ClaimState, ClaimSubStatus>(state),
            Outcome = r.Outcome is null ? null : Codes.Map<ClaimOutcomeCode, ClaimOutcome>(Codes.Parse<ClaimOutcomeCode>(r.Outcome)),
            CoverageInQuestion = r.CoverageInQuestion,
            DuplicateOfClaimId = r.DuplicateOfClaimId is { } duplicate ? new ClaimId(duplicate) : null,
            Handler = r.Handler,
            OpenDays = Math.Max(0, asOf.DaysSince(noticeOn)),
            RecordVersion = r.RecordVersion,
            CreatedAt = Instant.FromUtcDateTime(r.CreatedAt),
            ClosedAt = closedAt,
        };
    }

    // Dapper targets (classes with setters; plain column types).
#pragma warning disable CA1812 // Instantiated by Dapper.
    internal sealed class ClaimRecord
    {
        public Guid ClaimId { get; set; }

        public string ClaimNumber { get; set; } = string.Empty;

        public Guid PolicyId { get; set; }

        public string PolicyNumber { get; set; } = string.Empty;

        public Guid InsuredPartyId { get; set; }

        public string SnapshotRef { get; set; } = string.Empty;

        public DateTime SnapshotKnownAt { get; set; }

        public string SnapshotStatus { get; set; } = string.Empty;

        public bool PolicyInForceAtLoss { get; set; }

        public string? PolicyStatusAtLoss { get; set; }

        public string ProductCode { get; set; } = string.Empty;

        public string? ProductVersion { get; set; }

        public string LineOfBusiness { get; set; } = string.Empty;

        public DateTime LossAt { get; set; }

        public string LossDate { get; set; } = string.Empty;

        public string NoticeOn { get; set; } = string.Empty;

        public string LossCause { get; set; } = string.Empty;

        public string Channel { get; set; } = string.Empty;

        public string HandlingSegment { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? SubStatus { get; set; }

        public string? Outcome { get; set; }

        public bool CoverageInQuestion { get; set; }

        public Guid? DuplicateOfClaimId { get; set; }

        public string? Handler { get; set; }

        public int RecordVersion { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string Jurisdiction { get; set; } = string.Empty;
    }

    internal sealed class ExposureRecord
    {
        public Guid ExposureId { get; set; }

        public string ExposureNumber { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string CoverageCode { get; set; } = string.Empty;

        public Guid ClaimantId { get; set; }

        public Guid ClaimantPartyId { get; set; }

        public Guid? IncidentId { get; set; }

        public string Status { get; set; } = string.Empty;

        public string? Outcome { get; set; }

        public string CoverageIndication { get; set; } = string.Empty;

        public string CoverageDecision { get; set; } = string.Empty;

        public string? DuplicateReason { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    private sealed class PendingRecord
    {
        public string OldSnapshotRef { get; set; } = string.Empty;

        public string NewSnapshotRef { get; set; } = string.Empty;

        public Guid CauseEventId { get; set; }

        public DateTime RaisedAt { get; set; }
    }

    private sealed class TextRecord
    {
        public byte[] LossLocation { get; set; } = [];

        public byte[] Description { get; set; } = [];
    }

    private sealed class ClaimantRecord
    {
        public Guid ClaimantId { get; set; }

        public Guid PartyId { get; set; }

        public string ClaimantType { get; set; } = string.Empty;
    }

    private sealed class IncidentRecord
    {
        public Guid IncidentId { get; set; }

        public string IncidentType { get; set; } = string.Empty;

        public string? VehicleRef { get; set; }

        public bool? Drivable { get; set; }

        public string[]? DamageAreas { get; set; }
    }

    private sealed class FnolRecord
    {
        public Guid FnolId { get; set; }

        public Guid ClaimId { get; set; }

        public string ClaimNumber { get; set; } = string.Empty;

        public string Channel { get; set; } = string.Empty;

        public Guid? ReporterPartyId { get; set; }

        public DateTime SubmittedAt { get; set; }

        public string SubmittedBy { get; set; } = string.Empty;

        public byte[] Payload { get; set; } = [];
    }
#pragma warning restore CA1812

    /// <summary>Cursor/limit parsing shared by the search endpoints (limit 1..200, default 50).</summary>
    public static bool TryPage(int? limit, out int take)
    {
        take = limit ?? 50;
        return take is >= 1 and <= 200;
    }

    /// <summary>Invariant text of an integer (metadata values).</summary>
    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
