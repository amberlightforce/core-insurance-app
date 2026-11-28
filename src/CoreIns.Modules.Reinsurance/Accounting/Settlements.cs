using System.Globalization;
using CoreIns.Modules.Reinsurance.Authority;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Recovery;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
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
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Accounting;

/// <summary>The statuses of a settlement (BO-RI-16).</summary>
internal static class SettlementStatus
{
    public const string Proposed = "PROPOSED";
    public const string Approved = "APPROVED";
    public const string Released = "RELEASED";
    public const string Recorded = "RECORDED";

    /// <summary>A live settlement whose balance moved before it was settled; replaced by a new proposal.</summary>
    public const string Superseded = "SUPERSEDED";
}

/// <summary>Reading, locking, netting and mapping settlements.</summary>
internal static class SettlementSupport
{
    /// <summary>Source kind of the RECOVERY account item a settlement posts (the same kind as a cash call receipt, so the statement arithmetic is unchanged).</summary>
    public const string ItemSourceKind = "SETTLEMENT";

    public const string Pay = "PAY";
    public const string Receive = "RECEIVE";

    /// <summary>The only settled currency: the RI.SETTLEMENT authority is in EUR. TODO(hardening): other currencies with a converted authority amount.</summary>
    public const string SettledCurrency = "EUR";

