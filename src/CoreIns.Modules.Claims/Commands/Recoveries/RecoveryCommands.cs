using System.Text.Json;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Item = CoreIns.Modules.Claims.Contracts.Api.TransactionSetBuildRequest.TransactionItem;

namespace CoreIns.Modules.Claims.Commands.Recoveries;

/// <summary>Errors, codes and state rules shared by the recovery commands (PRD-07 §7.3.4).</summary>
internal static class RecoverySupport
{
    public const string CodePattern = "^[A-Z][A-Z0-9_]{0,63}$";
    public const string WriteOffReason = "WRITE_OFF";

    public static bool IsOpen(string status) => status is not ("CLOSED" or "WRITTEN_OFF");

    public static DomainError State(string detail) => DomainError.Of(ModuleCode.CLM, "RECOVERY-STATE", detail);

    public static DomainError Allocation(string detail) => DomainError.Of(ModuleCode.CLM, "ALLOCATION", detail);

    public static async Task<RecoveryRow?> LoadAsync(ClaimsDbContext db, LegalEntityId legalEntity, RecoveryId id, CancellationToken cancellationToken) =>
        await db.Recoveries.SingleOrDefaultAsync(r => r.RecoveryId == id && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);

    public static async Task<ClaimId?> ClaimOfAsync(ClaimsDbContext db, LegalEntityId legalEntity, RecoveryId id, CancellationToken cancellationToken) =>
        await db.Recoveries.AsNoTracking().Where(r => r.RecoveryId == id && r.LegalEntityId == legalEntity).Select(r => (ClaimId?)r.ClaimId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public static void AddMilestone(RecoveryRow row, string milestone, BusinessDate occurredOn, Instant now, string actor, string? note)
    {
        var list = RecoveryReader.ReadMilestones(row);
        list.Add(new RecoveryMilestoneView { Milestone = milestone, OccurredOn = occurredOn, RecordedAt = now, RecordedBy = actor, Note = note });
        row.Milestones = JsonSerializer.SerializeToDocument(list, SharedKernelJson.Options);
    }

    public static void Touch(RecoveryRow row, Instant now)
    {
        row.UpdatedAt = now;
        row.RecordVersion++;
    }

    public static CommandAuditFacts Facts(RecoveryId id, ClaimId claim, object? changes) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.CLM, "Recovery", id),
        BusinessKeys = BusinessKeys.Empty.With("claimId", claim.Value.ToString()).With("recoveryId", id.Value.ToString("D")),
        Changes = AuditDiff.Compute(null, changes ?? new { }),
    };

    public static bool ValidEur(Money m) => m.Currency == Currency.EUR && m.IsRoundedToMinorUnits && m.IsPositive;
}

// ---------------------------------------------------------------- create

internal sealed record CreateRecovery(RecoveryCreateRequest Request) : ICommand<RecoveryCreateResponse>;

internal sealed class CreateRecoveryValidator : AbstractValidator<CreateRecovery>
{
    public CreateRecoveryValidator()
    {
        RuleFor(c => c.Request.Type).IsInEnum();
        RuleFor(c => c.Request.ExpectedAmount).Must(RecoverySupport.ValidEur).WithErrorCode("AMOUNT").WithMessage("The expected amount is a positive EUR amount in cents.");
        RuleFor(c => c.Request.Reason).NotEmpty().Matches(RecoverySupport.CodePattern).WithErrorCode("CODE");
    }
}

