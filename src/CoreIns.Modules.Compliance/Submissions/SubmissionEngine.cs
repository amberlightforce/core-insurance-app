using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Platform;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DocApi = CoreIns.Modules.Documents.Contracts.Api;
using Persistence = CoreIns.Modules.Compliance.Persistence;

namespace CoreIns.Modules.Compliance.Submissions;

/// <summary>The maker-checker binding of a submission to <c>plt.Approval</c> (in process): approval type, authority type (limited by the submission type) and subject.</summary>
internal static class SubmissionApproval
{
    public const string Type = "CMP.SUBMISSION";

    public const string ReferralRole = "Staff.ComplianceOfficer";

    public const string SubmissionTypeDimension = "submissionType";

    public static AuthorityTypeCode Authority { get; } = AuthorityTypeCode.Parse(Type);

    public static AuthorityTypeDefinition Definition { get; } = new(
        Authority, ModuleCode.CMP, new LocalizedText("Έγκριση ρυθμιστικής υποβολής", "Regulatory submission approval"),
        [new AuthorityDimensionDefinition(SubmissionTypeDimension, DimensionKind.Code)]);

    public static ObjectRef Subject(Guid id) => new(ModuleCode.CMP, "Submission", id.ToString("D", CultureInfo.InvariantCulture));
}