    public static async Task<SettlementRow?> LockAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, Guid settlementId, CancellationToken cancellationToken)
    {
        var rows = await db.Settlements
            .FromSql($"SELECT * FROM ri.settlement WHERE settlement_id = {settlementId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// The net balance of a reinsurer through a period, signed (positive is payable to the reinsurer): the sum of its technical account items of that
    /// period and earlier (the arithmetic of the statement closing and the balance report), plus the settlement items posted after that period, which
    /// settle part of what lies before it. Also the statement-time sum without those items.
    /// </summary>
    public static async Task<(decimal Net, decimal ThroughPeriod)> NetAsync(
        ReinsuranceDbContext db, LegalEntityId legalEntity, Guid reinsurer, string currency, string period, CancellationToken cancellationToken)
    {
        var items = await db.AccountItems.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.ReinsurerPartyId == reinsurer && i.Currency == currency && (i.Period.CompareTo(period) <= 0 || i.SourceKind == ItemSourceKind))
            .Select(i => new { i.Amount, i.Period, i.SourceKind })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var through = items.Where(i => string.CompareOrdinal(i.Period, period) <= 0).Sum(i => i.Amount);
        return (items.Sum(i => i.Amount), through);
    }

    /// <summary>The period of the settlement item: the value date's month, or the month after the latest live statement of the reinsurer when that statement already covers it.</summary>
    public static async Task<string> ItemPeriodAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, Guid party, string currency, BusinessDate date, CancellationToken cancellationToken)
    {
        var period = AccountItemViews.PeriodOf(date);
        var latest = await db.Statements.AsNoTracking()
            .Where(s => s.LegalEntityId == legalEntity && s.ReinsurerPartyId == party && s.Currency == currency && s.Status != StatementStatus.Superseded)
            .Select(s => s.Period).OrderByDescending(p => p).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null || string.CompareOrdinal(latest, period) < 0)
        {
            return period;
        }

        var year = int.Parse(latest.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var month = int.Parse(latest.AsSpan(5, 2), CultureInfo.InvariantCulture);
        return new DateOnly(year, month, 1).AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    /// <summary>Null when the balance still equals the settlement net; the stale refusal otherwise (the account moved after the proposal).</summary>
    public static async Task<DomainError?> CheckNetAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, SettlementRow row, CancellationToken cancellationToken)
    {
        var (net, _) = await NetAsync(db, legalEntity, row.ReinsurerPartyId, row.Currency.Trim(), row.Period, cancellationToken).ConfigureAwait(false);
        return net == row.Net ? null : RiErrors.Stale($"The balance of the reinsurer is now {AccountingViews.Fixed(net)}, not the settled {AccountingViews.Fixed(row.Net)}; propose the settlement again.");
    }

    /// <summary>Posts the movement as a technical account item (the amount that brings the balance to zero) and seals the settlement as settled.</summary>
    public static async Task<AccountItemRow> PostAsync(
        ReinsuranceDbContext db, LegalEntityId legalEntity, SettlementRow row, string status, BusinessDate valueDate, string actor, Instant now, CancellationToken cancellationToken)
    {
        var period = await ItemPeriodAsync(db, legalEntity, row.ReinsurerPartyId, row.Currency.Trim(), valueDate, cancellationToken).ConfigureAwait(false);
        var anchor = await db.AccountItems.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.ReinsurerPartyId == row.ReinsurerPartyId && i.Currency == row.Currency)
            .OrderBy(i => i.ItemId).Select(i => new { i.ContractId, i.ContractYear }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var item = new AccountItemRow
        {
            ItemId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ContractId = anchor.ContractId, ContractYear = anchor.ContractYear, LayerNo = null, ReinsurerPartyId = row.ReinsurerPartyId,
            Kind = AccountItemKinds.Recovery, SourceKind = ItemSourceKind, SourceId = row.SettlementId.ToString("D"), SourceGroup = row.SettlementId.ToString("D"),
            Currency = row.Currency.Trim(), Amount = -row.Net, Period = period, ItemDate = valueDate, Reason = "Settlement " + row.SettlementId.ToString("D"), CreatedAt = now, CreatedBy = actor,
        };
        db.AccountItems.Add(item);
        row.Status = status;
        row.SettledAt = now;
        row.SettledBy = actor;
        row.ValueDate = valueDate;
        row.ItemId = item.ItemId;
        return item;
    }

    /// <summary>Publishes <c>SettlementRecorded</c> (REQ-RI-195) for the settled amount.</summary>
    public static void Publish(IEventPublisher events, SettlementRow row, AccountItemRow item)
    {
        var currency = Currency.FromCode(row.Currency.Trim());
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(SettlementRecordedV1.Descriptor),
            "Counterparty",
            row.ReinsurerPartyId.ToString("D"),
            new SettlementRecordedV1
            {
                SettlementId = row.SettlementId,
                Items = [new ItemAmount { ItemId = item.ItemId, Amount = new Money(row.Net, currency) }],
                Currency = currency,
                FxDifference = new Money(0m, currency),
                BilReference = row.BilReference ?? string.Empty,
            },
            BusinessKeys.Empty.With("counterpartyPartyId", row.ReinsurerPartyId.ToString("D")).With("settlementId", row.SettlementId.ToString("D"))));
    }

    public static SettlementView ToView(SettlementRow row) => new()
    {
        SettlementId = row.SettlementId, ReinsurerPartyId = new PartyId(row.ReinsurerPartyId), Currency = row.Currency.Trim(), Period = row.Period, StatementId = row.StatementId,
        StatementClosing = row.StatementClosing is { } closing ? AccountingViews.Eur(closing, row.Currency) : null,
        Net = AccountingViews.Eur(row.Net, row.Currency), Amount = AccountingViews.Eur(row.Amount, row.Currency),
        Direction = Enum.Parse<SettlementView.DirectionValue>(row.Direction, ignoreCase: true), Status = Enum.Parse<SettlementView.StatusValue>(row.Status, ignoreCase: true),
        ProposedAt = row.ProposedAt, ProposedBy = row.ProposedBy, ApprovedAt = row.ApprovedAt, ApprovedBy = row.ApprovedBy, SettledAt = row.SettledAt, SettledBy = row.SettledBy,
        ValueDate = row.ValueDate, BankReference = row.BankReference, EvidenceRef = row.EvidenceRef, ItemId = row.ItemId, BilReference = row.BilReference,
    };

    public static CommandAuditFacts Facts(Guid settlementId, Guid? reinsurer, object before, object after, bool ok) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.RI, "Settlement", settlementId.ToString("D")),
        BusinessKeys = reinsurer is { } party
            ? BusinessKeys.Empty.With("settlementId", settlementId.ToString("D")).With("reinsurerPartyId", party.ToString("D"))
            : BusinessKeys.Empty.With("settlementId", settlementId.ToString("D")),
        Changes = ok ? AuditDiff.Compute(before, after) : AuditDiff.Compute(before, before),
    };

    public static DomainError Wrong(SettlementRow row, string action) => AccountingGuards.State($"The settlement is {row.Status}; it cannot be {action}.");
}

