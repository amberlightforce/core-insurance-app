using System.Text.Json;
using CoreIns.Modules.Reinsurance.Authority;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary><c>ri.Programme.submit</c>.</summary>
internal sealed record SubmitProgramme(ProgrammeSubmitRequest Request) : ICommand<ProgrammeSubmitResponse>;

/// <summary><c>ri.Programme.approve</c>.</summary>
internal sealed record ApproveProgramme(ProgrammeApproveRequest Request) : ICommand<ProgrammeApproveResponse>;

/// <summary><c>ri.Programme.return</c>.</summary>
internal sealed record ReturnProgramme(ProgrammeReturnRequest Request) : ICommand<ProgrammeReturnResponse>;

/// <summary>Shape rules of submit.</summary>
internal sealed class SubmitProgrammeValidator : AbstractValidator<SubmitProgramme>
{
    public SubmitProgrammeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
    }
}

/// <summary>Shape rules of approve.</summary>
internal sealed class ApproveProgrammeValidator : AbstractValidator<ApproveProgramme>
{
    public ApproveProgrammeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
    }
}

/// <summary>Shape rules of return: the reason is required.</summary>
internal sealed class ReturnProgrammeValidator : AbstractValidator<ReturnProgramme>
{
    public ReturnProgrammeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).WithErrorCode("REASON_REQUIRED").When(c => c.Request is not null);
    }
}

/// <summary>
/// The PLT binding of a programme version (BO-RI-02, <c>RI.PROGRAMME_APPROVE</c>). RI sets type, subject, hash and authority; the client never does
/// (PITFALLS 3, 4). The hash is computed from the stored rows at submit and again at the decision.
/// </summary>
internal sealed class ProgrammeApprovals(ReinsuranceDbContext db, IPlatformApprovalService approvals, IEventPublisher events)
{
    public const string ApprovalType = "RI.PROGRAMME_APPROVE";

    public const string AggregateType = "RiProgramme";

    public const string ReferralRole = "Staff.ReinsuranceManager";

    public static ObjectRef SubjectOf(RiProgrammeId id) => ObjectRef.For(ModuleCode.RI, "Programme", id);

    public static Sha256Hash HashOf(ProgrammeRow programme, ProgrammeVersionRow version, ProgrammeContent content) =>
        content.Hash(programme.ProgrammeId, version.VersionNo, programme.Code, programme.LineCode, programme.ProgrammeYear);

    public static ApprovalRequestRequest BuildRequest(ProgrammeRow programme, ProgrammeVersionRow version, ProgrammeContent content, Sha256Hash hash) => new()
    {
        Type = ApprovalType,
        ObjectRef = SubjectOf(programme.ProgrammeId),
        PayloadHash = hash,
        Authority = new ApprovalAuthority { Type = ReinsuranceAuthorityTypes.ProgrammeApprove.Value, Amount = Money.Of(content.Layers.Sum(l => l.Limit), ContractRules.Eur) },
        ReferralRole = ReferralRole,
        Editors = [.. programme.Participants.Distinct(StringComparer.Ordinal)],
        Reason = $"Reinsurance programme {programme.Code} {programme.ProgrammeYear} (version {version.VersionNo}) submitted for approval.",
        Diff = JsonSerializer.SerializeToElement(new
        {
            legalEntityId = programme.LegalEntityId.Value,
            code = programme.Code,
            lineCode = programme.LineCode,
            programmeYear = programme.ProgrammeYear,
            versionNo = version.VersionNo,
            contentHash = hash.Value,
            name = content.Name,
            period = new { from = content.ValidFrom.ToString(), to = content.ValidTo.ToString() },
            layers = content.Layers.OrderBy(l => l.LayerNo).Select(l => new
            {
                l.LayerNo,
                attachment = ContractContent.Fixed(l.Attachment, ContractContent.MoneyScale),
                limit = ContractContent.Fixed(l.Limit, ContractContent.MoneyScale),
                contractId = l.ContractId.Value,
            }),
        }),
    };