/// <summary>
/// <c>clm.Recovery.create</c> (REQ-CLM-143): a subrogation or salvage case on an open claim, optionally on one exposure.
/// Cash is never recorded here; the recovery reserve is set through the transaction-set builder and cash arrives from BIL.
/// </summary>
internal sealed class CreateRecoveryHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, RecoveryReader reader, IOptions<ClaimsOptions> options)
    : ICommandHandler<CreateRecovery, RecoveryCreateResponse>
{
    public async Task<Result<RecoveryCreateResponse>> HandleAsync(CreateRecovery command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (claim.Status != ClaimStates.Open)
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "A recovery needs an open claim.");
        }

        if (request.ExposureId is { } exposureId && !await db.Exposures.AnyAsync(e => e.ExposureId == exposureId && e.ClaimId == claim.ClaimId, cancellationToken).ConfigureAwait(false))
        {
            return ClaimSupport.NotFound("exposure");
        }

        var salvage = request.Salvage;
        if (request.Type == RecoveryCreateRequest.TypeValue.Subrogation && salvage is not null)
        {
            return FnolAssessment.Invalid("salvage", "SALVAGE_ON_SUBROGATION", "Salvage details belong to a salvage case.");
        }

        if (salvage is { } s && ((s.Estimate is { } est && (est.Currency != Currency.EUR || est.IsNegative || !est.IsRoundedToMinorUnits))
            || (s.SalePrice is { } price && (price.Currency != Currency.EUR || price.IsNegative || !price.IsRoundedToMinorUnits))))
        {
            return FnolAssessment.Invalid("salvage", "AMOUNT", "Salvage amounts are non-negative EUR amounts in cents.");
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var today = options.Value.DateOf(now);
        var row = new RecoveryRow
        {
            RecoveryId = RecoveryId.New(), ClaimId = claim.ClaimId, ExposureId = request.ExposureId, LegalEntityId = legalEntity, Jurisdiction = claim.Jurisdiction,
            Type = request.Type == RecoveryCreateRequest.TypeValue.Salvage ? "SALVAGE" : "SUBROGATION",
            CounterpartyPartyId = request.CounterpartyPartyId, ExpectedAmount = request.ExpectedAmount.Amount, Currency = "EUR", Status = "OPEN",
            SalvageEstimate = salvage?.Estimate?.Amount, BuyerPartyId = salvage?.BuyerPartyId is { } b ? new PartyId(b) : null, SalePrice = salvage?.SalePrice?.Amount,
            AllocationRule = request.AllocationRule == RecoveryCreateRequest.AllocationRuleValue.Specified ? "SPECIFIED" : "PRO_RATA_PAID",
            CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
        };
        RecoverySupport.AddMilestone(row, "CASE_OPENED", today, now, actor, request.Reason);
        db.Recoveries.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryCreateResponse { Recovery = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class CreateRecoveryAuditor : ICommandAuditor<CreateRecovery, RecoveryCreateResponse>
{
    public CommandAuditFacts Describe(CreateRecovery command, Result<RecoveryCreateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? RecoverySupport.Facts(ok.Value.Recovery.RecoveryId, command.Request.ClaimId, new { type = command.Request.Type.ToString(), reason = command.Request.Reason })
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId), BusinessKeys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString()) };
}

// ---------------------------------------------------------------- milestone

internal sealed record RecordRecoveryMilestone(RecoveryRecordMilestoneRequest Request) : ICommand<RecoveryRecordMilestoneResponse>;

internal sealed class RecordRecoveryMilestoneValidator : AbstractValidator<RecordRecoveryMilestone>
{
    public RecordRecoveryMilestoneValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Milestone).NotEmpty().Matches(RecoverySupport.CodePattern).WithErrorCode("CODE")
            .Must(m => m != RecoveryReader.AllocationMilestone).WithErrorCode("CODE");
    }
}