/// <summary>Reads of the settlements.</summary>
internal sealed class SettlementReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<SettlementListResponse>> ListAsync(Guid? reinsurerPartyId, string? status, int? limit, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var query = db.Settlements.AsNoTracking().Where(s => s.LegalEntityId == legalEntity);
        if (reinsurerPartyId is { } party)
        {
            query = query.Where(s => s.ReinsurerPartyId == party);
        }

        if (!string.IsNullOrEmpty(status))
        {
            var wanted = status.ToUpperInvariant();
            if (wanted is not (SettlementStatus.Proposed or SettlementStatus.Approved or SettlementStatus.Released or SettlementStatus.Recorded or SettlementStatus.Superseded))
            {
                return RiErrors.Validation("status", "STATUS_INVALID", "The status filter is not valid.");
            }

            query = query.Where(s => s.Status == wanted);
        }

        var rows = await query.OrderByDescending(s => s.ProposedAt).ThenByDescending(s => s.SettlementId).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SettlementListResponse { Items = [.. rows.Select(SettlementSupport.ToView)] };
    }
}

/// <summary><c>ri.Settlement.propose</c> as a command of the platform pipeline.</summary>
internal sealed record ProposeSettlement(SettlementProposeRequest Request) : ICommand<SettlementProposeResponse>;

/// <summary>Shape rules: a currency code and a yyyy-MM period.</summary>
internal sealed class ProposeSettlementValidator : AbstractValidator<ProposeSettlement>
{
    public ProposeSettlementValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Currency).NotEmpty().Length(3).WithErrorCode("CURRENCY").When(c => c.Request is not null);
        RuleFor(c => c.Request.Period!).Matches("^[0-9]{4}-(0[1-9]|1[0-2])$").WithErrorCode("PERIOD").When(c => c.Request?.Period is not null);
    }
}

/// <summary>
/// Nets the balance of one reinsurer through a period into a settlement proposal (BO-RI-16, D-CON-13): positive is paid to the reinsurer, negative is
/// collected from it. The net is the same arithmetic as the statement closing, so it equals the statement balance of that period; a disputed statement
/// blocks the proposal and a statement that no longer matches the items asks for a reissue. One live proposal per reinsurer and currency.
/// TODO(hardening): non-EUR settlements (the authority is in EUR); partial settlements and netting across several reinsurers or a selection of items.
/// </summary>
internal sealed class ProposeSettlementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options)
    : ICommandHandler<ProposeSettlement, SettlementProposeResponse>
{
    public async Task<Result<SettlementProposeResponse>> HandleAsync(ProposeSettlement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (currency != SettlementSupport.SettledCurrency)
        {
            return RiErrors.Validation("currency", "CURRENCY_UNSUPPORTED", $"Only {SettlementSupport.SettledCurrency} settlements are supported.");
        }

        var party = request.ReinsurerPartyId.Value;
        var period = request.Period ?? AccountItemViews.PeriodOf(clock.Now.ToBusinessDate(options.Value.Zone));
        // One live settlement per reinsurer: one whose balance moved meanwhile can never be settled and is superseded by this proposal.
        var live = await db.Settlements
            .Where(s => s.LegalEntityId == legalEntity && s.ReinsurerPartyId == party && s.Currency == currency && (s.Status == SettlementStatus.Proposed || s.Status == SettlementStatus.Approved))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var other in live)
        {
            if (await SettlementSupport.CheckNetAsync(db, legalEntity, other, cancellationToken).ConfigureAwait(false) is null)
            {
                return AccountingGuards.State("The reinsurer already has a settlement waiting for approval or release.");
            }

            other.Status = SettlementStatus.Superseded;
        }

        if (live.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var (net, through) = await SettlementSupport.NetAsync(db, legalEntity, party, currency, period, cancellationToken).ConfigureAwait(false);
        if (net == 0m)
        {
            return AccountingGuards.State("The reinsurer has nothing to settle through that period.");
        }

        var statement = await db.Statements.AsNoTracking()
            .Where(s => s.LegalEntityId == legalEntity && s.ReinsurerPartyId == party && s.Currency == currency && s.Period == period && s.Status != StatementStatus.Superseded)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (statement is not null)
        {
            if (statement.Status == StatementStatus.Disputed)
            {
                return AccountingGuards.State("The statement of the period is disputed; settle after the dispute is resolved and the statement reissued.");
            }

            if (statement.Closing != through)
            {
                return RiErrors.Stale($"The statement closing ({AccountingViews.Fixed(statement.Closing)}) no longer matches the items ({AccountingViews.Fixed(through)}); reissue the statement first.");
            }
        }

        var row = new SettlementRow
        {
            SettlementId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ReinsurerPartyId = party, Currency = currency, Period = period, StatementId = statement?.StatementId,
            StatementClosing = statement?.Closing, Net = net, Amount = Math.Abs(net), Direction = net > 0m ? SettlementSupport.Pay : SettlementSupport.Receive, Status = SettlementStatus.Proposed,
            ProposedAt = clock.Now, ProposedBy = context.Actor.ToString(),
        };
        db.Settlements.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("Another request proposed a settlement for the reinsurer; try again.");
        }

        return new SettlementProposeResponse { Settlement = SettlementSupport.ToView(row) };
    }
}

