using System.Globalization;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Commands.StatutoryOffers;

/// <summary>Views and shared rules of the statutory offer of a third-party MTPL exposure (BO-CLM-06, REQ-CLM-007, D-REG-04).</summary>
internal static class StatutoryOfferSupport
{
    public const string TemplateKey = "DT-CLM-OFFER";

    // TODO(hardening): the cost type and category of the accepted-offer payment are the illustrative motor list (D-SL2-04); MTPL bodily injury needs its own category.
    public const string PaymentCostType = "INDEMNITY";
    public const string PaymentCostCategory = "VEHICLE_REPAIR";

    public static DomainError Content(string message) => DomainError.Of(ModuleCode.CLM, "OFFER-CONTENT", message);

    public static DomainError Delivery(string message) => DomainError.Of(ModuleCode.CLM, "OFFER-DELIVERY", message);

    public static DomainError State(string message) => DomainError.Of(ModuleCode.CLM, "OFFER-STATE", message);

    public static DomainError ClocksRequired() =>
        DomainError.Of(ModuleCode.CLM, "MTPL-CLOCKS-REQUIRED", "The statutory offer clocks need values that are legally confirmed.");

    public static StatutoryOfferView View(StatutoryOfferRow row) => new()
    {
        OfferId = row.OfferId,
        ClaimId = row.ClaimId,
        ExposureId = row.ExposureId,
        Kind = row.Kind == StatutoryOfferRow.ReasonedOffer ? StatutoryOfferView.KindValue.ReasonedOffer : StatutoryOfferView.KindValue.ReasonedReply,
        Status = row.Status switch
        {
            StatutoryOfferRow.Accepted => StatutoryOfferView.StatusValue.Accepted,
            StatutoryOfferRow.RepairAgreed => StatutoryOfferView.StatusValue.RepairAgreed,
            _ => StatutoryOfferView.StatusValue.Issued,
        },
        Amount = row.Amount is { } amount ? new Money(amount, Currency.EUR) : null,
        IssuedAt = row.IssuedAt,
        DeliveredAt = row.DeliveredAt,
        DeliveryEvidenceId = row.DeliveryEvidenceId,
        DocumentId = new DocumentId(row.DocumentId),
        ClockLegalStatus = row.ClockLegalStatus,
        PaymentClockId = row.PaymentClockId,
        RepairClockId = row.RepairClockId,
        AcceptedAt = row.AcceptedAt,
        RepairAgreedAt = row.RepairAgreedAt,
        PaymentSetId = row.PaymentSetId,
        RecordVersion = row.RecordVersion,
    };

    public static Task<StatutoryOfferRow?> FindAsync(ClaimsDbContext db, LegalEntityId legalEntity, Guid offerId, CancellationToken cancellationToken) =>
        db.StatutoryOffers.SingleOrDefaultAsync(o => o.LegalEntityId == legalEntity && o.OfferId == offerId, cancellationToken);

    /// <summary>Appends a clock instance to the exposure's clock list, so close, reopen and the exposure view see it (BO-CLM-05).</summary>
    public static string WithClock(string statutoryClocks, Guid instanceId) =>
        statutoryClocks.StartsWith(ExposureClocks.Prefix, StringComparison.Ordinal) ? statutoryClocks + "," + instanceId : statutoryClocks;

    public static CommandAuditFacts Facts(StatutoryOfferView view, object? before, object after) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.CLM, "StatutoryOffer", view.OfferId.ToString("D")),
        BusinessKeys = BusinessKeys.Empty.With("offerId", view.OfferId.ToString("D")).With("claimId", view.ClaimId.Value.ToString("D")).With("exposureId", view.ExposureId.Value.ToString("D")),
        Changes = AuditDiff.Compute(before, after),
    };
}

/// <summary><c>clm.StatutoryOffer.issue</c>.</summary>
internal sealed record IssueStatutoryOffer(StatutoryOfferIssueRequest Request) : ICommand<StatutoryOfferIssueResponse>;

internal sealed class IssueStatutoryOfferValidator : AbstractValidator<IssueStatutoryOffer>
{
    public IssueStatutoryOfferValidator()
    {
        RuleFor(c => c.Request.Content).Must(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 4000).WithMessage("The reasoning is required, at most 4000 characters.");
    }
}