/// <summary>Regulatory submissions tracker (BO-CMP-09): register, link evidence, maker-checker, filing, response, resubmission (PRD-11 section 9.1).</summary>
internal sealed class SubmissionEngine(
    Persistence.ComplianceDbContext db,
    ClockEngine clocks,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals,
    IHostEnvironment environment,
    IServiceProvider services)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Instant Now => clocks.Now;

    private string Actor => context.Actor.Id;

    private LegalEntityId LegalEntity => ClockLookup.LegalEntity(context, legalEntities);

    private static DomainError Err(string code, string message) => DomainError.Of(ModuleCode.CMP, code, message);

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private IReadOnlyList<string> ActorKeys =>
        context.OnBehalfOf is { } principal ? [context.Actor.ToString(), principal.ToString()] : [context.Actor.ToString()];

    // --- views ---

    public async Task<SubmissionView?> FindViewAsync(Guid id, CancellationToken ct) =>
        await FindAsync(id, ct).ConfigureAwait(false) is { } row ? (await ViewsAsync([row], ct).ConfigureAwait(false))[0] : null;

    public async Task<IReadOnlyList<SubmissionView>> ViewsAsync(IReadOnlyCollection<SubmissionRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.SubmissionId).ToList();
        var history = await db.Set<SubmissionHistoryRow>().AsNoTracking().Where(h => ids.Contains(h.SubmissionId)).OrderBy(h => h.At).ThenBy(h => h.HistoryId)
            .ToListAsync(ct).ConfigureAwait(false);
        var today = Now.ToBusinessDate(clocks.Zone);
        return [.. rows.Select(r => View(r, [.. history.Where(h => h.SubmissionId == r.SubmissionId)], today))];
    }

    private static SubmissionView View(SubmissionRow r, List<SubmissionHistoryRow> history, BusinessDate today) => new()
    {
        SubmissionId = r.SubmissionId,
        Number = r.Number,
        Version = r.Version,
        PreviousVersionId = r.PreviousVersionId,
        Status = Enum.Parse<SubmissionView.StatusValue>(r.Status.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
        SubmissionType = r.SubmissionType,
        Regulator = r.Regulator,
        PeriodLabel = r.PeriodLabel,
        PeriodFrom = r.PeriodFrom,
        PeriodTo = r.PeriodTo,
        DueDate = r.DueDate,
        DueLegalStatus = Enum.Parse<SubmissionView.DueLegalStatusValue>(r.DueLegalStatus, ignoreCase: true),
        Overdue = r.DueDate is { } due && SubmissionStatuses.IsOpen(r.Status) && today.CompareTo(due) > 0,
        Evidence = Evidence(r.Evidence),
        ApprovalStatus = Enum.Parse<SubmissionView.ApprovalStatusValue>(r.ApprovalStatus, ignoreCase: true),
        ApprovalRequestId = r.ApprovalRequestId is { } a ? new ApprovalRequestId(a) : null,
        PreparedBy = r.PreparedBy,
        RequestedBy = r.RequestedBy,
        ApprovedBy = r.ApprovedBy,
        FilingChannel = r.FilingChannel,
        FiledAt = r.FiledAt,
        Filer = r.Filer,
        FilingReference = r.FilingReference,
        ReceiptHash = r.ReceiptHash is { } h ? Sha256Hash.Parse(h) : null,
        ResponseOutcome = r.ResponseOutcome,
        ResponseReference = r.ResponseReference,
        ResponseNote = r.ResponseNote,
        RespondedAt = r.RespondedAt,
        Note = r.Note,
        CreatedAt = r.CreatedAt,
        RecordVersion = r.RecordVersion,
        History =
        [
            .. history.Select(x => new SubmissionHistoryEntry
            {
                Kind = Enum.Parse<SubmissionHistoryEntry.KindValue>(x.Kind, ignoreCase: true),
                FromStatus = x.FromStatus,
                ToStatus = x.ToStatus,
                Actor = x.Actor,
                At = x.At,
                Text = x.Text,
            }),
        ],
    };

    private static List<SubmissionEvidenceRef> Evidence(string json) => JsonSerializer.Deserialize<List<SubmissionEvidenceRef>>(json, Json) ?? [];

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static Sha256Hash ContentHash(SubmissionRow r)
    {
        var text = new StringBuilder("CMP.SUBMISSION/v1|")
            .Append(r.SubmissionId.ToString("D", CultureInfo.InvariantCulture)).Append('|').Append(r.Version).Append('|')
            .Append(r.SubmissionType).Append('|').Append(r.Regulator).Append('|').Append(r.PeriodLabel).Append('|')
            .Append(r.PeriodFrom?.Value.ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(r.PeriodTo?.Value.ToString("O", CultureInfo.InvariantCulture)).Append('|')
            .Append(r.DueDate?.Value.ToString("O", CultureInfo.InvariantCulture));
        foreach (var e in Evidence(r.Evidence).OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.RefId, StringComparer.Ordinal))
        {
            text.Append('|').Append(e.Kind).Append(':').Append(e.RefId).Append(':').Append(e.Hash?.Value);
        }

        return Sha256Hash.ComputeUtf8(text.ToString());
    }

    // --- reads ---

    public async Task<SubmissionRow?> FindAsync(Guid id, CancellationToken ct)
    {
        var legalEntity = LegalEntity;
        return await db.Set<SubmissionRow>().SingleOrDefaultAsync(r => r.SubmissionId == id && r.LegalEntityId == legalEntity, ct).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<SubmissionView> Items, string? Next, int Limit)> QueryAsync(
        string? cursor, int? limit, string? status, string? submissionType, string? regulator, CancellationToken ct)
    {
        var take = KeysetPaging.Clamp(limit);
        var after = Dsar.DsarReader.Cursor(cursor);
        var legalEntity = LegalEntity;
        var query = db.Set<SubmissionRow>().AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim().ToUpperInvariant();
            query = query.Where(r => r.Status == wanted);
        }

        if (!string.IsNullOrWhiteSpace(submissionType))
        {
            var wanted = submissionType.Trim();
            query = query.Where(r => r.SubmissionType == wanted);
        }

        if (!string.IsNullOrWhiteSpace(regulator))
        {
            var wanted = regulator.Trim();
            query = query.Where(r => r.Regulator == wanted);
        }

        var allIds = await query.Select(r => r.SubmissionId).ToListAsync(ct).ConfigureAwait(false);
        var pageIds = KeysetPaging.IdsAfter(allIds, after, take + 1).ToList();
        var rows = (await query.Where(r => pageIds.Contains(r.SubmissionId)).ToListAsync(ct).ConfigureAwait(false)).OrderBy(r => r.SubmissionId).ToList();
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.SubmissionId)));
        return (await ViewsAsync(page, ct).ConfigureAwait(false), next, take);
    }

    /// <summary>The DOC archive gaps per UTC month, through the DOC in-process contract.</summary>
    public async Task<Result<SubmissionArchiveCompletenessResponse>> ArchiveCompletenessAsync(string? from, string? to, CancellationToken ct)
    {
        if (services.GetService<IDocumentEvidenceService>() is not { } documents)
        {
            return Err("PACK-UNAVAILABLE", "The document archive is not available.");
        }

        if (!BusinessDate.TryParse(from ?? string.Empty, out var start) || !BusinessDate.TryParse(to ?? string.Empty, out var end) || end.CompareTo(start) < 0)
        {
            return Err("VALIDATION-BLOCKING", "from and to must be dates (yyyy-MM-dd) with from not after to.");
        }

        var report = await documents.CompletenessAsync(start.Value, end.Value, ct).ConfigureAwait(false);
        return new SubmissionArchiveCompletenessResponse
        {
            From = report.From,
            To = report.To,
            Truncated = report.Truncated,
            Periods =
            [
                .. report.Periods.Select(p => new SubmissionArchivePeriod
                {
                    Period = p.Period,
                    Issued = p.Issued,
                    Ok = p.Ok,
                    OrphanCount = p.OrphanArchiveIds.Count,
                    Gaps = [.. p.Gaps.Select(g => new SubmissionArchiveGap { DocumentId = g.DocumentId, DocumentNumber = g.DocumentNumber, Kind = g.Kind.ToString() })],
                }),
            ],
        };
    }

    // --- commands ---

    private async Task<Result<SubmissionRow>> LoadAsync(Guid id, CancellationToken ct) =>
        await FindAsync(id, ct).ConfigureAwait(false) is { } row ? row : Err("NOT-FOUND", "The submission does not exist in your legal entity.");

    private void Transition(SubmissionRow row, string to, string? text = null)
    {
        db.Add(History("TRANSITION", row, row.Status, to, text));
        row.Status = to;
    }

    private SubmissionHistoryRow History(string kind, SubmissionRow row, string? from, string? to, string? text) => new()
    {
        HistoryId = Guid.NewGuid(),
        SubmissionId = row.SubmissionId,
        LegalEntityId = row.LegalEntityId,
        Kind = kind,
        FromStatus = from,
        ToStatus = to,
        Actor = Actor,
        At = Now,
        Text = text,
    };

    private async Task<Result<SubmissionView>> SaveAsync(SubmissionRow row, CancellationToken ct)
    {
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Err("SUBMISSION-STATE", "The submission changed meanwhile; reload and try again.");
        }

        return (await ViewsAsync([row], ct).ConfigureAwait(false))[0];
    }

    private void AddParticipants(SubmissionRow row) =>
        row.Participants = Serialize(JsonSerializer.Deserialize<List<string>>(row.Participants, Json)!.Concat(ActorKeys).Distinct(StringComparer.Ordinal));

    private static BusinessDate? Date(BusinessDate? date) => date;

    public async Task<Result<SubmissionView>> CreateAsync(SubmissionCreateRequest request, CancellationToken ct)
    {
        var now = Now;
        var id = Guid.NewGuid();
        var legalEntity = LegalEntity;
        if (request.PeriodFrom is { } pf && request.PeriodTo is { } pt && pt.CompareTo(pf) < 0)
        {
            return Err("VALIDATION-BLOCKING", "periodTo must not be before periodFrom.");
        }

        SubmissionRow row;
        if (request.ResubmitOf is { } previousId)
        {
            // Resubmission = a new version linked to the rejected one; the rejected record only moves to RESUBMITTED.
            var loaded = await LoadAsync(previousId, ct).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                return loaded.Error!;
            }

            var previous = loaded.Value;
            if (previous.Status != SubmissionStatuses.Rejected)
            {
                return Err("SUBMISSION-STATE", "Only a REJECTED submission can be resubmitted.");
            }

            row = new SubmissionRow
            {
                SubmissionId = id,
                LegalEntityId = legalEntity,
                Number = VersionedNumber(previous.Number, previous.Version + 1),
                Version = previous.Version + 1,
                PreviousVersionId = previous.SubmissionId,
                Status = SubmissionStatuses.InPreparation,
                SubmissionType = previous.SubmissionType,
                Regulator = previous.Regulator,
                PeriodLabel = previous.PeriodLabel,
                PeriodFrom = previous.PeriodFrom,
                PeriodTo = previous.PeriodTo,
                DueDate = request.DueDate ?? previous.DueDate,
                DueLegalStatus = request.DueLegalStatus?.ToString() ?? previous.DueLegalStatus,
                Evidence = previous.Evidence,
                PreparedBy = Actor,
                Note = request.Note,
                CreatedAt = now,
                RecordVersion = 1,
            };
            db.Add(History("TRANSITION", previous, previous.Status, SubmissionStatuses.Resubmitted, $"Replaced by version {row.Version} ({row.Number})."));
            previous.Status = SubmissionStatuses.Resubmitted;
            previous.RecordVersion++;
            db.Add(History("TRANSITION", row, null, SubmissionStatuses.InPreparation, $"Resubmission of version {previous.Version}."));
        }
        else
        {
            row = new SubmissionRow
            {
                SubmissionId = id,
                LegalEntityId = legalEntity,
                Number = $"SUB-{now.ToBusinessDate(clocks.Zone).Value.Year}-{id.ToString("N", CultureInfo.InvariantCulture)[^10..].ToUpperInvariant()}",
                Status = SubmissionStatuses.Planned,
                SubmissionType = request.SubmissionType.Trim(),
                Regulator = request.Regulator.Trim(),
                PeriodLabel = request.PeriodLabel.Trim(),
                PeriodFrom = Date(request.PeriodFrom),
                PeriodTo = Date(request.PeriodTo),
                DueDate = request.DueDate,
                // The due date comes from the caller (calendar or configuration); it is legally Unverified unless stated otherwise (D-USR-19).
                // TODO(hardening): derive the due date from the market-pack regulatory calendar and keep its legal status there.
                DueLegalStatus = request.DueLegalStatus?.ToString() ?? "Unverified",
                PreparedBy = Actor,
                Note = request.Note,
                CreatedAt = now,
                RecordVersion = 1,
            };
            db.Add(History("TRANSITION", row, null, SubmissionStatuses.Planned, "Registered."));
        }

        row.Participants = Serialize(ActorKeys);
        db.Add(row);
        return await SaveCreatedAsync(row, ct).ConfigureAwait(false);
    }

    private async Task<Result<SubmissionView>> SaveCreatedAsync(SubmissionRow row, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return Err("SUBMISSION-STATE", "The submission was already replaced by another version.");
        }

        return (await ViewsAsync([row], ct).ConfigureAwait(false))[0];
    }

    private static string VersionedNumber(string number, int version)
    {
        var at = number.LastIndexOf("-V", StringComparison.Ordinal);
        var root = at > 0 && int.TryParse(number.AsSpan(at + 2), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? number[..at] : number;
        return $"{root}-V{version}";
    }

    public async Task<Result<SubmissionView>> AttachContentAsync(SubmissionAttachContentRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        return await AttachAsync(loaded.Value, new SubmissionEvidenceRef
        {
            Kind = Complaints.ComplaintViews.Snake(request.Kind.ToString()),
            RefId = request.RefId.Trim(),
            Hash = request.Hash,
            Label = request.Label,
        }, ct).ConfigureAwait(false);
    }

    private async Task<Result<SubmissionView>> AttachAsync(SubmissionRow row, SubmissionEvidenceRef item, CancellationToken ct)
    {
        if (!SubmissionStatuses.IsEditable(row.Status))
        {
            return Err("SUBMISSION-STATE", "Evidence can only be linked while the submission is PLANNED or IN_PREPARATION.");
        }

        var evidence = Evidence(row.Evidence);
        if (!evidence.Any(e => e.Kind == item.Kind && e.RefId == item.RefId))
        {
            // TODO(hardening): check that a TAX_RETURN, MART_EXPORT or FISCAL_DOCUMENT reference exists through the owning module's contract.
            evidence.Add(item with { AddedBy = Actor, AddedAt = Now });
            row.Evidence = Serialize(evidence);
            AddParticipants(row);
        }

        if (row.Status == SubmissionStatuses.Planned)
        {
            Transition(row, SubmissionStatuses.InPreparation, "Content linked.");
        }

        return await SaveAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<Result<SubmissionView>> CommentAsync(SubmissionCommentRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        // A comment is a history line only: the submission record itself (also a SUBMITTED one) is not changed.
        var row = loaded.Value;
        db.Add(History("COMMENT", row, null, null, request.Text.Trim()));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await ViewsAsync([row], ct).ConfigureAwait(false))[0];
    }

    public async Task<Result<SubmissionView>> EvidencePackAsync(SubmissionEvidencePackRequest request, CancellationToken ct, Action<EvidencePackExportResponse>? exported = null)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        if (!SubmissionStatuses.IsEditable(row.Status))
        {
            return Err("SUBMISSION-STATE", "Evidence can only be linked while the submission is PLANNED or IN_PREPARATION.");
        }

        if (services.GetService<IDocumentEvidenceService>() is not { } documents)
        {
            return Err("PACK-UNAVAILABLE", "The document archive is not available.");
        }

        // The pack scope: the request, else the submission's own period.
        var createdFrom = request.CreatedFrom ?? (request.Subject is null ? row.PeriodFrom : null);
        var createdTo = request.CreatedTo ?? (request.Subject is null ? row.PeriodTo : null);
        if (request.Subject is null && (createdFrom is null || createdTo is null))
        {
            return Err("VALIDATION-BLOCKING", "Give createdFrom and createdTo, or a subject (the submission has no period dates).");
        }

        var pack = await documents.ExportPackAsync(
            new EvidencePackExportRequest
            {
                Subject = request.Subject,
                CreatedFrom = createdFrom?.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CreatedTo = createdTo?.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LegalClaimReason = request.LegalClaimReason,
            }, ct).ConfigureAwait(false);
        if (pack.IsFailure)
        {
            return pack.Error!;
        }

        exported?.Invoke(pack.Value);
        var label = $"Evidence pack: {pack.Value.DocumentCount} documents, {pack.Value.ExcludedCount} excluded (DSAR restriction)";
        return await AttachAsync(row, new SubmissionEvidenceRef { Kind = "EVIDENCE_PACK", RefId = Key(pack.Value.ArchiveId), Hash = pack.Value.Hash, Label = label }, ct).ConfigureAwait(false);
    }

    private DomainError? UnverifiedDue(SubmissionRow row) =>
        environment.IsProduction() && (row.DueDate is null || row.DueLegalStatus != "Verified")
            ? Err("SUBMISSION-UNVERIFIED-DUE", "The due date of this submission is not legally verified; it cannot be approved or filed in Production.")
            : null;

    public async Task<Result<SubmissionView>> ApproveAsync(SubmissionApproveRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        return request.Decision switch
        {
            SubmissionApproveRequest.DecisionValue.Request => await RequestApprovalAsync(row, comment, ct).ConfigureAwait(false),
            _ => await DecideAsync(row, request.Decision == SubmissionApproveRequest.DecisionValue.Approve, comment, ct).ConfigureAwait(false),
        };
    }

    private async Task<Result<SubmissionView>> RequestApprovalAsync(SubmissionRow row, string? comment, CancellationToken ct)
    {
        if (row.Status != SubmissionStatuses.InPreparation)
        {
            return Err("SUBMISSION-STATE", "Approval can be requested for an IN_PREPARATION submission only.");
        }

        if (Evidence(row.Evidence).Count == 0)
        {
            return Err("VALIDATION-BLOCKING", "Link at least one artefact (evidence pack, mart export, tax return) before approval is requested.");
        }

        if (UnverifiedDue(row) is { } unverified)
        {
            return unverified;
        }

        var hash = ContentHash(row);
        try
        {
            var response = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = SubmissionApproval.Type,
                    ObjectRef = SubmissionApproval.Subject(row.SubmissionId),
                    PayloadHash = hash,
                    Authority = new ApprovalAuthority
                    {
                        Type = SubmissionApproval.Type,
                        Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [SubmissionApproval.SubmissionTypeDimension] = row.SubmissionType },
                    },
                    ReferralRole = SubmissionApproval.ReferralRole,
                    Editors = JsonSerializer.Deserialize<List<string>>(row.Participants, Json),
                    Reason = $"Regulatory submission {row.Number} ({row.SubmissionType} {row.PeriodLabel} to {row.Regulator})" + (comment is null ? string.Empty : ": " + comment),
                },
                CommandOptions.New(),
                ct).ConfigureAwait(false);
            row.ApprovalRequestId = response.Request.RequestId;
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.ContentHash = hash.Value;
        row.ApprovalStatus = SubmissionApprovalStatuses.Pending;
        row.RequestedBy = Actor;
        AddParticipants(row);
        Transition(row, SubmissionStatuses.Ready, comment ?? "Approval requested.");
        return await SaveAsync(row, ct).ConfigureAwait(false);
    }

    private async Task<Result<SubmissionView>> DecideAsync(SubmissionRow row, bool approve, string? comment, CancellationToken ct)
    {
        if (row.Status != SubmissionStatuses.Ready || row.ApprovalStatus != SubmissionApprovalStatuses.Pending || row.ApprovalRequestId is not { } requestId || row.ContentHash is null)
        {
            return Err("SUBMISSION-STATE", "Only a READY submission with an open approval can be approved or rejected.");
        }

        var participants = JsonSerializer.Deserialize<List<string>>(row.Participants, Json)!;
        if (ActorKeys.Any(k => participants.Contains(k, StringComparer.Ordinal)))
        {
            return Err("APPROVAL-SAME-USER", "The person who prepared, changed or requested approval of a submission cannot decide it (maker is not checker).");
        }

        var hash = ContentHash(row);
        if (hash.Value != row.ContentHash)
        {
            return Err("SUBMISSION-STATE", "The submission changed after approval was requested.");
        }

        if (approve && UnverifiedDue(row) is { } unverified)
        {
            return unverified;
        }

        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId,
                    PayloadHash = hash,
                    Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                    Comment = comment,
                },
                CommandOptions.New(),
                ct).ConfigureAwait(false);
            if (approve)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = SubmissionApproval.Type, ObjectRef = SubmissionApproval.Subject(row.SubmissionId) },
                    ct).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != SubmissionApproval.Type)
                {
                    return Err("SUBMISSION-NOT-APPROVED", "The approval does not cover this submission's content.");
                }
            }
        }
        catch (DomainException ex)
        {
            return ex.Error.Code.Module == ModuleCode.PLT && ex.Error.Code.Name is "SELF-APPROVAL" or "EDITOR-CANNOT-APPROVE"
                ? Err("APPROVAL-SAME-USER", "The approver took part in the content (maker is not checker).")
                : ex.Error;
        }

        if (approve)
        {
            row.ApprovalStatus = SubmissionApprovalStatuses.Approved;
            row.ApprovedBy = Actor;
            db.Add(History("TRANSITION", row, row.Status, row.Status, comment ?? "Approved."));
        }
        else
        {
            // Back to preparation: the frozen hash and the request are dropped, the next request asks again.
            row.ApprovalStatus = SubmissionApprovalStatuses.None;
            row.ApprovalRequestId = null;
            row.ContentHash = null;
            row.RequestedBy = null;
            Transition(row, SubmissionStatuses.InPreparation, comment ?? "Approval rejected.");
        }

        return await SaveAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<Result<SubmissionView>> RecordFilingAsync(SubmissionRecordFilingRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.SubmissionId, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        if (row.Status != SubmissionStatuses.Ready)
        {
            return Err("SUBMISSION-STATE", "Only a READY submission can be filed; a filed one is never changed.");
        }

        if (row.ApprovalStatus != SubmissionApprovalStatuses.Approved || row.ContentHash is null || ContentHash(row).Value != row.ContentHash)
        {
            return Err("SUBMISSION-NOT-APPROVED", "The submission needs an approval of its current content before it is filed.");
        }

        if (UnverifiedDue(row) is { } unverified)
        {
            return unverified;
        }

        // TODO(hardening): stop the statutory clock of the deadline here (cmp.Clock.stop) once submissions start one.
        row.FilingChannel = request.Channel.Trim();
        row.FiledAt = request.FiledAt ?? Now;
        row.Filer = string.IsNullOrWhiteSpace(request.Filer) ? Actor : request.Filer.Trim();
        row.FilingReference = request.FilingReference.Trim();
        row.ReceiptHash = request.ReceiptHash?.Value;
        Transition(row, SubmissionStatuses.Submitted, $"Filed through {row.FilingChannel}, reference {row.FilingReference}.");
        return await SaveAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<Result<SubmissionView>> RecordResponseAsync(SubmissionRecordResponseRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(request.Id, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        if (row.Status != SubmissionStatuses.Submitted)
        {
            return Err("SUBMISSION-STATE", "A response can be recorded for a SUBMITTED submission only.");
        }

        row.ResponseOutcome = request.Outcome.ToString().ToUpperInvariant();
        row.ResponseReference = request.Reference?.Trim();
        row.ResponseNote = request.Note?.Trim();
        row.RespondedAt = Now;
        Transition(row, row.ResponseOutcome, request.Note);
        return await SaveAsync(row, ct).ConfigureAwait(false);
    }
}
