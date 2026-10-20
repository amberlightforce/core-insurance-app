using System.Security.Cryptography;
using System.Text;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Contracts.Events;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Channels.Commands;

/// <summary>Shared mapping and event helpers of the withdrawal commands.</summary>
internal static class WithdrawalSupport
{
    public const string WebChannel = "WEB_DIRECT";
    public const string StaffChannel = "STAFF";

    public static WithdrawalStatusCode ToStatus(string code) => code switch
    {
        WithdrawalStatuses.Declared => WithdrawalStatusCode.Declared,
        WithdrawalStatuses.Confirmed => WithdrawalStatusCode.Confirmed,
        WithdrawalStatuses.Submitted => WithdrawalStatusCode.Submitted,
        WithdrawalStatuses.Processed => WithdrawalStatusCode.Processed,
        WithdrawalStatuses.Rejected => WithdrawalStatusCode.Rejected,
        _ => WithdrawalStatusCode.Declined,
    };

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenMatches(string expectedHash, string token) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(HashToken(token)));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static BusinessKeys Keys(WithdrawalRequestRow row) =>
        BusinessKeys.Empty.With("withdrawalRequestId", row.WithdrawalRequestId.ToString()).With("policyId", row.PolicyId.Value.ToString());

    public static void PublishReceived(IEventPublisher events, WithdrawalRequestRow row, Instant confirmedAt) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(WithdrawalRequestReceivedV1.Descriptor), "WithdrawalRequest", row.WithdrawalRequestId.ToString(),
            new WithdrawalRequestReceivedV1
            {
                WithdrawalRequestId = row.WithdrawalRequestId,
                RequestNumber = row.RequestNumber,
                ConfirmedAt = confirmedAt,
                MatchedPolicyId = row.PolicyId,
                Channel = row.Channel,
            },
            Keys(row)) { OccurredAt = confirmedAt });

    public static void PublishClosed(IEventPublisher events, WithdrawalRequestRow row, WithdrawalRequestClosedV1.OutcomeValue outcome, Instant at) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(WithdrawalRequestClosedV1.Descriptor), "WithdrawalRequest", row.WithdrawalRequestId.ToString(),
            new WithdrawalRequestClosedV1 { WithdrawalRequestId = row.WithdrawalRequestId, Outcome = outcome, PolicyId = row.PolicyId },
            Keys(row)) { OccurredAt = at });

    public static async Task<WithdrawalRequestRow?> FindAsync(ChannelsDbContext db, LegalEntityId legalEntity, string idOrNumber, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrNumber, out var id))
        {
            return await db.WithdrawalRequests.SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.WithdrawalRequestId == id, cancellationToken).ConfigureAwait(false);
        }

        return await db.WithdrawalRequests.SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.RequestNumber == idOrNumber, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The acknowledgement letter (DT-WITHDRAWAL-ACK) of the received declaration. DOC de-duplicates on (template, request number),
    /// so the letter is rendered once however often it is requested. No DOC binding means no letter. A failing DOC call never fails
    /// the (already committed) confirmation: the sweeper requests the letter again while the request is still CONFIRMED.
    /// TODO(hardening): a SUBMITTED request whose letter failed is not retried; needs an ack marker column on the request.
    /// </summary>
    public static async Task AcknowledgeAsync(
        IServiceProvider services, IWithdrawalPolicyPort policies, ChannelsOptions settings, WithdrawalRequestRow row, Instant now, CancellationToken cancellationToken)
    {
        var documents = services.GetService<IDocumentRequestService>();
        var facts = documents is null ? null : await policies.FindAsync(row.PolicyId, cancellationToken).ConfigureAwait(false);
        if (documents is null || facts is null)
        {
            return;
        }

        try
        {
            await documents.RequestAsync(
                new DocumentRequestSpec(
                    "DT-WITHDRAWAL-ACK",
                    row.RequestNumber,
                    new ObjectRef(ModuleCode.CHN, "WithdrawalRequest", row.WithdrawalRequestId.ToString("D")),
                    facts.PolicyholderPartyId,
                    "el",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["customerName"] = row.DeclarantName,
                        ["policyNo"] = facts.PolicyNumber,
                        ["withdrawalDate"] = now.ToBusinessDate(settings.Zone).ToString(),
                    }),
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The confirmation is committed; the letter is retried by the sweeper.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _ = ex; // TODO(hardening): log through a module logger once the support class has one.
        }
    }

    public static DomainError NotFound() =>
        DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The withdrawal request does not exist in your legal entity.");
}