/// <summary>
/// Records a reasoned offer or reply at its proven delivery on a third-party MTPL exposure (REQ-CLM-007, D7): the delivery is not before the issue and not in
/// the future; the offer clock stops as met at the delivery; a reasoned offer starts the payment clock from the delivery (10 days, D-REG-04); the letter
/// is rendered from DT-CLM-OFFER; <c>StatutoryOfferIssued</c> is published. One reasoned offer per exposure. Fail closed (D-USR-19): in Production a reasoned
/// offer is refused while the payment-clock value is not Verified or SecondarySource (OQ-008).
/// TODO(hardening): a CMP failure after the CLM commit (or the reverse) is not reconciled; the clock keys make a retry find the same instances.
/// TODO(hardening): the proof of delivery is an opaque evidence id; DOC holds no delivery-evidence lookup yet to validate it.
/// </summary>
internal sealed class IssueStatutoryOfferHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, IHostEnvironment environment, IOptions<ClaimsOptions> options,
    IServiceProvider services, ExposureClocks exposureClocks, ClaimProtection protection)
    : ICommandHandler<IssueStatutoryOffer, StatutoryOfferIssueResponse>
{
    public async Task<Result<StatutoryOfferIssueResponse>> HandleAsync(IssueStatutoryOffer command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var isOffer = request.Kind == StatutoryOfferIssueRequest.KindValue.ReasonedOffer;
        if (isOffer)
        {
            if (request.Amount is not { } amount || amount.Currency != Currency.EUR || !amount.IsPositive || !amount.IsRoundedToMinorUnits)
            {
                return StatutoryOfferSupport.Content("A reasoned offer needs a positive EUR amount rounded to minor units.");
            }
        }
        else if (request.Amount is not null)
        {
            return StatutoryOfferSupport.Content("A reasoned reply carries no amount.");
        }

        var legalEntity = protection.Current(context);
        var exposure = await db.Exposures.SingleOrDefaultAsync(e => e.ExposureId == request.ExposureId && e.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        var claim = exposure is null ? null : await ClaimSupport.LoadAsync(db, legalEntity, exposure.ClaimId, cancellationToken).ConfigureAwait(false);
        if (exposure is null || claim is null)
        {
            return ClaimSupport.NotFound("exposure");
        }

        if (claim.Status != ClaimStates.Open || exposure.Status != ClaimStates.Open)
        {
            return StatutoryOfferSupport.State("The claim and the exposure must be open.");
        }

        if (!exposure.StatutoryClocks.StartsWith(ExposureClocks.Prefix, StringComparison.Ordinal))
        {
            return StatutoryOfferSupport.State("Only a third-party MTPL exposure with statutory clocks has a statutory offer.");
        }

        var now = clock.Now;
        var issuedAt = request.IssuedAt ?? request.DeliveredAt;
        if (issuedAt > now || request.DeliveredAt > now)
        {
            return StatutoryOfferSupport.Delivery("The issue and the delivery cannot be in the future.");
        }

        if (request.DeliveredAt < issuedAt)
        {
            return StatutoryOfferSupport.Delivery("The proven delivery cannot be earlier than the issue.");
        }

        if (isOffer && await db.StatutoryOffers.AnyAsync(o => o.ExposureId == exposure.ExposureId && o.Kind == StatutoryOfferRow.ReasonedOffer, cancellationToken).ConfigureAwait(false))
        {
            return StatutoryOfferSupport.State("This exposure has a reasoned offer already.");
        }

        var paymentStatus = await exposureClocks.LegalStatusAsync(ExposureClocks.PaymentCode, cancellationToken).ConfigureAwait(false);
        if (isOffer && environment.IsProduction() && !ExposureClocks.IsConfirmed(paymentStatus))
        {
            return StatutoryOfferSupport.ClocksRequired();
        }

        var offerId = Guid.CreateVersion7();
        var documentId = Guid.CreateVersion7();
        if (!context.DryRun)
        {
            var claimant = await db.Claimants.AsNoTracking().SingleAsync(c => c.ClaimantId == exposure.ClaimantId, cancellationToken).ConfigureAwait(false);
            if (services.GetService<IDocumentRequestService>() is not { } documents)
            {
                return StatutoryOfferSupport.State("Document rendering is not available.");
            }

            var outcome = await documents.RequestAsync(
                new DocumentRequestSpec(
                    StatutoryOfferSupport.TemplateKey,
                    offerId.ToString("D"),
                    new ObjectRef(ModuleCode.CLM, "StatutoryOffer", offerId.ToString("D")),
                    claimant.PartyId,
                    request.Language == StatutoryOfferIssueRequest.LanguageValue.En ? "en" : "el",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["claimNumber"] = claim.ClaimNumber.Value,
                        ["exposureNumber"] = exposure.ExposureNumber.Value,
                        ["kind"] = isOffer ? "REASONED_OFFER" : "REASONED_REPLY",
                        ["amount"] = request.Amount is { } a ? a.Amount.ToString("F2", CultureInfo.InvariantCulture) + " " + a.Currency.Code : "-",
                        ["issuedOn"] = options.Value.DateOf(issuedAt).ToString(),
                        ["content"] = request.Content,
                    }),
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(outcome.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return StatutoryOfferSupport.State("The offer letter could not be rendered; nothing was recorded.");
            }

            documentId = outcome.DocumentId.Value;
        }

        // The offer clock is met at the proven delivery; a reasoned offer starts the payment clock from it (D7).
        Guid? paymentClock = null;
        if (!context.DryRun)
        {
            await exposureClocks.StopLiveAsync(exposure.StatutoryClocks, ExposureClocks.OfferCode, request.DeliveredAt, cancellationToken).ConfigureAwait(false);
            if (isOffer)
            {
                paymentClock = await exposureClocks.StartOneAsync(
                    ExposureClocks.PaymentCode, exposure.ExposureId, request.DeliveredAt, "offer", "Reasoned offer delivered", cancellationToken).ConfigureAwait(false);
                exposure.StatutoryClocks = StatutoryOfferSupport.WithClock(exposure.StatutoryClocks, paymentClock.Value);
            }
        }

        var row = new StatutoryOfferRow
        {
            OfferId = offerId,
            LegalEntityId = legalEntity,
            Jurisdiction = claim.Jurisdiction,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            ClaimId = claim.ClaimId,
            ExposureId = exposure.ExposureId,
            Kind = isOffer ? StatutoryOfferRow.ReasonedOffer : StatutoryOfferRow.ReasonedReply,
            Status = StatutoryOfferRow.Issued,
            Amount = request.Amount?.Amount,
            Currency = request.Amount?.Currency.Code,
            IssuedAt = issuedAt,
            DeliveredAt = request.DeliveredAt,
            DeliveryEvidenceId = request.DeliveryEvidenceId,
            DocumentId = documentId,
            ClockLegalStatus = paymentStatus,
            PaymentClockId = paymentClock,
            UpdatedAt = now,
        };
        db.StatutoryOffers.Add(row);
        exposure.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(StatutoryOfferIssuedV1.Descriptor), ClaimEvents.AggregateType, claim.ClaimId.Value.ToString(),
            new StatutoryOfferIssuedV1
            {
                ExposureId = exposure.ExposureId,
                Kind = isOffer ? StatutoryOfferIssuedV1.KindValue.ReasonedOffer : StatutoryOfferIssuedV1.KindValue.ReasonedReply,
                DeliveredAt = request.DeliveredAt,
                DocumentId = new DocumentId(documentId),
                DeliveryEvidenceId = request.DeliveryEvidenceId,
            },
            BusinessKeys.Empty
                .With("claimId", claim.ClaimId.Value.ToString())
                .With("exposureId", exposure.ExposureId.Value.ToString())
                .With("documentId", documentId.ToString("D"))
                .With("deliveryEvidenceId", request.DeliveryEvidenceId.ToString("D")))
        {
            OccurredAt = now,
        });
        return new StatutoryOfferIssueResponse { Offer = StatutoryOfferSupport.View(row) };
    }
}

