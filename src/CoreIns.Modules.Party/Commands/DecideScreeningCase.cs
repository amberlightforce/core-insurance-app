using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Party.Services.Screening;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Commands;

/// <summary>
/// <c>pty.Screening.caseDecide</c> (BO-PTY-07): maker-checker over a screening case. PROPOSE records Clear or TrueMatch with a rationale and opens a
/// PLT approval; a different person APPROVEs (the case becomes CLEARED or TRUE_MATCH) or REJECTs (the case is OPEN again).
/// </summary>
internal sealed record DecideScreeningCase(ScreeningCaseDecideRequest Request) : ICommand<ScreeningCaseDecideResponse>;

internal sealed class DecideScreeningCaseValidator : AbstractValidator<DecideScreeningCase>
{
    public DecideScreeningCaseValidator()
    {
        RuleFor(c => c.Request.CaseId).NotEmpty();
        RuleFor(c => c.Request.Comment).MaximumLength(500);
        RuleFor(c => c.Request.Decision).NotNull().When(c => c.Request.Action == ScreeningCaseDecideRequest.ActionValue.Propose)
            .WithMessage("A proposal needs a decision (CLEAR or TRUE_MATCH).");
        RuleFor(c => c.Request.Comment).NotEmpty().When(c => c.Request.Action is ScreeningCaseDecideRequest.ActionValue.Propose or ScreeningCaseDecideRequest.ActionValue.Reject)
            .WithMessage("A proposal and a rejection need a comment.");
    }
}

internal sealed class DecideScreeningCaseHandler(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, IPlatformApprovalService approvals)
    : ICommandHandler<DecideScreeningCase, ScreeningCaseDecideResponse>
{
    public static ObjectRef Subject(Guid caseId) => new(ModuleCode.PTY, "ScreeningCase", caseId.ToString("D"));

    /// <summary>What the checker approves: the case and the decision proposed for it (the client never supplies it again).</summary>
    public static Sha256Hash HashOf(Guid caseId, string decision) =>
        Sha256Hash.ComputeUtf8(string.Join('\n', PartyAuthorityTypes.ScreeningDecisionApprovalType, caseId.ToString("D"), decision));

    private static DomainError State(string detail) => DomainError.Of(ModuleCode.PTY, "STATE", detail);

    public async Task<Result<ScreeningCaseDecideResponse>> HandleAsync(DecideScreeningCase command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var row = await db.ScreeningCases.SingleOrDefaultAsync(c => c.CaseId == request.CaseId && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The screening case does not exist.");
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        if (request.Action == ScreeningCaseDecideRequest.ActionValue.Propose)
        {
            if (row.Status != ScreeningStatus.Open)
            {
                return State($"Only an open case can get a proposal; the case is {row.Status}.");
            }

            var decision = request.Decision == ScreeningCaseDecideRequest.DecisionValue.Clear ? "CLEAR" : "TRUE_MATCH";
            var hash = HashOf(row.CaseId, decision);
            var approval = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = PartyAuthorityTypes.ScreeningDecisionApprovalType,
                    ObjectRef = Subject(row.CaseId),
                    PayloadHash = hash,
                    Authority = new ApprovalAuthority { Type = PartyAuthorityTypes.ScreeningDecision.Value },
                    ReferralRole = "Staff.ComplianceOfficer",
                    Reason = request.Comment,
                },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            row.Status = ScreeningStatus.PendingApproval;
            row.ProposedDecision = decision;
            row.ProposedBy = actor;
            row.ProposedAt = now;
            row.ProposalComment = request.Comment!.Trim();
            row.ApprovalRequestId = approval.Request.RequestId;
            row.ContentHash = hash.Value;
            return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
        }

        if (row.Status != ScreeningStatus.PendingApproval || row.ApprovalRequestId is not { } requestId || row.ProposedDecision is not { } proposed)
        {
            return State($"Only a case with a proposal waiting for approval can be approved or rejected; the case is {row.Status}.");
        }

        var expected = HashOf(row.CaseId, proposed);
        if (expected.Value != row.ContentHash)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "The stored proposal no longer matches its content hash.");
        }

        var reject = request.Action == ScreeningCaseDecideRequest.ActionValue.Reject;
        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId, PayloadHash = expected, Comment = request.Comment,
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            if (!reject)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest
                    {
                        RequestId = requestId, Hash = expected, Type = PartyAuthorityTypes.ScreeningDecisionApprovalType, ObjectRef = Subject(row.CaseId),
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != PartyAuthorityTypes.ScreeningDecision.Value)
                {
                    return State($"PLT refused execution: the request is {verified.Status}.");
                }
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        row.DecidedBy = actor;
        row.DecidedAt = now;
        row.DecisionComment = request.Comment?.Trim();
        if (reject)
        {
            // Back to the queue: the proposal is closed, a new one can be made.
            row.Status = ScreeningStatus.Open;
            row.ProposedDecision = null;
            row.ProposedBy = null;
            row.ProposedAt = null;
            row.ProposalComment = null;
            row.ApprovalRequestId = null;
            row.ContentHash = null;
        }
        else
        {
            row.Status = proposed == "CLEAR" ? ScreeningStatus.Cleared : ScreeningStatus.TrueMatch;
        }

        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ScreeningCaseDecideResponse>> SaveAsync(ScreeningCaseRow row, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ScreeningCaseDecideResponse { Case = ScreeningCaseQueries.View(row) };
    }
}

/// <summary>Audit facts of the decision: the case, its number and the party; no personal value.</summary>
internal sealed class DecideScreeningCaseAuditor : ICommandAuditor<DecideScreeningCase, ScreeningCaseDecideResponse>
{
    public CommandAuditFacts Describe(DecideScreeningCase command, Result<ScreeningCaseDecideResponse>? result)
    {
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = command.Request is null ? null : DecideScreeningCaseHandler.Subject(command.Request.CaseId) };
        }

        var view = ok.Value.Case;
        var keys = BusinessKeys.Empty.With("caseNumber", view.CaseNumber);
        return new CommandAuditFacts
        {
            ObjectRef = DecideScreeningCaseHandler.Subject(view.CaseId),
            BusinessKeys = view.PartyId is { } party ? keys.With("partyId", party.Value.ToString()) : keys,
        };
    }
}
