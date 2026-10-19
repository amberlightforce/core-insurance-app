using CoreIns.Modules.Policy.Commands.Cancellation;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands.Withdrawal;

/// <summary><c>pol.Withdrawal.submit</c>: the statutory right of withdrawal or objection voids the term ab initio (REQ-POL-307, -308, -314).</summary>
internal sealed record SubmitWithdrawal(WithdrawalSubmitRequest Request) : ICommand<WithdrawalSubmitResponse>;

internal sealed class SubmitWithdrawalValidator : AbstractValidator<SubmitWithdrawal>
{
    public SubmitWithdrawalValidator()
    {
        RuleFor(c => c.Request.PolicyId.Value).NotEmpty().WithErrorCode("POLICY_REQUIRED");
        RuleFor(c => c.Request.Right).IsInEnum().WithErrorCode("RIGHT_UNKNOWN");
        RuleFor(c => c.Request.ChannelRef).NotEmpty().MaximumLength(128).WithErrorCode("CHANNEL_REF_REQUIRED");
    }
}

/// <summary>
/// Voids the policy's first term ab initio with a full refund, exactly once per (policy, channelRef).
/// <list type="number">
/// <item>A stored result for (policy, channelRef) is returned as it was, with <c>replayed = true</c>; nothing is written.</item>
/// <item>The right window is checked from <c>receivedAt</c> (never from now): the bind date plus <c>WithdrawalDays</c> calendar days,
/// the last day ending 23:59:59 Europe/Athens (D-SL6-06, ILLUSTRATIVE 14 days). Later declarations are refused with
/// <c>POL-ERR-RIGHT-EXPIRED</c> (422).</item>
/// <item>The cancellation machinery does the rest (<see cref="CancelPolicyHandler"/>, source DistanceWithdrawal): effective = cover
/// start, full refund of premium plus IPT and levies (REVERSE_AS_VOID, F-401 / D-SL6-02), cost-of-cover deduction 0.00,
/// transaction Void, term Voided, <c>ChargeDeltaEmitted</c> credits (BIL turns them into the refund), <c>PolicyCancelled</c> (BIL
/// stops billing) and <c>PolicyVoided</c>.</item>
/// <item>The first response is stored on the unique (policy, channelRef) row in the same transaction.</item>
/// </list>
/// </summary>
internal sealed class SubmitWithdrawalHandler(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    CancelPolicyHandler cancel,
    IOptions<PolicyOptions> options) : ICommandHandler<SubmitWithdrawal, WithdrawalSubmitResponse>
{
    public async Task<Result<WithdrawalSubmitResponse>> HandleAsync(SubmitWithdrawal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var channelRef = request.ChannelRef.Trim();
        var right = request.Right == WithdrawalRight.Objection ? "OBJECTION" : "WITHDRAWAL";

        var existing = await db.Withdrawals.AsNoTracking()
            .SingleOrDefaultAsync(w => w.PolicyId == request.PolicyId && w.ChannelRef == channelRef && w.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return JobSupport.FromJson<WithdrawalSubmitResponse>(existing.Result) with { Replayed = true };
        }

        if (!await db.Policies.AsNoTracking().AnyAsync(p => p.PolicyId == request.PolicyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false))
        {
            return JobSupport.NotFound("policy");
        }

        // The right belongs to the contract as concluded: the first term of the policy (its current version).
        var term = await db.Terms.AsNoTracking()
            .Where(x => x.PolicyId == request.PolicyId && x.LegalEntityId == legalEntity && x.RecordedTo == null)
            .OrderBy(x => x.TermNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        var zone = options.Value.Zone;
        var lastDay = new BusinessDate(term.WrittenDate.Value.AddDays(options.Value.WithdrawalDays));
        var deadline = PolicyTime.EndOf(lastDay, zone);
        if (request.ReceivedAt > deadline)
        {
            return DomainError.Of(
                ModuleCode.POL, "RIGHT-EXPIRED",
                $"The right of {right.ToLowerInvariant()} could be exercised until {lastDay} (end of day); the declaration was received {request.ReceivedAt}.");
        }

        var cancelRequest = new CancellationCreateRequest
        {
            PolicyId = request.PolicyId,
            Source = CancellationSources.DistanceWithdrawal,
            ReasonCode = "DISTANCE_WITHDRAWAL",
            EffectiveAt = term.ValidFrom,
            Kind = Contracts.Api.CancellationKind.Standard,
            RequestRef = channelRef,
        };
        var cancelled = await cancel.HandleAsync(new CancelPolicy(cancelRequest, new WithdrawalCase(term.TermId, right, channelRef, request.ReceivedAt)), cancellationToken)
            .ConfigureAwait(false);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        var done = cancelled.Value;
        var preview = done.ServicingPreview;
        var transactionId = await db.Jobs.AsNoTracking().Where(j => j.JobId == done.JobId).Select(j => j.BoundTransactionId).SingleAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The withdrawal job has no bound transaction.");
        var response = new WithdrawalSubmitResponse
        {
            VoidJob = new WithdrawalVoidJob
            {
                JobId = done.JobId, State = done.State, TermId = term.TermId, TransactionId = transactionId, EffectiveAt = done.EffectiveAt,
                Right = request.Right, RefundDue = preview.RefundDue, CostOfCoverDeduction = new Money(0.00m, preview.RefundDue.Currency),
            },
            RefundPreview = preview,
            Replayed = false,
        };

        db.Withdrawals.Add(new WithdrawalRow
        {
            WithdrawalId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PolicyId = request.PolicyId, TermId = term.TermId, JobId = done.JobId,
            ChannelRef = channelRef, Right = right, ReceivedAt = request.ReceivedAt, RecordedAt = clock.Now, Actor = context.Actor.ToString(),
            Result = JobSupport.Json(response),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // TODO(hardening): a concurrent submit of the same channelRef lost the race after the policy lock; the caller retries and gets the first result.
            return JobSupport.Stale();
        }

        return response;
    }
}

/// <summary>Audit facts of <c>pol.Withdrawal.submit</c>: policy, job and the right (no personal data).</summary>
internal sealed class SubmitWithdrawalAuditor : ICommandAuditor<SubmitWithdrawal, WithdrawalSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitWithdrawal command, Result<WithdrawalSubmitResponse>? result)
    {
        var objectRef = ObjectRef.For(ModuleCode.POL, "Policy", command.Request.PolicyId);
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts { ObjectRef = objectRef };
        }

        var r = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = objectRef,
            BusinessKeys = BusinessKeys.Empty.With("policyId", command.Request.PolicyId.Value.ToString()).With("jobId", r.VoidJob.JobId.Value.ToString()),
            Changes = AuditDiff.Compute(null, new { right = command.Request.Right.ToString(), channelRef = command.Request.ChannelRef, replayed = r.Replayed }),
        };
    }
}