/// <summary>
/// <c>clm.Recovery.recordMilestone</c> (REQ-CLM-147). Some milestones move the case: DISPUTED (from Demanded),
/// ARBITRATION_STARTED / LITIGATION_STARTED (from Disputed), SETTLEMENT_AGREED (from Open, Demanded, Disputed, arbitration or
/// litigation), SALVAGE_SOLD (salvage: buyer and sale price, becomes Agreed), REOPENED (Closed → Open). Others are recorded only.
/// </summary>
internal sealed class RecordRecoveryMilestoneHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, RecoveryReader reader)
    : ICommandHandler<RecordRecoveryMilestone, RecoveryRecordMilestoneResponse>
{
    public async Task<Result<RecoveryRecordMilestoneResponse>> HandleAsync(RecordRecoveryMilestone command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (await RecoverySupport.ClaimOfAsync(db, legalEntity, request.RecoveryId, cancellationToken).ConfigureAwait(false) is not { } claimId
            || await ClaimSupport.LoadAsync(db, legalEntity, claimId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ClaimSupport.NotFound("recovery");
        }

        var row = (await RecoverySupport.LoadAsync(db, legalEntity, request.RecoveryId, cancellationToken).ConfigureAwait(false))!;
        if (row.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(row.RecordVersion);
        }

        var status = row.Status;
        string? next;
        switch (request.Milestone)
        {
            case "REOPENED":
                next = status == "CLOSED" ? "OPEN" : null;
                break;
            case "SETTLEMENT_AGREED":
                next = status is "OPEN" or "DEMANDED" or "DISPUTED" or "IN_ARBITRATION" or "IN_LITIGATION" ? "AGREED" : null;
                break;
            case "DISPUTED":
                next = status == "DEMANDED" ? "DISPUTED" : null;
                break;
            case "ARBITRATION_STARTED":
                next = status == "DISPUTED" ? "IN_ARBITRATION" : null;
                break;
            case "LITIGATION_STARTED":
                next = status == "DISPUTED" ? "IN_LITIGATION" : null;
                break;
            case "SALVAGE_SOLD":
                if (row.Type != "SALVAGE")
                {
                    return FnolAssessment.Invalid("milestone", "SALVAGE_ONLY", "SALVAGE_SOLD belongs to a salvage case.");
                }

                if (request.Salvage is not { BuyerPartyId: not null, SalePrice: { } price } || price.Currency != Currency.EUR || !price.IsPositive || !price.IsRoundedToMinorUnits)
                {
                    return FnolAssessment.Invalid("salvage", "SALVAGE_SALE", "A sale needs a buyer party and a positive EUR sale price.");
                }

                next = status is "OPEN" or "DEMANDED" ? "AGREED" : null;
                break;
            default:
                next = RecoverySupport.IsOpen(status) ? status : null;
                break;
        }

        if (next is null)
        {
            return RecoverySupport.State($"Milestone {request.Milestone} is not allowed from {status}.");
        }

        var now = clock.Now;
        if (request.Milestone == "SALVAGE_SOLD")
        {
            row.BuyerPartyId = new PartyId(request.Salvage!.BuyerPartyId!.Value);
            row.SalePrice = request.Salvage.SalePrice!.Value.Amount;
            if (request.Salvage.Estimate is { } estimate)
            {
                row.SalvageEstimate = estimate.Amount;
            }

            if (row.ReceivableId is null)
            {
                row.ExpectedAmount = row.SalePrice.Value;
            }
        }

        RecoverySupport.AddMilestone(row, request.Milestone, request.OccurredOn, now, context.Actor.ToString(), request.Note);
        row.Status = next;
        RecoverySupport.Touch(row, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        return new RecoveryRecordMilestoneResponse { Recovery = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class RecordRecoveryMilestoneAuditor : ICommandAuditor<RecordRecoveryMilestone, RecoveryRecordMilestoneResponse>
{
    public CommandAuditFacts Describe(RecordRecoveryMilestone command, Result<RecoveryRecordMilestoneResponse>? result) =>
        result is { IsSuccess: true } ok
            ? RecoverySupport.Facts(command.Request.RecoveryId, ok.Value.Recovery.ClaimId, new { milestone = command.Request.Milestone, state = ok.Value.Recovery.State.ToString() })
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Recovery", command.Request.RecoveryId) };
}

// ---------------------------------------------------------------- demand

internal sealed record DemandRecovery(RecoveryDemandRequest Request) : ICommand<RecoveryDemandResponse>;

internal sealed class DemandRecoveryValidator : AbstractValidator<DemandRecovery>
{
    public DemandRecoveryValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Amount).Must(RecoverySupport.ValidEur).WithErrorCode("AMOUNT").WithMessage("The demand is a positive EUR amount in cents.");
    }
}

/// <summary>
/// <c>clm.Recovery.demand</c> (REQ-CLM-145, D-SL4-06): registers a BIL receivable on the counterparty (in process, idempotent on
/// the recovery id). The cash is later matched by BIL, which publishes <c>CashAllocated</c>; CLM records the recovery from that
/// evidence only. A user-specified allocation is validated against the claim's exposures and must sum to the demand.
/// </summary>
internal sealed class DemandRecoveryHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, RecoveryReader reader,
    IOptions<ClaimsOptions> options, IServiceProvider services) : ICommandHandler<DemandRecovery, RecoveryDemandResponse>
{
    public async Task<Result<RecoveryDemandResponse>> HandleAsync(DemandRecovery command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (await RecoverySupport.ClaimOfAsync(db, legalEntity, request.RecoveryId, cancellationToken).ConfigureAwait(false) is not { } claimId
            || await ClaimSupport.LoadAsync(db, legalEntity, claimId, cancellationToken).ConfigureAwait(false) is not { } claim)
        {
            return ClaimSupport.NotFound("recovery");
        }

        var row = (await RecoverySupport.LoadAsync(db, legalEntity, request.RecoveryId, cancellationToken).ConfigureAwait(false))!;
        if (row.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(row.RecordVersion);
        }

        if (row.Status is not ("OPEN" or "AGREED") || row.ReceivableId is not null)
        {
            return RecoverySupport.State($"A demand is not possible from {row.Status}{(row.ReceivableId is null ? string.Empty : " (already demanded)")}.");
        }

        if (row.Type is not ("SUBROGATION" or "SALVAGE"))
        {
            return RecoverySupport.State("Only subrogation and salvage cases are demanded through a BIL receivable.");
        }

        var specified = request.AllocationRule is { } rule ? rule == RecoveryDemandRequest.AllocationRuleValue.Specified : row.AllocationRule == "SPECIFIED";
        string? allocationJson = null;
        if (specified)
        {
            var lines = request.Allocation ?? [];
            if (lines.Count == 0 || lines.Count > 20 || lines.Sum(l => l.Amount.Amount) != request.Amount.Amount
                || lines.Any(l => l.Amount.Currency != Currency.EUR || !l.Amount.IsPositive || !l.Amount.IsRoundedToMinorUnits))
            {
                return RecoverySupport.Allocation("A specified allocation needs positive EUR lines that sum to the demand.");
            }

            var exposures = await db.Exposures.AsNoTracking().Where(e => e.ClaimId == claim.ClaimId).Select(e => e.ExposureId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var settings = options.Value.Financials;
            if (lines.Any(l => !exposures.Contains(l.ExposureId) || (row.ExposureId is { } own && own != l.ExposureId) || !settings.Allows(l.CostType, l.CostCategory)))
            {
                return RecoverySupport.Allocation("An allocation line names an exposure, cost type or category that is not on the recovery's claim.");
            }

            allocationJson = JsonSerializer.Serialize(lines, SharedKernelJson.Options);
        }

        var now = clock.Now;
        var today = options.Value.DateOf(now);
        Guid receivableId;
        BillingAccountId accountId;
        string reference;
        if (context.DryRun)
        {
            (receivableId, accountId, reference) = (Guid.Empty, default, "DRY-RUN");
        }
        else
        {
            if (services.GetService(typeof(IBillingReceivableService)) is not IBillingReceivableService receivables)
            {
                return DomainError.Of(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", "The billing receivable service is not available.");
            }

            try
            {
                var response = await receivables.RegisterAsync(new ReceivableRegisterRequest
                {
                    SourceType = ReceivableSourceType.ClmClaimPayment,
                    SourceId = row.RecoveryId.Value.ToString("D"),
                    CounterpartyPartyId = row.CounterpartyPartyId,
                    ClaimId = claim.ClaimId,
                    RecoveryId = row.RecoveryId.Value,
                    Purpose = row.Type == "SALVAGE" ? ReceivablePurpose.Salvage : ReceivablePurpose.Subrogation,
                    Amount = request.Amount,
                    DueDate = request.DueDate,
                }, new CommandOptions(IdempotencyKey.From(row.RecoveryId.Value)), cancellationToken).ConfigureAwait(false);
                (receivableId, accountId, reference) = (response.ReceivableId, response.BillingAccountId, response.PaymentReference);
            }
            catch (DomainException ex)
            {
                return new DomainError(ErrorCode.For(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE"), $"BIL refused the receivable ({ex.Error.Code.Value}).")
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["bilCode"] = ex.Error.Code.Value },
                };
            }

            row.ReceivableId = receivableId;
            row.PaymentReference = reference;
        }

        row.DemandedOn = today;
        row.ExpectedAmount = request.Amount.Amount;
        row.AllocationRule = specified ? "SPECIFIED" : "PRO_RATA_PAID";
        if (row.Status == "OPEN")
        {
            row.Status = "DEMANDED";
        }

        if (allocationJson is not null)
        {
            RecoverySupport.AddMilestone(row, RecoveryReader.AllocationMilestone, today, now, context.Actor.ToString(), allocationJson);
        }

        RecoverySupport.AddMilestone(row, "DEMAND_ISSUED", today, now, context.Actor.ToString(), null);
        RecoverySupport.Touch(row, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ClaimSupport.Stale();
        }

        return new RecoveryDemandResponse
        {
            Recovery = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false),
            ReceivableId = receivableId, BillingAccountId = accountId, PaymentReference = reference,
        };
    }
}

internal sealed class DemandRecoveryAuditor : ICommandAuditor<DemandRecovery, RecoveryDemandResponse>
{
    public CommandAuditFacts Describe(DemandRecovery command, Result<RecoveryDemandResponse>? result) =>
        result is { IsSuccess: true } ok
            ? RecoverySupport.Facts(command.Request.RecoveryId, ok.Value.Recovery.ClaimId, new { amount = command.Request.Amount.Amount, receivableId = ok.Value.ReceivableId })
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Recovery", command.Request.RecoveryId) };
}

// ---------------------------------------------------------------- write-off

internal sealed record WriteOffRecovery(RecoveryWriteOffRequest Request) : ICommand<RecoveryWriteOffResponse>;

internal sealed class WriteOffRecoveryValidator : AbstractValidator<WriteOffRecovery>
{
    public WriteOffRecoveryValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Amount).Must(RecoverySupport.ValidEur).WithErrorCode("AMOUNT").WithMessage("The write-off is a positive EUR amount in cents.");
        RuleFor(c => c.Request.Reason).NotEmpty().Matches(RecoverySupport.CodePattern).WithErrorCode("CODE");
    }
}

/// <summary>
/// <c>clm.Recovery.writeOff</c> (REQ-CLM-150): releases the open recovery reserve of the case under <c>CLM.RECOVERY_WRITEOFF</c>
/// by building and submitting an ordinary transaction set (negative recovery-reserve lines, reason <c>WRITE_OFF</c>), so the
/// aggregate authority, four-eyes and PLT approval apply unchanged. The case becomes WrittenOff when that set is approved
/// (here, or later by the approver). The amount must equal the open recovery reserve.
/// </summary>
internal sealed class WriteOffRecoveryHandler(
    ClaimsDbContext db, RequestContext context, ClaimProtection protection, RecoveryReader reader,
    ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse> build,
    ICommandHandler<SubmitTransactionSet, TransactionSetSubmitResponse> submit) : ICommandHandler<WriteOffRecovery, RecoveryWriteOffResponse>
{
    public async Task<Result<RecoveryWriteOffResponse>> HandleAsync(WriteOffRecovery command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        if (await RecoverySupport.ClaimOfAsync(db, legalEntity, request.RecoveryId, cancellationToken).ConfigureAwait(false) is not { } claimId
            || await ClaimSupport.LoadAsync(db, legalEntity, claimId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ClaimSupport.NotFound("recovery");
        }

        var row = (await RecoverySupport.LoadAsync(db, legalEntity, request.RecoveryId, cancellationToken).ConfigureAwait(false))!;
        if (row.RecordVersion != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(row.RecordVersion);
        }

        if (!RecoverySupport.IsOpen(row.Status))
        {
            return RecoverySupport.State($"A {row.Status} case cannot be written off.");
        }

        // Open recovery reserve per line of this recovery (approved transactions only).
        var effective = new[] { Codes.Of(SetStatus.Approved), Codes.Of(SetStatus.Posted) };
        var txns = await (from t in db.FinancialTransactions.AsNoTracking()
                          join s in db.TransactionSets.AsNoTracking() on t.SetId equals s.SetId
                          where t.RecoveryId == row.RecoveryId && effective.Contains(s.Status)
                          select new { t.ReserveLineId, t.Kind, t.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var reserveKind = Codes.Of(TransactionKind.RecoveryReserve);
        var open = txns.GroupBy(t => t.ReserveLineId)
            .Select(g => (Line: g.Key, Open: g.Sum(t => t.Kind == reserveKind ? t.Amount : -t.Amount)))
            .Where(x => x.Open > 0m).ToList();
        var total = open.Sum(x => x.Open);
        if (total <= 0m)
        {
            return RecoverySupport.State("There is no open recovery reserve to release; set one first.");
        }

        if (request.Amount.Amount != total)
        {
            return FnolAssessment.Invalid("amount", "WRITE_OFF_AMOUNT", $"The write-off must equal the open recovery reserve ({total.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }

        var lineIds = open.Select(x => x.Line).ToList();
        var lines = await db.ReserveLines.AsNoTracking().Where(l => lineIds.Contains(l.ReserveLineId)).ToDictionaryAsync(l => l.ReserveLineId, cancellationToken).ConfigureAwait(false);
        var buildRequest = new TransactionSetBuildRequest
        {
            ClaimId = claimId,
            Transactions = [.. open.Select(x => new Item
            {
                Kind = Item.KindValue.RecoveryReserve, ExposureId = lines[x.Line].ExposureId, CostType = lines[x.Line].CostType,
                CostCategory = lines[x.Line].CostCategory, Amount = ClaimMoney.Of(-x.Open, lines[x.Line].Currency),
                Reason = RecoverySupport.WriteOffReason, RecoveryId = row.RecoveryId,
            })],
        };

        TransactionSetSubmitResponse submitted;
        using (context.Use(Derived(context.IdempotencyKey, "build"), context.DryRun))
        {
            var built = await build.HandleAsync(new BuildTransactionSet(buildRequest), cancellationToken).ConfigureAwait(false);
            if (built.IsFailure)
            {
                return built.Error!;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            using (context.Use(Derived(context.IdempotencyKey, "submit"), context.DryRun))
            {
                var result = await submit.HandleAsync(new SubmitTransactionSet(new TransactionSetSubmitRequest { SetId = built.Value.SetId }), cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.Error!;
                }

                submitted = result.Value;
            }
        }

        var written = submitted.Status == Codes.Of(SetStatus.Approved);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryWriteOffResponse
        {
            Recovery = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false),
            Outcome = written ? RecoveryWriteOffResponse.OutcomeValue.WrittenOff : RecoveryWriteOffResponse.OutcomeValue.PendingApproval,
            SetId = submitted.SetId,
            ApprovalRequestId = submitted.ApprovalRequestId,
        };
    }

    private static IdempotencyKey Derived(IdempotencyKey? key, string operation)
    {
        if (key is not { } k)
        {
            return IdempotencyKey.New();
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{k.Value:D}|recovery.writeOff|{operation}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return IdempotencyKey.From(new Guid(bytes));
    }
}

internal sealed class WriteOffRecoveryAuditor : ICommandAuditor<WriteOffRecovery, RecoveryWriteOffResponse>
{
    public CommandAuditFacts Describe(WriteOffRecovery command, Result<RecoveryWriteOffResponse>? result) =>
        result is { IsSuccess: true } ok
            ? RecoverySupport.Facts(command.Request.RecoveryId, ok.Value.Recovery.ClaimId, new { amount = command.Request.Amount.Amount, reason = command.Request.Reason, outcome = ok.Value.Outcome.ToString() })
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Recovery", command.Request.RecoveryId) };
}