    /// <summary>
    /// Null when the stored content hashed again is the content that was submitted (the version's hash) and the version is bound to a request;
    /// else <c>RI-ERR-STALE</c>.
    /// </summary>
    public static DomainError? CheckBinding(ProgrammeRow programme, ProgrammeVersionRow version, ProgrammeContent content, out Sha256Hash hash)
    {
        hash = HashOf(programme, version, content);
        return string.Equals(hash.Value, version.ContentHash, StringComparison.Ordinal) && version.ApprovalRequestId is not null
            ? null
            : RiErrors.Stale("The stored content differs from the content that was submitted for approval.");
    }

    /// <summary>
    /// PLT decides the request the version is bound to with the hash RI computed from the stored rows; the key of the person who decided
    /// on success. PLT's own refusals are mapped (maker / editor to <c>RI-ERR-SOD</c>, a changed request or hash to <c>RI-ERR-STALE</c>). A request
    /// decided in the PLT inbox is accepted only for exactly this hash and a checker who took no part in the programme; a rejected or
    /// withdrawn request cannot be approved, but a return takes the programme back to DRAFT.
    /// </summary>
    public async Task<Result<string>> DecideAsync(
        ProgrammeRow programme, Guid requestId, Sha256Hash hash, string self, bool reject, string? comment, CancellationToken cancellationToken)
    {
        try
        {
            var current = await approvals.GetAsync(requestId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (current.Request.Status != ApprovalStatus.PendingApproval)
            {
                if (reject)
                {
                    return self;
                }

                if (current.Request.Status != ApprovalStatus.Approved || current.Request.PayloadHash != hash || current.Decision is not { } decided)
                {
                    return RiErrors.Stale($"The approval request is {current.Request.Status}; return the programme to Draft and submit it again.");
                }

                var checker = $"{decided.Checker.Kind.ToString().ToUpperInvariant()}:{decided.Checker.Id}";
                if (programme.Participants.Contains(checker, StringComparer.Ordinal))
                {
                    return RiErrors.Sod("The approval in the PLT inbox was given by someone who took part in the programme (REQ-RI-057).");
                }

                return await VerifyAsync(programme, requestId, hash, checker, cancellationToken).ConfigureAwait(false);
            }

            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId, PayloadHash = hash, Comment = comment,
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            return reject ? self : await VerifyAsync(programme, requestId, hash, self, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error.Code.Name switch
            {
                "SELF-APPROVAL" or "EDITOR-CANNOT-APPROVE" => RiErrors.Sod("PLT refused: the checker took part in the programme (REQ-PLT-115)."),
                "APPROVAL-STALE" or "APPROVAL-HASH-MISMATCH" or "NOT-FOUND" => RiErrors.Stale("The approval request changed or no longer matches the programme."),
                _ => ex.Error,
            };
        }
    }

    private async Task<Result<string>> VerifyAsync(ProgrammeRow programme, Guid requestId, Sha256Hash hash, string checker, CancellationToken cancellationToken)
    {
        var verified = await approvals.VerifyForExecutionAsync(
            new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = ApprovalType, ObjectRef = SubjectOf(programme.ProgrammeId) },
            cancellationToken).ConfigureAwait(false);
        return verified.Ok && verified.Authority.Type == ReinsuranceAuthorityTypes.ProgrammeApprove.Value
            ? checker
            : RiErrors.Stale($"PLT refused execution: the request is {verified.Status}.");
    }

    /// <summary>Publishes <c>RIProgrammeApproved</c> for the version that just became active.</summary>
    public void PublishApproved(ProgrammeRow programme, ProgrammeContent content)
    {
        var id = programme.ProgrammeId.Value.ToString("D");
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RIProgrammeApprovedV1.Descriptor),
            AggregateType,
            id,
            new RIProgrammeApprovedV1 { ProgrammeId = programme.ProgrammeId, ContractIds = content.ContractIds, Period = DateRange.Of(content.ValidFrom, content.ValidTo) },
            BusinessKeys.Empty.With("riProgrammeId", id).With("programmeId", id)));
    }

    /// <summary>
    /// Null when every contract of the ladder is Approved or Active in the legal entity; else the error names those that are not (a programme
    /// is in force only when all its treaties are approved).
    /// </summary>
    public async Task<DomainError?> CheckContractsApprovedAsync(LegalEntityId legalEntity, ProgrammeContent content, CancellationToken cancellationToken)
    {
        var ids = content.ContractIds;
        var contracts = await db.Contracts.AsNoTracking().Where(c => ids.Contains(c.ContractId) && c.LegalEntityId == legalEntity).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (contracts.Count != ids.Count)
        {
            return DomainError.Of(ModuleCode.RI, "STATE", "A contract of the programme no longer exists in the legal entity.");
        }

        var notReady = contracts.Where(c => ContractStateModel.Parse(c.Status) is not (ContractStatus.Approved or ContractStatus.Active)).Select(c => c.ContractNumber).Order().ToList();
        return notReady.Count == 0
            ? null
            : DomainError.Of(ModuleCode.RI, "STATE", "The programme becomes active only when all its contracts are approved; not yet approved: " + string.Join(", ", notReady) + ".");
    }
}