/// <summary>Audit facts of <c>ri.Settlement.propose</c>.</summary>
internal sealed class ProposeSettlementAuditor : ICommandAuditor<ProposeSettlement, SettlementProposeResponse>
{
    public CommandAuditFacts Describe(ProposeSettlement command, Result<SettlementProposeResponse>? result) =>
        result is { IsSuccess: true } ok
            ? SettlementSupport.Facts(ok.Value.Settlement.SettlementId, command.Request.ReinsurerPartyId.Value, new { status = "NONE" }, new { status = "PROPOSED", net = ok.Value.Settlement.Net.Amount.ToString(CultureInfo.InvariantCulture) }, true)
            : new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.RI, "Settlement", "new"),
                BusinessKeys = BusinessKeys.Empty.With("reinsurerPartyId", command.Request.ReinsurerPartyId.Value.ToString("D")),
            };
}

/// <summary><c>ri.Settlement.approve</c> as a command of the platform pipeline.</summary>
internal sealed record ApproveSettlement(SettlementApproveRequest Request) : ICommand<SettlementApproveResponse>;

/// <summary>Shape rules: a body.</summary>
internal sealed class ApproveSettlementValidator : AbstractValidator<ApproveSettlement>
{
    public ApproveSettlementValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>
/// Approves a PROPOSED settlement. Maker-checker: the proposer cannot approve (<c>RI-ERR-SOD</c>); the actor needs <c>RI.SETTLEMENT</c> authority over the
/// settled amount (PITFALLS 4: the dimensions are computed here), and a referral or denial is refused (<c>RI-ERR-AUTHORITY</c>). The balance must still equal the net.
/// </summary>
internal sealed class ApproveSettlementHandler(
    ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IAuthorityService authority) : ICommandHandler<ApproveSettlement, SettlementApproveResponse>
{
    public async Task<Result<SettlementApproveResponse>> HandleAsync(ApproveSettlement command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await SettlementSupport.LockAsync(db, legalEntity, command.Request.SettlementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("settlement");
        }

        if (row.Status != SettlementStatus.Proposed)
        {
            return SettlementSupport.Wrong(row, "approved");
        }

        if (ContractSupport.ActorKeys(context).Contains(row.ProposedBy, StringComparer.Ordinal))
        {
            return RiErrors.Sod("The person who proposed the settlement cannot approve it.");
        }

        var now = clock.Now;
        var check = await authority.CheckAsync(
            new AuthorityCheckRequest(
                context.Actor, context.Roles, ReinsuranceAuthorityTypes.Settlement,
                new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [ReinsuranceAuthorityTypes.AmountDimension] = DimensionValue.Of(new Money(row.Amount, Currency.FromCode(row.Currency.Trim()))) },
                new ObjectRef(ModuleCode.RI, "Settlement", row.SettlementId.ToString("D")), now),
            cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision != AuthorityDecision.Allow)
        {
            return RecoveryOpsErrors.Authority($"No authority to approve a settlement of {AccountingViews.Fixed(row.Amount)} {row.Currency.Trim()} (RI.SETTLEMENT: {check.ReasonCode}).");
        }

        if (await SettlementSupport.CheckNetAsync(db, legalEntity, row, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        row.Status = SettlementStatus.Approved;
        row.ApprovedAt = now;
        row.ApprovedBy = context.Actor.ToString();
        row.ApprovalComment = command.Request.Comment?.Trim();
        row.AuthorityCheckId = check.CheckId.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SettlementApproveResponse { Settlement = SettlementSupport.ToView(row) };
    }
}

/// <summary>Audit facts of <c>ri.Settlement.approve</c>.</summary>
internal sealed class ApproveSettlementAuditor : ICommandAuditor<ApproveSettlement, SettlementApproveResponse>
{
    public CommandAuditFacts Describe(ApproveSettlement command, Result<SettlementApproveResponse>? result) =>
        SettlementSupport.Facts(command.Request.SettlementId, null, new { status = "PROPOSED" }, new { status = "APPROVED" }, result is { IsSuccess: true });
}

/// <summary><c>ri.Settlement.release</c> as a command of the platform pipeline.</summary>
internal sealed record ReleaseSettlement(SettlementReleaseRequest Request) : ICommand<SettlementReleaseResponse>;

/// <summary>Shape rules: a body.</summary>
internal sealed class ReleaseSettlementValidator : AbstractValidator<ReleaseSettlement>
{
    public ReleaseSettlementValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>
/// Releases an APPROVED settlement exactly once (the row lock and the status): the movement posts to the technical account (a RECOVERY item that brings the
/// balance to zero) and <c>SettlementRecorded</c> is published. The balance must still equal the approved net.
/// TODO(hardening): create the BIL disbursement (pay) or receivable (receive) with source RI_SETTLEMENT (bil.Disbursement.request, bil.Receivable.register)
/// once BIL serves that source (it refuses it with BIL-ERR-SOURCE today), carry its id as the bilReference, and add the FIN rule set that posts
/// SettlementRecorded on GL-1320 and GL-4210 (D-SL4-10, placeholders stay flagged). Until then the bilReference is a placeholder naming the settlement.
/// </summary>
internal sealed class ReleaseSettlementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options, IEventPublisher events)
    : ICommandHandler<ReleaseSettlement, SettlementReleaseResponse>
{
    public async Task<Result<SettlementReleaseResponse>> HandleAsync(ReleaseSettlement command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await SettlementSupport.LockAsync(db, legalEntity, command.Request.SettlementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("settlement");
        }

        if (row.Status != SettlementStatus.Approved)
        {
            return SettlementSupport.Wrong(row, "released");
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var valueDate = command.Request.ValueDate ?? today;
        if (valueDate > today)
        {
            return RiErrors.Validation("valueDate", "VALUE_DATE", "The value date cannot be in the future.");
        }

        if (await SettlementSupport.CheckNetAsync(db, legalEntity, row, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        row.BilReference = "RI_SETTLEMENT:" + row.SettlementId.ToString("D");
        var item = await SettlementSupport.PostAsync(db, legalEntity, row, SettlementStatus.Released, valueDate, context.Actor.ToString(), clock.Now, cancellationToken).ConfigureAwait(false);
        SettlementSupport.Publish(events, row, item);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SettlementReleaseResponse { Settlement = SettlementSupport.ToView(row) };
    }
}

/// <summary>Audit facts of <c>ri.Settlement.release</c>.</summary>
internal sealed class ReleaseSettlementAuditor : ICommandAuditor<ReleaseSettlement, SettlementReleaseResponse>
{
    public CommandAuditFacts Describe(ReleaseSettlement command, Result<SettlementReleaseResponse>? result) =>
        SettlementSupport.Facts(command.Request.SettlementId, null, new { status = "APPROVED" }, new { status = "RELEASED" }, result is { IsSuccess: true });
}

/// <summary><c>ri.Settlement.recordManual</c> as a command of the platform pipeline.</summary>
internal sealed record RecordManualSettlement(SettlementRecordManualRequest Request) : ICommand<SettlementRecordManualResponse>;

/// <summary>Shape rules: a bank reference and the evidence.</summary>
internal sealed class RecordManualSettlementValidator : AbstractValidator<RecordManualSettlement>
{
    public RecordManualSettlementValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.BankReference).NotEmpty().MaximumLength(100).WithErrorCode("BANK_REFERENCE").When(c => c.Request is not null);
        RuleFor(c => c.Request.EvidenceRef).NotEmpty().MaximumLength(500).WithErrorCode("EVIDENCE").When(c => c.Request is not null);
    }
}

/// <summary>
/// Records the off-system movement of an APPROVED settlement (BO-RI-16): the money moved outside the platform, so nothing goes to BIL; the bank reference
/// (unique per reinsurer, NX-EC-08) and the evidence are kept and the movement posts to the technical account like a release. The amount must equal the
/// settled amount. TODO(hardening): partial manual movements, and attach the evidence as a DOC document instead of a reference.
/// </summary>
internal sealed class RecordManualSettlementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IOptions<ReinsuranceOptions> options, IEventPublisher events)
    : ICommandHandler<RecordManualSettlement, SettlementRecordManualResponse>
{
    public async Task<Result<SettlementRecordManualResponse>> HandleAsync(RecordManualSettlement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await SettlementSupport.LockAsync(db, legalEntity, request.SettlementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("settlement");
        }

        if (row.Status != SettlementStatus.Approved)
        {
            return SettlementSupport.Wrong(row, "recorded as a manual movement");
        }

        if (request.Amount.Currency.Code != row.Currency.Trim())
        {
            return RiErrors.Validation("amount", "CURRENCY_MISMATCH", $"The movement must be in the settlement currency {row.Currency.Trim()}.");
        }

        if (AccountingGuards.CheckCents("amount", request.Amount, allowZero: false) is { } amountError)
        {
            return amountError;
        }

        if (request.Amount.Amount != row.Amount)
        {
            return AccountingGuards.State($"The movement ({AccountingViews.Fixed(request.Amount.Amount)}) must equal the settled amount ({AccountingViews.Fixed(row.Amount)}).");
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var valueDate = request.ValueDate ?? today;
        if (valueDate > today)
        {
            return RiErrors.Validation("valueDate", "VALUE_DATE", "The value date cannot be in the future.");
        }

        var reference = request.BankReference.Trim();
        if (await db.Settlements.AsNoTracking().AnyAsync(s => s.LegalEntityId == legalEntity && s.ReinsurerPartyId == row.ReinsurerPartyId && s.BankReference == reference, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State("The bank reference was already recorded for a settlement of this reinsurer.");
        }

        if (await SettlementSupport.CheckNetAsync(db, legalEntity, row, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        row.BankReference = reference;
        row.EvidenceRef = request.EvidenceRef.Trim();
        row.BilReference = "MANUAL:" + reference;
        var item = await SettlementSupport.PostAsync(db, legalEntity, row, SettlementStatus.Recorded, valueDate, context.Actor.ToString(), clock.Now, cancellationToken).ConfigureAwait(false);
        SettlementSupport.Publish(events, row, item);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("The bank reference was recorded by another request; try again.");
        }

        return new SettlementRecordManualResponse { Settlement = SettlementSupport.ToView(row) };
    }
}

/// <summary>Audit facts of <c>ri.Settlement.recordManual</c>.</summary>
internal sealed class RecordManualSettlementAuditor : ICommandAuditor<RecordManualSettlement, SettlementRecordManualResponse>
{
    public CommandAuditFacts Describe(RecordManualSettlement command, Result<SettlementRecordManualResponse>? result) =>
        SettlementSupport.Facts(
            command.Request.SettlementId, null, new { status = "APPROVED" },
            new { status = "RECORDED", bankReference = command.Request.BankReference, evidenceRef = command.Request.EvidenceRef }, result is { IsSuccess: true });
}
