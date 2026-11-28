using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Party.Services.Dsar;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Party.merge</c> (BO-PTY-05): the maker asks to merge one party into another; a checker decides.</summary>
internal sealed record RequestPartyMerge(PartyMergeRequest Request) : ICommand<PartyMergeResponse>;

/// <summary><c>pty.Party.approveMerge</c>: the checker approves (the merge is executed) or rejects.</summary>
internal sealed record DecidePartyMerge(PartyMergeApproveRequest Request) : ICommand<PartyMergeApproveResponse>;

/// <summary><c>pty.Party.unmerge</c>: reverses an executed merge within its window.</summary>
internal sealed record UnmergeParty(PartyUnmergeRequest Request) : ICommand<PartyUnmergeResponse>;

internal sealed class RequestPartyMergeValidator : AbstractValidator<RequestPartyMerge>
{
    public RequestPartyMergeValidator()
    {
        RuleFor(c => c.Request.SurvivorPartyId.Value).NotEmpty();
        RuleFor(c => c.Request.MergedPartyId.Value).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class DecidePartyMergeValidator : AbstractValidator<DecidePartyMerge>
{
    public DecidePartyMergeValidator()
    {
        RuleFor(c => c.Request.MergeId).NotEmpty();
        RuleFor(c => c.Request.Comment).MaximumLength(500);
        RuleFor(c => c.Request.Comment).NotEmpty().When(c => c.Request.Decision == PartyMergeApproveRequest.DecisionValue.Reject).WithMessage("A rejection needs a comment.");
    }
}

internal sealed class UnmergePartyValidator : AbstractValidator<UnmergeParty>
{
    public UnmergePartyValidator()
    {
        RuleFor(c => c.Request.MergeId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>Shared guards and the PLT binding of the merge commands.</summary>
internal static class MergeSupport
{
    private static readonly string[] Mergeable = [Codes.Of(PartyStatus.Prospect), Codes.Of(PartyStatus.Active), Codes.Of(PartyStatus.Inactive)];

    public static DomainError NotFound() => DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The merge or a party of it does not exist.");

    public static DomainError State(string detail) => DomainError.Of(ModuleCode.PTY, "STATE", detail);

    public static ObjectRef Subject(Guid mergeId) => new(ModuleCode.PTY, "PartyMerge", mergeId.ToString("D"));

    /// <summary>What the checker approves: the merge, its two parties and the status the absorbed party had.</summary>
    public static Sha256Hash HashOf(Guid mergeId, PartyId survivor, PartyId merged, string priorStatus) =>
        Sha256Hash.ComputeUtf8(string.Join('\n', PartyAuthorityTypes.PartyMergeApprovalType, mergeId.ToString("D"), survivor.Value.ToString("D"), merged.Value.ToString("D"), priorStatus));

    /// <summary>
    /// The rules a pair must meet to be merged (REQ-PTY-007), checked when the merge is requested and again when it is approved: both parties exist and
    /// are live (not merged, not deceased/dissolved/anonymised), neither is under a DSAR restriction, they are the same kind of party, and they do
    /// not hold different valid AFMs. Returns the two rows on success.
    /// </summary>
    public static async Task<Result<(PartyRow Survivor, PartyRow Merged)>> GuardPairAsync(
        PartyDbContext db, DuplicateFinder finder, LegalEntityId legalEntity, PartyId survivorId, PartyId mergedId, CancellationToken cancellationToken)
    {
        if (survivorId == mergedId)
        {
            return RoleSupport.Invalid("mergedPartyId", "SAME_PARTY", "A party cannot be merged into itself.");
        }

        var parties = await db.Parties.Where(p => p.LegalEntityId == legalEntity && (p.PartyId == survivorId || p.PartyId == mergedId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var survivor = parties.SingleOrDefault(p => p.PartyId == survivorId);
        var merged = parties.SingleOrDefault(p => p.PartyId == mergedId);
        if (survivor is null || merged is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "A party of the merge does not exist.");
        }

        foreach (var party in new[] { survivor, merged })
        {
            if (party.Status == Codes.Of(PartyStatus.Merged))
            {
                return DomainError.Of(ModuleCode.PTY, "MERGED", "A party of the merge was already merged; use the surviving party.");
            }

            if (party.Status == Codes.Of(PartyStatus.Restricted)
                || await db.DsarRestrictions.AnyAsync(r => r.PartyId == party.PartyId && r.RecordClass == PartyDsarWork.PartyRecord, cancellationToken).ConfigureAwait(false))
            {
                return DomainError.Of(ModuleCode.PTY, "RESTRICTED", "A party of the merge is restricted by a data-subject request and cannot be merged.");
            }

            if (!Mergeable.Contains(party.Status))
            {
                return State($"A party that is {party.Status} cannot be merged.");
            }
        }

        if (survivor.PartyType != merged.PartyType)
        {
            return RoleSupport.Invalid("mergedPartyId", "PARTY_TYPE", "A person and an organisation cannot be merged.");
        }

        if (await finder.HasIdentifierConflictAsync(legalEntity, survivorId.Value, mergedId.Value, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PTY, "MERGE-IDENTIFIER-CONFLICT", "The parties hold different valid AFMs; they are not the same party and cannot be merged.");
        }

        return (survivor, merged);
    }
}

/// <summary>
/// The maker's request. Nothing about either party changes yet; the PLT approval (type PTY.PARTY_MERGE, maker ≠ checker, human checker, merge
/// authority) is opened here and decided by <see cref="DecidePartyMergeHandler"/>.
/// </summary>
internal sealed class RequestPartyMergeHandler(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, DuplicateFinder finder, PartyMergeQueries queries, IPlatformApprovalService approvals)
    : ICommandHandler<RequestPartyMerge, PartyMergeResponse>
{
    public async Task<Result<PartyMergeResponse>> HandleAsync(RequestPartyMerge command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var guard = await MergeSupport.GuardPairAsync(db, finder, legalEntity, request.SurvivorPartyId, request.MergedPartyId, cancellationToken).ConfigureAwait(false);
        if (guard.IsFailure)
        {
            return guard.Error;
        }

        var (survivor, merged) = guard.Value;
        if (await db.PartyMerges.AnyAsync(
                m => m.Status == MergeStatus.Pending && (m.MergedPartyId == merged.PartyId || m.SurvivorPartyId == merged.PartyId || m.MergedPartyId == survivor.PartyId), cancellationToken)
            .ConfigureAwait(false))
        {
            return MergeSupport.State("One of the parties already has a merge waiting for approval.");
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var row = new PartyMergeRow
        {
            MergeId = EntityIds.NewGuid(),
            LegalEntityId = legalEntity,
            SurvivorPartyId = survivor.PartyId,
            MergedPartyId = merged.PartyId,
            Status = MergeStatus.Pending,
            Reason = request.Reason.Trim(),
            PriorStatus = merged.Status,
            RequestedBy = actor,
            RequestedAt = now,
        };
        var hash = MergeSupport.HashOf(row.MergeId, row.SurvivorPartyId, row.MergedPartyId, row.PriorStatus);
        row.ContentHash = hash.Value;
        var approval = await approvals.RequestAsync(
            new ApprovalRequestRequest
            {
                Type = PartyAuthorityTypes.PartyMergeApprovalType,
                ObjectRef = MergeSupport.Subject(row.MergeId),
                PayloadHash = hash,
                Authority = new ApprovalAuthority { Type = PartyAuthorityTypes.PartyMerge.Value },
                ReferralRole = "Staff.UnderwritingManager",
                Reason = row.Reason,
            },
            CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        row.ApprovalRequestId = approval.Request.RequestId;
        db.PartyMerges.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return MergeSupport.State("The party already has an open merge; reload and try again.");
        }

        return new PartyMergeResponse { Merge = await queries.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// The checker's decision. PLT enforces the segregation of duties (maker, editors, AI agents) and the authority; PTY recomputes the hash from the
/// stored merge (the client never supplies the approved content) and re-checks the merge rules before approving, so a party that was restricted or
/// got a conflicting AFM meanwhile is refused. Approval marks the absorbed party MERGED (survivor id and the merge row are its pointer) and
/// publishes <c>PartiesMerged</c>; nothing else is rewritten (other modules re-point on the event or through pty.Party.resolveSurvivor).
/// </summary>
internal sealed class DecidePartyMergeHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, DuplicateFinder finder, PartyMergeQueries queries,
    IPlatformApprovalService approvals, IOptions<PartyOptions> options)
    : ICommandHandler<DecidePartyMerge, PartyMergeApproveResponse>
{
    public async Task<Result<PartyMergeApproveResponse>> HandleAsync(DecidePartyMerge command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var reject = request.Decision == PartyMergeApproveRequest.DecisionValue.Reject;
        var legalEntity = protection.Current(context);
        var row = await db.PartyMerges.SingleOrDefaultAsync(m => m.MergeId == request.MergeId && m.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return MergeSupport.NotFound();
        }

        if (row.Status != MergeStatus.Pending || row.ApprovalRequestId is not { } requestId)
        {
            return MergeSupport.State($"Only a merge pending approval can be decided; the merge is {row.Status}.");
        }

        var hash = MergeSupport.HashOf(row.MergeId, row.SurvivorPartyId, row.MergedPartyId, row.PriorStatus);
        if (hash.Value != row.ContentHash)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "The stored merge no longer matches its content hash.");
        }

        PartyRow? merged = null;
        if (!reject)
        {
            var guard = await MergeSupport.GuardPairAsync(db, finder, legalEntity, row.SurvivorPartyId, row.MergedPartyId, cancellationToken).ConfigureAwait(false);
            if (guard.IsFailure)
            {
                return guard.Error;
            }

            merged = guard.Value.Merged;
        }

        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId, PayloadHash = hash, Comment = request.Comment,
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            if (!reject)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest
                    {
                        RequestId = requestId, Hash = hash, Type = PartyAuthorityTypes.PartyMergeApprovalType, ObjectRef = MergeSupport.Subject(row.MergeId),
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != PartyAuthorityTypes.PartyMerge.Value)
                {
                    return MergeSupport.State($"PLT refused execution: the request is {verified.Status}.");
                }
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        var now = clock.Now;
        row.DecidedBy = context.Actor.ToString();
        row.DecidedAt = now;
        row.DecisionComment = request.Comment;
        if (reject)
        {
            row.Status = MergeStatus.Rejected;
        }
        else
        {
            row.Status = MergeStatus.Executed;
            row.ExecutedAt = now;
            row.UnmergeWindowEnd = now.Plus(TimeSpan.FromDays(options.Value.UnmergeWindowDays));
            merged!.Status = Codes.Of(PartyStatus.Merged);
            merged.RecordVersion++;
            merged.UpdatedAt = now;
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(PartiesMergedV1.Descriptor),
                "Party",
                row.SurvivorPartyId.Value.ToString(),
                new PartiesMergedV1
                {
                    MergeId = row.MergeId,
                    SurvivorPartyId = row.SurvivorPartyId,
                    MergedPartyIds = [row.MergedPartyId],
                    RepointMap = [new RepointMapItem { From = row.MergedPartyId.Value.ToString("D"), To = row.SurvivorPartyId.Value.ToString("D") }],
                    UnmergeWindowEnd = row.UnmergeWindowEnd.Value,
                },
                BusinessKeys.Empty.With("partyId", row.SurvivorPartyId.Value.ToString()).With("mergeId", row.MergeId.ToString("D"))));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "A party changed meanwhile; try again.");
        }

        return new PartyMergeApproveResponse { Merge = await queries.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Unmerge: while the window is open, the absorbed party gets back the status it had (D-CON-06) and the merge is closed. No data of either
/// party was moved at merge time, so both are exactly as before, bitemporal history included. Refused when a party is DSAR-restricted. Publishes
/// <c>PartyUnmerged</c>. TODO(hardening): activity recorded on the survivor after the merge (steward assignment of post-merge changes) is not split off.
/// </summary>
internal sealed class UnmergePartyHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, PartyMergeQueries queries)
    : ICommandHandler<UnmergeParty, PartyUnmergeResponse>
{
    public async Task<Result<PartyUnmergeResponse>> HandleAsync(UnmergeParty command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var row = await db.PartyMerges.SingleOrDefaultAsync(m => m.MergeId == request.MergeId && m.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return MergeSupport.NotFound();
        }

        if (row.Status != MergeStatus.Executed)
        {
            return MergeSupport.State($"Only an executed merge can be unmerged; the merge is {row.Status}.");
        }

        var now = clock.Now;
        if (row.UnmergeWindowEnd is not { } windowEnd || now > windowEnd)
        {
            return DomainError.Of(ModuleCode.PTY, "WINDOW-CLOSED", "The unmerge window of this merge has closed.");
        }

        var merged = await db.Parties.SingleAsync(p => p.PartyId == row.MergedPartyId, cancellationToken).ConfigureAwait(false);
        if (merged.Status != Codes.Of(PartyStatus.Merged))
        {
            return MergeSupport.State("The absorbed party is no longer merged.");
        }

        if (await db.DsarRestrictions.AnyAsync(
                r => (r.PartyId == row.MergedPartyId || r.PartyId == row.SurvivorPartyId) && r.RecordClass == PartyDsarWork.PartyRecord, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PTY, "RESTRICTED", "A party of the merge is restricted by a data-subject request; it cannot be unmerged.");
        }

        merged.Status = row.PriorStatus;
        merged.RecordVersion++;
        merged.UpdatedAt = now;
        row.Status = MergeStatus.Unmerged;
        row.UnmergedAt = now;
        row.UnmergedBy = context.Actor.ToString();
        row.UnmergeReason = request.Reason.Trim();
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PartyUnmergedV1.Descriptor),
            "Party",
            row.SurvivorPartyId.Value.ToString(),
            new PartyUnmergedV1
            {
                MergeId = row.MergeId,
                SurvivorPartyId = row.SurvivorPartyId,
                RestoredPartyIds = [row.MergedPartyId],
                ReverseRepointMap = [new RepointMapItem { From = row.SurvivorPartyId.Value.ToString("D"), To = row.MergedPartyId.Value.ToString("D") }],
            },
            BusinessKeys.Empty.With("partyId", row.SurvivorPartyId.Value.ToString()).With("mergeId", row.MergeId.ToString("D"))));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "A party changed meanwhile; try again.");
        }

        return new PartyUnmergeResponse { Merge = await queries.ViewAsync(row, cancellationToken).ConfigureAwait(false), RestoredPartyId = row.MergedPartyId };
    }
}

/// <summary>Audit facts of the merge commands: the merge and the two party ids; no personal value.</summary>
internal sealed class MergeAuditor :
    ICommandAuditor<RequestPartyMerge, PartyMergeResponse>,
    ICommandAuditor<DecidePartyMerge, PartyMergeApproveResponse>,
    ICommandAuditor<UnmergeParty, PartyUnmergeResponse>
{
    public CommandAuditFacts Describe(RequestPartyMerge command, Result<PartyMergeResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Merge : null);

    public CommandAuditFacts Describe(DecidePartyMerge command, Result<PartyMergeApproveResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Merge : null);

    public CommandAuditFacts Describe(UnmergeParty command, Result<PartyUnmergeResponse>? result) => Facts(result is { IsSuccess: true } ok ? ok.Value.Merge : null);

    private static CommandAuditFacts Facts(PartyMergeView? merge) => merge is null
        ? new CommandAuditFacts()
        : new CommandAuditFacts
        {
            ObjectRef = MergeSupport.Subject(merge.MergeId),
            BusinessKeys = BusinessKeys.Empty
                .With("partyId", merge.SurvivorPartyId.Value.ToString())
                .With("mergedPartyId", merge.MergedPartyId.Value.ToString())
                .With("mergeId", merge.MergeId.ToString("D")),
        };
}