/// <summary>
/// DRAFT to PENDING_APPROVAL (BO-RI-02). The content is read back from the stored rows, validated again (ladder, contracts in force carrying
/// their layers and the line, no overlap) and hashed; RI itself then opens the PLT approval request (<c>RI.PROGRAMME_APPROVE</c>, subject = this
/// programme) bound to that hash. No client input sets the type, subject, hash or authority. The submitter joins the programme's participants and
/// so can never approve it.
/// </summary>
internal sealed class SubmitProgrammeHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals) : ICommandHandler<SubmitProgramme, ProgrammeSubmitResponse>
{
    public async Task<Result<ProgrammeSubmitResponse>> HandleAsync(SubmitProgramme command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var programme = await ProgrammeSupport.LockAsync(db, legalEntity, request.ProgrammeId, cancellationToken).ConfigureAwait(false);
        if (programme is null)
        {
            return ProgrammeSupport.NotFound();
        }

        if (programme.RecordVersion != request.ExpectedRecordVersion)
        {
            return RiErrors.Stale(programme.RecordVersion);
        }

        var version = await ProgrammeSupport.CurrentVersionAsync(db, programme, cancellationToken).ConfigureAwait(false);
        if (version.Status != ProgrammeStatuses.Draft)
        {
            return DomainError.Of(ModuleCode.RI, "STATE", $"The programme is {version.Status}; only a DRAFT can be submitted.");
        }

        var content = await ProgrammeSupport.ReadContentAsync(db, version, cancellationToken).ConfigureAwait(false);
        if (ProgrammeRules.Validate(programme.Code, programme.LineCode, content, checkIdentity: false) is { } invalid)
        {
            return invalid;
        }

        await ProgrammeSupport.LockLineAsync(db, legalEntity, programme.LineCode, cancellationToken).ConfigureAwait(false);
        if (await ProgrammeSupport.CheckOverlapAsync(db, legalEntity, programme.LineCode, content.ValidFrom, content.ValidTo, programme.ProgrammeId, cancellationToken).ConfigureAwait(false) is { } overlap)
        {
            return overlap;
        }

        if (await ProgrammeSupport.CheckContractsAsync(db, legalEntity, content, programme.LineCode, programme.ProgrammeId, cancellationToken).ConfigureAwait(false) is { } contractError)
        {
            return contractError;
        }

        programme.Participants = [.. programme.Participants.Concat(ContractSupport.ActorKeys(context)).Distinct(StringComparer.Ordinal)];
        var hash = ProgrammeApprovals.HashOf(programme, version, content);
        Platform.Contracts.Api.ApprovalRequestResponse approval;
        try
        {
            approval = await approvals.RequestAsync(ProgrammeApprovals.BuildRequest(programme, version, content, hash), CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        var now = clock.Now;
        version.Status = ProgrammeStatuses.PendingApproval;
        version.ContentHash = hash.Value;
        version.ApprovalRequestId = approval.Request.RequestId;
        version.SubmittedBy = context.Actor.ToString();
        version.SubmittedAt = now;
        version.ReturnReason = null;
        programme.UpdatedAt = now;
        programme.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The programme changed meanwhile.");
        }

        var view = (await ProgrammeSupport.ViewsAsync(db, legalEntityCode.Value, [programme], cancellationToken).ConfigureAwait(false))[0];
        return new ProgrammeSubmitResponse { Programme = view, ApprovalRequestId = new ApprovalRequestId(approval.Request.RequestId) };
    }
}

/// <summary>
/// The checker's APPROVE (BO-RI-02), in order: the programme exists in the caller's legal entity and the caller saw the current record version
/// (<c>RI-ERR-STALE</c>, so of two racing deciders one wins); the current version is PENDING_APPROVAL (<c>RI-ERR-STATE</c>); the caller took no part
/// in the content (<c>RI-ERR-SOD</c>); the stored content hashed again is the content that was submitted; every contract of the ladder is Approved or
/// Active (<c>RI-ERR-STATE</c>); PLT decides on the hash (authority <c>RI.PROGRAMME_APPROVE</c>, maker and editors refused) and confirms it for
/// execution. Then, in one transaction, the previously active version of the programme becomes SUPERSEDED and this version ACTIVE;
/// <c>RIProgrammeApproved</c> is published.
/// </summary>
internal sealed class ApproveProgrammeHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    ProgrammeApprovals approvals) : ICommandHandler<ApproveProgramme, ProgrammeApproveResponse>
{
    public async Task<Result<ProgrammeApproveResponse>> HandleAsync(ApproveProgramme command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var load = await ProgrammeDecision.LoadAsync(db, context, legalEntity, request.ProgrammeId, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (load.IsFailure)
        {
            return load.Error!;
        }

        var (programme, version, content, hash) = load.Value;
        if (await approvals.CheckContractsApprovedAsync(legalEntity, content, cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var decided = await approvals.DecideAsync(programme, version.ApprovalRequestId!.Value, hash, context.Actor.ToString(), reject: false, comment: null, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        var now = clock.Now;
        int? supersededNo = null;
        try
        {
            // The previous active version first (a programme has one active version), then this one.
            var previous = await db.ProgrammeVersions
                .Where(v => v.ProgrammeId == programme.ProgrammeId && v.Status == ProgrammeStatuses.Active && v.VersionId != version.VersionId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var old in previous)
            {
                old.Status = ProgrammeStatuses.Superseded;
                supersededNo = old.VersionNo;
            }

            version.Status = ProgrammeStatuses.Active;
            version.DecidedBy = decided.Value;
            version.DecidedAt = now;
            programme.UpdatedAt = now;
            programme.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The programme changed meanwhile.");
        }

        approvals.PublishApproved(programme, content);
        var view = (await ProgrammeSupport.ViewsAsync(db, legalEntityCode.Value, [programme], cancellationToken).ConfigureAwait(false))[0];
        return new ProgrammeApproveResponse { Programme = view, SupersededVersionNo = supersededNo };
    }
}

/// <summary>The checker's RETURN (BO-RI-02): PLT rejects the request with the reason and the version goes back to DRAFT. Same checks as approve, except the contracts.</summary>
internal sealed class ReturnProgrammeHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    ProgrammeApprovals approvals) : ICommandHandler<ReturnProgramme, ProgrammeReturnResponse>
{
    public async Task<Result<ProgrammeReturnResponse>> HandleAsync(ReturnProgramme command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var load = await ProgrammeDecision.LoadAsync(db, context, legalEntity, request.ProgrammeId, request.ExpectedRecordVersion, cancellationToken).ConfigureAwait(false);
        if (load.IsFailure)
        {
            return load.Error!;
        }

        var (programme, version, _, hash) = load.Value;
        var decided = await approvals.DecideAsync(programme, version.ApprovalRequestId!.Value, hash, context.Actor.ToString(), reject: true, request.Reason, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        var now = clock.Now;
        version.Status = ProgrammeStatuses.Draft;
        version.ReturnReason = request.Reason;
        version.DecidedBy = context.Actor.ToString();
        version.DecidedAt = now;
        programme.UpdatedAt = now;
        programme.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The programme changed meanwhile.");
        }

        var view = (await ProgrammeSupport.ViewsAsync(db, legalEntityCode.Value, [programme], cancellationToken).ConfigureAwait(false))[0];
        return new ProgrammeReturnResponse { Programme = view };
    }
}

/// <summary>The checks approve and return share.</summary>
internal static class ProgrammeDecision
{
    public static async Task<Result<(ProgrammeRow Programme, ProgrammeVersionRow Version, ProgrammeContent Content, Sha256Hash Hash)>> LoadAsync(
        ReinsuranceDbContext db, RequestContext context, LegalEntityId legalEntity, RiProgrammeId programmeId, int expectedRecordVersion, CancellationToken cancellationToken)
    {
        var programme = await ProgrammeSupport.LockAsync(db, legalEntity, programmeId, cancellationToken).ConfigureAwait(false);
        if (programme is null)
        {
            return ProgrammeSupport.NotFound();
        }

        if (programme.RecordVersion != expectedRecordVersion)
        {
            return RiErrors.Stale(programme.RecordVersion);
        }

        var version = await ProgrammeSupport.CurrentVersionAsync(db, programme, cancellationToken).ConfigureAwait(false);
        if (version.Status != ProgrammeStatuses.PendingApproval || version.ApprovalRequestId is null)
        {
            return DomainError.Of(ModuleCode.RI, "STATE", $"The programme is {version.Status}; only a version waiting for its decision can be decided.");
        }

        var keys = ContractSupport.ActorKeys(context);
        if (keys.Any(k => programme.Participants.Contains(k, StringComparer.Ordinal) || string.Equals(version.SubmittedBy, k, StringComparison.Ordinal)))
        {
            return RiErrors.Sod("The person who entered, edited or submitted the programme cannot decide it (REQ-RI-057).");
        }

        var content = await ProgrammeSupport.ReadContentAsync(db, version, cancellationToken).ConfigureAwait(false);
        if (ProgrammeApprovals.CheckBinding(programme, version, content, out var hash) is { } mismatch)
        {
            return mismatch;
        }

        return (programme, version, content, hash);
    }
}

/// <summary>Audit facts of the programme approval commands.</summary>
internal static class ProgrammeApprovalAudit
{
    public static CommandAuditFacts Facts(RiProgrammeId id, RiProgramme? after, string fromStatus, object? extra = null)
    {
        var keys = BusinessKeys.Empty.With("riProgrammeId", id.Value.ToString("D"));
        return after is null
            ? new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.RI, "Programme", id), BusinessKeys = keys }
            : new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.RI, "Programme", id),
                ObjectNumber = after.Code,
                BusinessKeys = keys,
                Changes = AuditDiff.Compute(new { status = fromStatus }, new { status = after.Status.ToString(), versionNo = after.VersionNo, recordVersion = after.RecordVersion, extra }),
            };
    }
}