internal sealed class IssueStatutoryOfferAuditor : ICommandAuditor<IssueStatutoryOffer, StatutoryOfferIssueResponse>
{
    public CommandAuditFacts Describe(IssueStatutoryOffer command, Result<StatutoryOfferIssueResponse>? result) =>
        result is { IsSuccess: true } ok
            ? StatutoryOfferSupport.Facts(
                ok.Value.Offer, null,
                new { kind = ok.Value.Offer.Kind.ToString(), amount = ok.Value.Offer.Amount?.ToString(), deliveredAt = ok.Value.Offer.DeliveredAt.ToString(), clockLegalStatus = ok.Value.Offer.ClockLegalStatus })
            : new CommandAuditFacts();
}

/// <summary><c>clm.StatutoryOffer.recordAcceptance</c>.</summary>
internal sealed record RecordOfferAcceptance(StatutoryOfferRecordAcceptanceRequest Request) : ICommand<StatutoryOfferRecordAcceptanceResponse>;

/// <summary>
/// The claimant accepted a reasoned offer: releases exactly one Draft payment set (the offered amount, FINAL, to the payee account captured on the claim)
/// through the normal set build, so every payment rule applies (cover, payee account, duplicates). A repeat returns the accepted offer and its set.
/// The payment set is only a Draft: submit and approval follow the normal authority path.
/// TODO(hardening): the payment clock is not stopped here; it should stop when the disbursement is confirmed (BIL) and is only breached or cancelled today.
/// </summary>
internal sealed class RecordOfferAcceptanceHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse> build)
    : ICommandHandler<RecordOfferAcceptance, StatutoryOfferRecordAcceptanceResponse>
{
    public async Task<Result<StatutoryOfferRecordAcceptanceResponse>> HandleAsync(RecordOfferAcceptance command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var row = await StatutoryOfferSupport.FindAsync(db, protection.Current(context), request.OfferId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ClaimSupport.NotFound("statutory offer");
        }

        if (row.Status == StatutoryOfferRow.Accepted)
        {
            return new StatutoryOfferRecordAcceptanceResponse { Offer = StatutoryOfferSupport.View(row) };
        }

        if (row.Kind != StatutoryOfferRow.ReasonedOffer || row.Status != StatutoryOfferRow.Issued || row.Amount is not { } offered)
        {
            return StatutoryOfferSupport.State("Only an issued reasoned offer can be accepted.");
        }

        var now = clock.Now;
        var acceptedAt = request.AcceptedAt ?? now;
        if (acceptedAt > now || acceptedAt < row.DeliveredAt)
        {
            return StatutoryOfferSupport.Delivery("The acceptance cannot be before the delivery or in the future.");
        }

        var built = await build.HandleAsync(new BuildTransactionSet(new TransactionSetBuildRequest
        {
            ClaimId = row.ClaimId,
            Transactions =
            [
                new Item
                {
                    Kind = Item.KindValue.Payment, ExposureId = row.ExposureId, CostType = StatutoryOfferSupport.PaymentCostType,
                    CostCategory = StatutoryOfferSupport.PaymentCostCategory, Amount = new Money(offered, Currency.EUR),
                    PayeePartyId = request.PayeePartyId, PayeeAccountId = request.PayeeAccountId, PaymentType = Item.PaymentTypeValue.Final,
                },
            ],
        }), cancellationToken).ConfigureAwait(false);
        if (built.IsFailure)
        {
            return built.Error!;
        }

        row.Status = StatutoryOfferRow.Accepted;
        row.AcceptedAt = acceptedAt;
        row.PaymentSetId = built.Value.SetId;
        row.UpdatedAt = now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        return new StatutoryOfferRecordAcceptanceResponse { Offer = StatutoryOfferSupport.View(row) };
    }
}