/// <summary><c>chn.Withdrawal.declare</c>: step 1, records the declaration and issues the confirmation token (REQ-CHN-007).</summary>
internal sealed record DeclareWithdrawal(WithdrawalDeclareRequest Request) : ICommand<WithdrawalDeclareResponse>;

internal sealed class DeclareWithdrawalValidator : AbstractValidator<DeclareWithdrawal>
{
    public DeclareWithdrawalValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.ContractId).NotEmpty();
        RuleFor(c => c.Request.EMail).NotEmpty().MaximumLength(320).Must(e => e.Contains('@', StringComparison.Ordinal) && !e.Any(char.IsWhiteSpace)).WithMessage("The e-mail address is not valid.");
        RuleFor(c => c.Request.Reason).MaximumLength(1000);
    }
}

/// <summary>
/// Declare: the window is checked at once (a late declaration is recorded as DECLINED with CHN-ERR-RIGHT-EXPIRED and returned
/// as a normal response, so the record survives the transaction, D-SL6-06). An open declaration of the same policy is reused with
/// a fresh token, so a policy has one active request.
/// </summary>
internal sealed class DeclareWithdrawalHandler(
    ChannelsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IWithdrawalPolicyPort policies,
    IWithdrawalNotifier notifier,
    IOptions<ChannelsOptions> options) : ICommandHandler<DeclareWithdrawal, WithdrawalDeclareResponse>
{
    public async Task<Result<WithdrawalDeclareResponse>> HandleAsync(DeclareWithdrawal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var settings = options.Value;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var policyId = new PolicyId(request.ContractId);

        var active = await db.WithdrawalRequests
            .Where(r => r.LegalEntityId == legalEntity && r.PolicyId == policyId && r.Status != WithdrawalStatuses.Declined)
            .OrderByDescending(r => r.DeclaredAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        if (active is not null)
        {
            string? token = null;
            if (active.Status == WithdrawalStatuses.Declared)
            {
                token = WithdrawalSupport.NewToken();
                active.TokenHash = WithdrawalSupport.HashToken(token);
                active.TokenExpiresAt = now.Plus(TimeSpan.FromHours(settings.WithdrawalTokenHours));
                active.RecordVersion++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await notifier.SendConfirmationTokenAsync(active.RequestNumber, active.DeclarantEmail, token, active.TokenExpiresAt!.Value, cancellationToken).ConfigureAwait(false);
            }

            return Response(active, token, settings);
        }

        var facts = await policies.FindAsync(policyId, cancellationToken).ConfigureAwait(false);
        if (facts is null)
        {
            return DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The contract does not exist in your legal entity.");
        }

        var zone = settings.Zone;
        var open = WithdrawalWindow.IsOpen(facts.ConcludedAt, now, settings.WithdrawalDays, zone);
        var number = await numbering.NextAsync(new NumberRequest("WITHDRAWAL", now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var token2 = open ? WithdrawalSupport.NewToken() : null;
        var row = new WithdrawalRequestRow
        {
            WithdrawalRequestId = Guid.CreateVersion7(),
            RequestNumber = number.Value,
            LegalEntityId = legalEntity,
            Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
            PolicyId = policyId,
            DeclarantName = request.Name.Trim(),
            DeclarantEmail = request.EMail.Trim(),
            Reason = request.Reason,
            Channel = context.Roles.Any(r => r.StartsWith("Staff.", StringComparison.Ordinal)) ? WithdrawalSupport.StaffChannel : WithdrawalSupport.WebChannel,
            Status = open ? WithdrawalStatuses.Declared : WithdrawalStatuses.Declined,
            DeclaredAt = now,
            DeclaredBy = context.Actor.ToString(),
            ConcludedAt = facts.ConcludedAt,
            WindowDays = settings.WithdrawalDays,
            DeadlineAt = WithdrawalWindow.Deadline(facts.ConcludedAt, settings.WithdrawalDays, zone),
            TokenHash = token2 is null ? null : WithdrawalSupport.HashToken(token2),
            TokenExpiresAt = token2 is null ? null : now.Plus(TimeSpan.FromHours(settings.WithdrawalTokenHours)),
            DeclineCode = open ? null : WithdrawalStatuses.RightExpired,
            ChannelRef = number.Value,
            RecordVersion = 1,
        };
        db.WithdrawalRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (token2 is not null)
        {
            await notifier.SendConfirmationTokenAsync(row.RequestNumber, row.DeclarantEmail, token2, row.TokenExpiresAt!.Value, cancellationToken).ConfigureAwait(false);
        }

        return Response(row, token2, settings);
    }

    private static WithdrawalDeclareResponse Response(WithdrawalRequestRow row, string? token, ChannelsOptions settings) => new()
    {
        RequestNumber = row.RequestNumber,
        Status = WithdrawalSupport.ToStatus(row.Status),
        ConfirmedAt = row.ConfirmedAt,
        ConfirmToken = settings.ReturnWithdrawalToken ? token : null,
        DeclineCode = row.DeclineCode,
        Receipt = new WithdrawalReceipt { RequestNumber = row.RequestNumber, DeclaredAt = row.DeclaredAt, ConfirmBy = row.DeadlineAt },
    };
}

/// <summary>Audit facts of <c>chn.Withdrawal.declare</c>.</summary>
internal sealed class DeclareWithdrawalAuditor : ICommandAuditor<DeclareWithdrawal, WithdrawalDeclareResponse>
{
    public CommandAuditFacts Describe(DeclareWithdrawal command, Result<WithdrawalDeclareResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("policyId", command.Request.ContractId.ToString());
        return result is { IsSuccess: true } ok
            ? new CommandAuditFacts { BusinessKeys = keys.With("requestNumber", ok.Value.RequestNumber), Changes = AuditDiff.Compute(null, new { status = ok.Value.Status.ToString() }) }
            : new CommandAuditFacts { BusinessKeys = keys };
    }
}

/// <summary><c>chn.Withdrawal.confirm</c>: step 2, sets <c>confirmedAt</c> from platform time and hands the request to POL.</summary>
internal sealed record ConfirmWithdrawal(WithdrawalConfirmRequest Request) : ICommand<WithdrawalConfirmResponse>;

internal sealed class ConfirmWithdrawalValidator : AbstractValidator<ConfirmWithdrawal>
{
    public ConfirmWithdrawalValidator()
    {
        RuleFor(c => c.Request.RequestNumber).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Token).NotEmpty().MaximumLength(200);
    }
}

/// <summary>
/// Confirm: idempotent on the request (a second confirmation returns the first result and calls neither POL nor the outbox
/// again). The request row, the WithdrawalRequestReceived event (the backup path, R-89) and the POL handoff share one transaction.
/// </summary>
internal sealed class ConfirmWithdrawalHandler(
    ChannelsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    IWithdrawalPolicyPort policies,
    IServiceProvider services,
    IOptions<ChannelsOptions> options) : ICommandHandler<ConfirmWithdrawal, WithdrawalConfirmResponse>
{
    public async Task<Result<WithdrawalConfirmResponse>> HandleAsync(ConfirmWithdrawal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await db.WithdrawalRequests.SingleOrDefaultAsync(
            r => r.LegalEntityId == legalEntity && r.RequestNumber == request.RequestNumber, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return WithdrawalSupport.NotFound();
        }

        if (row.Status == WithdrawalStatuses.Declined)
        {
            return Response(row);
        }

        if (row.TokenHash is null || !WithdrawalSupport.TokenMatches(row.TokenHash, request.Token))
        {
            return DomainError.Of(ModuleCode.CHN, "TOKEN-INVALID", "The confirmation token is not valid for this request.");
        }

        if (row.Status != WithdrawalStatuses.Declared)
        {
            // Already confirmed (or declined): the same answer again, no second event and no second POL call.
            return Response(row);
        }

        var now = clock.Now;
        var settings = options.Value;
        if (!WithdrawalWindow.IsOpen(row.ConcludedAt, now, row.WindowDays, settings.Zone))
        {
            row.Status = WithdrawalStatuses.Declined;
            row.DeclineCode = WithdrawalStatuses.RightExpired;
            row.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Response(row);
        }

        if (row.TokenExpiresAt is { } expires && now > expires)
        {
            return DomainError.Of(ModuleCode.CHN, "TOKEN-INVALID", "The confirmation token has expired; declare again.");
        }

        row.ConfirmedAt = now;
        row.Status = WithdrawalStatuses.Confirmed;
        row.RecordVersion++;
        WithdrawalSupport.PublishReceived(events, row, now);

        var outcome = await policies.SubmitAsync(row.PolicyId, now, row.ChannelRef, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case PolicySubmitOutcome.Accepted:
                row.Status = WithdrawalStatuses.Submitted;
                row.PolSubmittedAt = now;
                break;
            case PolicySubmitOutcome.Expired:
                // POL disagrees with the window (its own conclusion date): closed as rejected, still recorded.
                row.Status = WithdrawalStatuses.Rejected;
                row.DeclineCode = WithdrawalStatuses.RightExpired;
                row.ClosedAt = now;
                WithdrawalSupport.PublishClosed(events, row, WithdrawalRequestClosedV1.OutcomeValue.Rejected, now);
                break;
            default:
                // No POL binding (or POL unreachable): the row stays CONFIRMED and WithdrawalSweeper re-submits it (idempotent on the channel reference).
                break;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WithdrawalSupport.AcknowledgeAsync(services, policies, settings, row, now, cancellationToken).ConfigureAwait(false);
        return Response(row);
    }

    private static WithdrawalConfirmResponse Response(WithdrawalRequestRow row) => new()
    {
        RequestNumber = row.RequestNumber,
        Status = WithdrawalSupport.ToStatus(row.Status),
        ConfirmedAt = row.ConfirmedAt,
        DeclineCode = row.DeclineCode,
    };
}

/// <summary>Audit facts of <c>chn.Withdrawal.confirm</c>.</summary>
internal sealed class ConfirmWithdrawalAuditor : ICommandAuditor<ConfirmWithdrawal, WithdrawalConfirmResponse>
{
    public CommandAuditFacts Describe(ConfirmWithdrawal command, Result<WithdrawalConfirmResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("requestNumber", command.Request.RequestNumber);
        return result is { IsSuccess: true } ok
            ? new CommandAuditFacts { BusinessKeys = keys, Changes = AuditDiff.Compute(null, new { status = ok.Value.Status.ToString() }) }
            : new CommandAuditFacts { BusinessKeys = keys };
    }
}

/// <summary><c>chn.Withdrawal.close</c>: the POL outcome closes the request and emits WithdrawalRequestClosed.</summary>
internal sealed record CloseWithdrawal(string Id, WithdrawalCloseRequest Request) : ICommand<WithdrawalCloseResponse>;

internal sealed class CloseWithdrawalHandler(
    ChannelsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events) : ICommandHandler<CloseWithdrawal, WithdrawalCloseResponse>
{
    public async Task<Result<WithdrawalCloseResponse>> HandleAsync(CloseWithdrawal command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await WithdrawalSupport.FindAsync(db, legalEntity, command.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return WithdrawalSupport.NotFound();
        }

        var processed = command.Request.Outcome == WithdrawalCloseRequest.OutcomeValue.Processed;
        var target = processed ? WithdrawalStatuses.Processed : WithdrawalStatuses.Rejected;
        if (row.Status == target)
        {
            return Response(row);
        }

        if (row.Status is not (WithdrawalStatuses.Confirmed or WithdrawalStatuses.Submitted))
        {
            return DomainError.Of(ModuleCode.CHN, "WRONG-STATE", $"A request in state {row.Status} cannot be closed.");
        }

        var now = clock.Now;
        row.Status = target;
        row.ClosedAt = now;
        row.RecordVersion++;
        WithdrawalSupport.PublishClosed(events, row, processed ? WithdrawalRequestClosedV1.OutcomeValue.Processed : WithdrawalRequestClosedV1.OutcomeValue.Rejected, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Response(row);
    }

    private static WithdrawalCloseResponse Response(WithdrawalRequestRow row) =>
        new() { RequestNumber = row.RequestNumber, Status = WithdrawalSupport.ToStatus(row.Status) };
}