/// <summary>Audit facts of <c>ri.Programme.submit</c>.</summary>
internal sealed class SubmitProgrammeAuditor : ICommandAuditor<SubmitProgramme, ProgrammeSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitProgramme command, Result<ProgrammeSubmitResponse>? result) =>
        ProgrammeApprovalAudit.Facts(
            command.Request.ProgrammeId, result is { IsSuccess: true } ok ? ok.Value.Programme : null, "DRAFT",
            result is { IsSuccess: true } done ? new { approvalRequestId = done.Value.ApprovalRequestId.Value } : null);
}

/// <summary>Audit facts of <c>ri.Programme.approve</c>.</summary>
internal sealed class ApproveProgrammeAuditor : ICommandAuditor<ApproveProgramme, ProgrammeApproveResponse>
{
    public CommandAuditFacts Describe(ApproveProgramme command, Result<ProgrammeApproveResponse>? result) =>
        ProgrammeApprovalAudit.Facts(
            command.Request.ProgrammeId, result is { IsSuccess: true } ok ? ok.Value.Programme : null, "PENDING_APPROVAL",
            result is { IsSuccess: true } done ? new { superseded = done.Value.SupersededVersionNo } : null);
}

/// <summary>Audit facts of <c>ri.Programme.return</c>.</summary>
internal sealed class ReturnProgrammeAuditor : ICommandAuditor<ReturnProgramme, ProgrammeReturnResponse>
{
    public CommandAuditFacts Describe(ReturnProgramme command, Result<ProgrammeReturnResponse>? result) =>
        ProgrammeApprovalAudit.Facts(
            command.Request.ProgrammeId, result is { IsSuccess: true } ok ? ok.Value.Programme : null, "PENDING_APPROVAL",
            result is { IsSuccess: true } ? new { reason = command.Request.Reason } : null);
}