internal sealed class RecordOfferAcceptanceAuditor : ICommandAuditor<RecordOfferAcceptance, StatutoryOfferRecordAcceptanceResponse>
{
    public CommandAuditFacts Describe(RecordOfferAcceptance command, Result<StatutoryOfferRecordAcceptanceResponse>? result) =>
        result is { IsSuccess: true } ok
            ? StatutoryOfferSupport.Facts(ok.Value.Offer, new { status = "ISSUED" }, new { status = "ACCEPTED", paymentSetId = ok.Value.Offer.PaymentSetId })
            : new CommandAuditFacts();
}

/// <summary><c>clm.StatutoryOffer.recordRepairAgreement</c>.</summary>
internal sealed record RecordRepairAgreement(StatutoryOfferRecordRepairAgreementRequest Request) : ICommand<StatutoryOfferRecordRepairAgreementResponse>;

/// <summary>
/// Repair in kind replaces the payment (D7): the payment clock is cancelled and the repair clock (20 days, UNVERIFIED, OQ-008) starts from the agreement.
/// In Production this is refused while the repair-clock value is not confirmed. A repeat returns the agreed offer.
/// </summary>
internal sealed class RecordRepairAgreementHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, IHostEnvironment environment, ExposureClocks exposureClocks, ClaimProtection protection)
    : ICommandHandler<RecordRepairAgreement, StatutoryOfferRecordRepairAgreementResponse>
{
    public async Task<Result<StatutoryOfferRecordRepairAgreementResponse>> HandleAsync(RecordRepairAgreement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var row = await StatutoryOfferSupport.FindAsync(db, legalEntity, request.OfferId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ClaimSupport.NotFound("statutory offer");
        }

        if (row.Status == StatutoryOfferRow.RepairAgreed)
        {
            return new StatutoryOfferRecordRepairAgreementResponse { Offer = StatutoryOfferSupport.View(row) };
        }

        if (row.Kind != StatutoryOfferRow.ReasonedOffer || row.Status != StatutoryOfferRow.Issued)
        {
            return StatutoryOfferSupport.State("Repair in kind can only be agreed on an issued reasoned offer that was not accepted for payment.");
        }

        var now = clock.Now;
        var agreedAt = request.AgreedAt ?? now;
        if (agreedAt > now || agreedAt < row.DeliveredAt)
        {
            return StatutoryOfferSupport.Delivery("The agreement cannot be before the delivery or in the future.");
        }

        var repairStatus = await exposureClocks.LegalStatusAsync(ExposureClocks.RepairCode, cancellationToken).ConfigureAwait(false);
        if (environment.IsProduction() && !ExposureClocks.IsConfirmed(repairStatus))
        {
            return StatutoryOfferSupport.ClocksRequired();
        }

        var exposure = await db.Exposures.SingleAsync(e => e.ExposureId == row.ExposureId, cancellationToken).ConfigureAwait(false);
        if (exposure.Status != ClaimStates.Open)
        {
            return StatutoryOfferSupport.State("The exposure is not open.");
        }

        if (!context.DryRun)
        {
            if (row.PaymentClockId is { } paymentClock)
            {
                await exposureClocks.CancelOneAsync(paymentClock, "Repair in kind agreed instead of payment", cancellationToken).ConfigureAwait(false);
            }

            row.RepairClockId = await exposureClocks.StartOneAsync(
                ExposureClocks.RepairCode, row.ExposureId, agreedAt, "repair", "Repair in kind agreed", cancellationToken).ConfigureAwait(false);
            exposure.StatutoryClocks = StatutoryOfferSupport.WithClock(exposure.StatutoryClocks, row.RepairClockId.Value);
        }
        else
        {
            row.RepairClockId = Guid.Empty;
        }

        row.Status = StatutoryOfferRow.RepairAgreed;
        row.RepairAgreedAt = agreedAt;
        row.UpdatedAt = now;
        row.RecordVersion++;
        exposure.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        return new StatutoryOfferRecordRepairAgreementResponse { Offer = StatutoryOfferSupport.View(row) };
    }
}

internal sealed class RecordRepairAgreementAuditor : ICommandAuditor<RecordRepairAgreement, StatutoryOfferRecordRepairAgreementResponse>
{
    public CommandAuditFacts Describe(RecordRepairAgreement command, Result<StatutoryOfferRecordRepairAgreementResponse>? result) =>
        result is { IsSuccess: true } ok
            ? StatutoryOfferSupport.Facts(ok.Value.Offer, new { status = "ISSUED" }, new { status = "REPAIR_AGREED", repairClockId = ok.Value.Offer.RepairClockId })
            : new CommandAuditFacts();
}
