using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>The statement lines of an FS_CLEARING disbursement as stored (jsonb): claim, FS case and the amount as a fixed-point string.</summary>
internal static class FsLines
{
    private sealed record Stored(Guid ClaimId, Guid FsCaseId, string Amount);

    public static string Serialize(IReadOnlyList<FsClearingLine> lines) =>
        JsonSerializer.Serialize(lines.Select(l => new Stored(l.ClaimId.Value, l.FsCaseId.Value, l.Amount.Amount.ToString("F2", CultureInfo.InvariantCulture))));

    public static IReadOnlyList<FsClearingLine>? Deserialize(string? json) =>
        json is null
            ? null
            : [.. (JsonSerializer.Deserialize<List<Stored>>(json) ?? []).Select(s => new FsClearingLine
            {
                ClaimId = new ClaimId(s.ClaimId),
                FsCaseId = new FsCaseId(s.FsCaseId),
                Amount = new Money(decimal.Parse(s.Amount, CultureInfo.InvariantCulture), Currency.EUR),
            })];
}

/// <summary><c>bil.Disbursement.approveRelease</c> (D-SL4-12, REQ-BIL-206, -357): the maker/checker release of an FS_CLEARING net.</summary>
internal sealed record ApproveDisbursementRelease(DisbursementApproveReleaseRequest Request) : ICommand<DisbursementApproveReleaseResponse>;

internal sealed class ApproveDisbursementReleaseValidator : AbstractValidator<ApproveDisbursementRelease>
{
    public ApproveDisbursementReleaseValidator()
    {
        RuleFor(c => c.Request.Reason).MaximumLength(500);
        RuleFor(c => c.Request.Reason).NotEmpty().When(c => c.Request.Decision == DisbursementApproveReleaseRequest.DecisionValue.Reject).WithErrorCode("REASON_REQUIRED");
    }
}

/// <summary>
/// Decides the release of a PendingApproval FS_CLEARING disbursement. The caller needs the <c>bil.Disbursement.approveRelease</c>
/// permission (Staff.BillingManager) and must not be the requester (BIL-ERR-SOD, 403). APPROVE moves it to Approved and, with a
/// bank channel bound, releases it (FS_CLEARING_RELEASED: LA-24 to LA-13), issues it and clears it on the statement debit
/// (FS_CLEARING_CLEARED: LA-13 to LA-10). REJECT moves it to Rejected and publishes <c>DisbursementRejected</c> (party APPROVER).
/// A disbursement that is not awaiting a release approval is BIL-ERR-APPROVAL-REQUIRED.
/// </summary>
internal sealed class ApproveDisbursementReleaseHandler(
    BillingDbContext db,
    RequestContext context,
    IClock clock,
    LedgerWriter ledger,
    DisbursementReleaser releaser,
    IEventPublisher events,
    IServiceProvider services) : ICommandHandler<ApproveDisbursementRelease, DisbursementApproveReleaseResponse>
{
    public async Task<Result<DisbursementApproveReleaseResponse>> HandleAsync(ApproveDisbursementRelease command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        var row = await db.Disbursements.SingleOrDefaultAsync(d => d.DisbursementId == request.DisbursementId && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("disbursement");
        }

        if (row.SourceType != DisbursementCodes.FsClearing || row.State != Codes.Of(DisbursementState.PendingApproval))
        {
            return DomainError.Of(ModuleCode.BIL, "APPROVAL-REQUIRED", "The disbursement is not awaiting a release approval.");
        }

        var actor = context.Actor.ToString();
        if (string.Equals(actor, row.CreatedBy, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.BIL, "SOD", "The requester cannot approve the release of their own disbursement (REQ-BIL-357).");
        }

        var account = await db.PayeeAccounts.AsNoTracking().SingleAsync(a => a.PayeeAccountId == row.PayeeAccountId, cancellationToken).ConfigureAwait(false);
        var amount = new Money(row.Amount, Currency.FromCode(row.Currency));
        var machine = DisbursementStateModel.Machine;
        var now = clock.Now;
        row.ReleaseApprovedBy = actor;
        row.RecordVersion++;

        if (request.Decision == DisbursementApproveReleaseRequest.DecisionValue.Reject)
        {
            row.State = Codes.Of(machine.FireOrThrow(DisbursementState.PendingApproval, DisbursementTrigger.Reject));
            var keys = BusinessKeys.Empty.With("disbursementId", row.DisbursementId.Value.ToString());
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(DisbursementRejectedV1.Descriptor), "Disbursement", row.DisbursementId.Value.ToString(),
                new DisbursementRejectedV1
                {
                    DisbursementId = row.DisbursementId,
                    SourceType = row.SourceType,
                    SourceId = row.SourceId,
                    ReasonCode = request.Reason!,
                    RejectingParty = DisbursementRejectedV1.RejectingPartyValue.Approver,
                    StatementRef = row.StatementRef,
                    Method = row.Method,
                },
                keys) { OccurredAt = now });
        }
        else
        {
            row.State = Codes.Of(machine.FireOrThrow(DisbursementState.PendingApproval, DisbursementTrigger.Approve));
            row.ApprovedAt = now;
            if (services.GetService<IBankChannel>() is { } channel)
            {
                await releaser.ReleaseAsync(row, channel, amount, ledger.Today, now, null, cancellationToken).ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisbursementApproveReleaseResponse
        {
            Disbursement = DisbursementReader.View(row, account.IbanLast4),
            Decision = request.Decision == DisbursementApproveReleaseRequest.DecisionValue.Approve
                ? DisbursementApproveReleaseResponse.DecisionValue.Approve
                : DisbursementApproveReleaseResponse.DecisionValue.Reject,
        };
    }
}

internal sealed class ApproveDisbursementReleaseAuditor : ICommandAuditor<ApproveDisbursementRelease, DisbursementApproveReleaseResponse>
{
    public CommandAuditFacts Describe(ApproveDisbursementRelease command, Result<DisbursementApproveReleaseResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.BIL, "Disbursement", command.Request.DisbursementId),
        BusinessKeys = BusinessKeys.Empty.With("disbursementId", command.Request.DisbursementId.Value.ToString()),
        Changes = AuditDiff.Compute(null, new
        {
            decision = command.Request.Decision.ToString(),
            status = result is { IsSuccess: true } ok ? ok.Value.Disbursement.Status.ToString() : null,
        }),
    };
}
