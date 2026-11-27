using System.Globalization;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Accounting;

/// <summary>The kinds of technical account item (BO-RI-11).</summary>
internal static class AccountItemKinds
{
    public const string CededPremium = "CEDED_PREMIUM";
    public const string DepositInstalment = "DEPOSIT_INSTALMENT";
    public const string PremiumAdjustment = "PREMIUM_ADJUSTMENT";
    public const string Commission = "COMMISSION";
    public const string Recovery = "RECOVERY";

    public static readonly string[] All = [CededPremium, DepositInstalment, PremiumAdjustment, Commission, Recovery];

    /// <summary>The premium side of the balance report.</summary>
    public static bool IsPremium(string kind) => kind is CededPremium or DepositInstalment or PremiumAdjustment;
}

/// <summary>Row to view mapping and period helpers of the technical account.</summary>
internal static class AccountItemViews
{
    public static string PeriodOf(BusinessDate date) => date.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool IsPeriod(string? value) =>
        value is { Length: 7 } && value[4] == '-' && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _)
        && int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12;

    public static AccountItemView ToView(AccountItemRow row) => new()
    {
        ItemId = row.ItemId, ContractId = row.ContractId, ContractYear = row.ContractYear, LayerNo = row.LayerNo, ReinsurerPartyId = new PartyId(row.ReinsurerPartyId),
        Kind = Enum.Parse<AccountItemView.KindValue>(row.Kind.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
        SourceKind = row.SourceKind, SourceId = row.SourceId, Amount = AccountingViews.Eur(row.Amount, row.Currency), Period = row.Period, ItemDate = row.ItemDate,
        ReversalOf = row.ReversalOf, Reason = row.Reason, CreatedAt = row.CreatedAt, CreatedBy = row.CreatedBy,
    };
}

/// <summary><c>ri.Item.generate</c> as a command of the platform pipeline.</summary>
internal sealed record GenerateItems(ItemGenerateRequest Request) : ICommand<ItemGenerateResponse>;

/// <summary>Shape rules: none beyond a body.</summary>
internal sealed class GenerateItemsValidator : AbstractValidator<GenerateItems>
{
    public GenerateItemsValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>
/// Produces the technical account items not produced yet (BO-RI-11): the paid recoveries, the cession premium deltas, the deposit
/// instalments due up to the as-of date, and the approved adjustments and commission. Each amount is split over the participants of the
/// contract version in force on the item date with the shared cent allocator (NX-EC-06), so the parts sum exactly. A source produces its
/// item once (unique on kind, source and reinsurer), so running this again adds nothing.
/// </summary>
internal sealed class GenerateItemsHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<GenerateItems, ItemGenerateResponse>
{
    public async Task<Result<ItemGenerateResponse>> HandleAsync(GenerateItems command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var zone = options.Value.Zone;
        var now = clock.Now;
        var asOf = command.Request.AsOf ?? now.ToBusinessDate(zone);
        var contractIds = command.Request.ContractId is { } only
            ? [only]
            : await db.Contracts.AsNoTracking().Where(c => c.LegalEntityId == legalEntity).OrderBy(c => c.ContractId).Select(c => c.ContractId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var created = AccountItemKinds.All.ToDictionary(k => k, _ => 0);
        var skipped = 0;
        foreach (var contractId in contractIds)
        {
            var contract = await ContractSupport.LockAsync(db, legalEntity, contractId, cancellationToken).ConfigureAwait(false);
            if (contract is null)
            {
                if (command.Request.ContractId is not null)
                {
                    return RiErrors.NotFound();
                }

                continue;
            }

            var run = new Run(db, contract, legalEntity, now, context.Actor.ToString(), zone);
            await run.LoadAsync(cancellationToken).ConfigureAwait(false);
            skipped += await run.RecoveriesAsync(cancellationToken).ConfigureAwait(false);
            skipped += await run.CessionsAsync(cancellationToken).ConfigureAwait(false);
            skipped += await run.InstalmentsAsync(asOf, cancellationToken).ConfigureAwait(false);
            skipped += await run.AdjustmentsAsync(cancellationToken).ConfigureAwait(false);
            skipped += await run.CommissionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (kind, count) in run.Created)
            {
                created[kind] += count;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return AccountingGuards.State("Another run produced the same items; try again.");
            }
        }

        return new ItemGenerateResponse { Created = created.Values.Sum(), ByKind = created.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value), Skipped = skipped };
    }

    /// <summary>The generation of one contract.</summary>
    private sealed class Run(ReinsuranceDbContext db, ContractRow contract, LegalEntityId legalEntity, Instant now, string actor, TimeZoneInfo zone)
    {
        private readonly Dictionary<BusinessDate, IReadOnlyList<(Guid Party, decimal Weight)>?> participants = [];
        private HashSet<(string Kind, string SourceId, Guid Reinsurer)> existing = [];
        private List<AccountItemRow> booked = [];

        public Dictionary<string, int> Created { get; } = [];

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            booked = await db.AccountItems.AsNoTracking().Where(i => i.ContractId == contract.ContractId).ToListAsync(cancellationToken).ConfigureAwait(false);
            existing = [.. booked.Select(i => (i.Kind, i.SourceId, i.ReinsurerPartyId))];
        }

        /// <summary>Paid recoveries per participant (a negative amount: payable to the cedant).</summary>
        public async Task<int> RecoveriesAsync(CancellationToken cancellationToken)
        {
            var rows = await db.Recoveries.AsNoTracking().Where(r => r.ContractId == contract.ContractId.Value && r.PaidDelta != 0m).OrderBy(r => r.RecordedAt).ThenBy(r => r.RecoveryId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var date = r.RecordedAt.ToBusinessDate(zone);
                Add(AccountItemKinds.Recovery, "RECOVERY", r.RecoveryId.ToString("D"), r.RecoveryId.ToString("D"), r.LayerNo, r.ParticipantPartyId, -r.PaidDelta, date);
            }

            return 0;
        }

        /// <summary>Ceded premium deltas of the contract's treaty and year, split over the participants (cession rows are per treaty, not per participant).</summary>
        public async Task<int> CessionsAsync(CancellationToken cancellationToken)
        {
            var rows = await db.Cessions.AsNoTracking()
                .Where(c => c.LegalEntityId == legalEntity.Value && c.TreatyId == contract.StableTreatyId && c.ContractYear == contract.ContractYear && c.Delta != 0m)
                .OrderBy(c => c.RecordedAt).ThenBy(c => c.CessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var skipped = 0;
            foreach (var c in rows)
            {
                var date = c.RecordedAt.ToBusinessDate(zone);
                if (!await SplitAsync(AccountItemKinds.CededPremium, "CESSION", c.CessionId.ToString("D"), c.CessionId.ToString("D"), null, c.Delta, date, cancellationToken).ConfigureAwait(false))
                {
                    skipped++;
                }
            }

            return skipped;
        }

        /// <summary>The instalments due up to <paramref name="asOf"/> of each layer's current schedule; a reschedule posts the difference to what is booked.</summary>
        public async Task<int> InstalmentsAsync(BusinessDate asOf, CancellationToken cancellationToken)
        {
            var schedules = await db.DepositSchedules.AsNoTracking().Where(s => s.ContractId == contract.ContractId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var skipped = 0;
            foreach (var schedule in schedules.GroupBy(s => s.LayerNo).Select(g => g.OrderByDescending(s => s.Revision).First()))
            {
                foreach (var instalment in AccountingViews.ToView(schedule, current: true).Instalments.Where(i => i.DueDate <= asOf))
                {
                    var group = $"{schedule.LayerNo}:{instalment.Seq}";
                    if (booked.Any(i => i.Kind == AccountItemKinds.DepositInstalment && i.SourceGroup == group && i.ReversalOf is not null))
                    {
                        continue; // Reversed by hand: not produced again.
                    }

                    var split = await SplitAmountsAsync(instalment.Amount.Amount, instalment.DueDate, cancellationToken).ConfigureAwait(false);
                    if (split is null)
                    {
                        skipped++;
                        continue;
                    }

                    foreach (var (party, share) in split)
                    {
                        var already = booked.Where(i => i.Kind == AccountItemKinds.DepositInstalment && i.SourceGroup == group && i.ReinsurerPartyId == party).Sum(i => i.Amount);
                        Add(AccountItemKinds.DepositInstalment, "DEPOSIT_SCHEDULE", $"{schedule.ScheduleId:D}:{instalment.Seq}", group, schedule.LayerNo, party, share - already, instalment.DueDate);
                    }
                }
            }

            return skipped;
        }

        /// <summary>Approved adjustment premiums (additional positive, return negative).</summary>
        public async Task<int> AdjustmentsAsync(CancellationToken cancellationToken)
        {
            var rows = await (from a in db.Adjustments.AsNoTracking()
                              join d in db.AccountingDecisions.AsNoTracking() on a.AdjustmentId equals d.SubjectId
                              where a.ContractId == contract.ContractId && d.Kind == "ADJUSTMENT" && d.Decision == "APPROVED" && a.AdjustmentAmount != 0m
                              orderby d.DecidedAt, a.AdjustmentId
                              select new { Row = a, d.DecidedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var skipped = 0;
            foreach (var x in rows)
            {
                var id = x.Row.AdjustmentId.ToString("D");
                if (!await SplitAsync(AccountItemKinds.PremiumAdjustment, "ADJUSTMENT", id, id, x.Row.LayerNo, x.Row.AdjustmentAmount, x.DecidedAt.ToBusinessDate(zone), cancellationToken).ConfigureAwait(false))
                {
                    skipped++;
                }
            }

            return skipped;
        }

        /// <summary>Approved commission adjustments (more earned commission lowers what is payable: negative).</summary>
        public async Task<int> CommissionsAsync(CancellationToken cancellationToken)
        {
            var rows = await (from c in db.Commissions.AsNoTracking()
                              join d in db.AccountingDecisions.AsNoTracking() on c.CommissionId equals d.SubjectId
                              where c.ContractId == contract.ContractId && d.Kind == "COMMISSION" && d.Decision == "APPROVED" && c.AdjustmentAmount != 0m
                              orderby d.DecidedAt, c.CommissionId
                              select new { Row = c, d.DecidedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var skipped = 0;
            foreach (var x in rows)
            {
                var id = x.Row.CommissionId.ToString("D");
                if (!await SplitAsync(AccountItemKinds.Commission, "COMMISSION", id, id, null, -x.Row.AdjustmentAmount, x.DecidedAt.ToBusinessDate(zone), cancellationToken).ConfigureAwait(false))
                {
                    skipped++;
                }
            }

            return skipped;
        }

        private async Task<bool> SplitAsync(string kind, string sourceKind, string sourceId, string group, int? layer, decimal amount, BusinessDate date, CancellationToken cancellationToken)
        {
            if (kind != AccountItemKinds.Recovery && existing.Any(e => e.Kind == kind && e.SourceId == sourceId))
            {
                return true;
            }

            var split = await SplitAmountsAsync(amount, date, cancellationToken).ConfigureAwait(false);
            if (split is null)
            {
                return false;
            }

            foreach (var (party, share) in split)
            {
                Add(kind, sourceKind, sourceId, group, layer, party, share, date);
            }

            return true;
        }

        /// <summary>The amount over the participants of the version in force on the date, or null when none is.</summary>
        private async Task<IReadOnlyList<(Guid Party, decimal Share)>?> SplitAmountsAsync(decimal amount, BusinessDate date, CancellationToken cancellationToken)
        {
            if (!participants.TryGetValue(date, out var lines))
            {
                var version = await ContractVersions.InForceAsync(db, contract.ContractId, date, now, approvedOnly: true, cancellationToken).ConfigureAwait(false);
                if (version is null)
                {
                    lines = null;
                }
                else
                {
                    var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
                    lines = [.. content.Lines.OrderByDescending(l => l.Lead).ThenBy(l => l.ReinsurerPartyId).Select(l => (l.ReinsurerPartyId, l.SignedLinePct))];
                }

                participants[date] = lines;
            }

            if (lines is not { Count: > 0 })
            {
                return null;
            }

            var shares = CentAllocation.Allocate(amount, [.. lines.Select(l => l.Weight)]);
            return [.. lines.Select((l, i) => (l.Party, shares[i]))];
        }

        private void Add(string kind, string sourceKind, string sourceId, string group, int? layer, Guid reinsurer, decimal amount, BusinessDate date)
        {
            if (amount == 0m || !existing.Add((kind, sourceId, reinsurer)))
            {
                return;
            }

            var row = new AccountItemRow
            {
                ItemId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ContractId = contract.ContractId, ContractYear = contract.ContractYear, LayerNo = layer,
                ReinsurerPartyId = reinsurer, Kind = kind, SourceKind = sourceKind, SourceId = sourceId, SourceGroup = group, Currency = contract.Currency.Trim(), Amount = amount,
                Period = AccountItemViews.PeriodOf(date), ItemDate = date, CreatedAt = now, CreatedBy = actor,
            };
            db.AccountItems.Add(row);
            booked.Add(row);
            Created[kind] = Created.GetValueOrDefault(kind) + 1;
        }
    }
}

/// <summary>Audit facts of <c>ri.Item.generate</c>.</summary>
internal sealed class GenerateItemsAuditor : ICommandAuditor<GenerateItems, ItemGenerateResponse>
{
    public CommandAuditFacts Describe(GenerateItems command, Result<ItemGenerateResponse>? result)
    {
        var contract = command.Request.ContractId;
        var keys = contract is { } id ? BusinessKeys.Empty.With("riContractId", id.Value.ToString("D")) : BusinessKeys.Empty;
        return new CommandAuditFacts
        {
            ObjectRef = contract is { } c ? ObjectRef.For(ModuleCode.RI, "Contract", c) : new ObjectRef(ModuleCode.RI, "AccountItems", "all"),
            BusinessKeys = keys,
            Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(new { created = 0 }, new { created = ok.Value.Created, skipped = ok.Value.Skipped }) : AuditDiff.Compute(new { created = 0 }, new { created = 0 }),
        };
    }
}

/// <summary><c>ri.Item.reverse</c> as a command of the platform pipeline.</summary>
internal sealed record ReverseItem(ItemReverseRequest Request) : ICommand<ItemReverseResponse>;

/// <summary>Shape rules: a reversal needs its reason.</summary>
internal sealed class ReverseItemValidator : AbstractValidator<ReverseItem>
{
    public ReverseItemValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Reverses an item by appending the opposite one in the current period (the ledger is never edited). An item is reversed once, a
/// reversal is not reversed, and the generator does not produce the reversed source again.
/// </summary>
internal sealed class ReverseItemHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<ReverseItem, ItemReverseResponse>
{
    public async Task<Result<ItemReverseResponse>> HandleAsync(ReverseItem command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var item = await db.AccountItems.AsNoTracking().SingleOrDefaultAsync(i => i.ItemId == request.ItemId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return AccountingGuards.NotFound("account item");
        }

        await ContractSupport.LockAsync(db, legalEntity, item.ContractId, cancellationToken).ConfigureAwait(false);
        if (item.ReversalOf is not null)
        {
            return AccountingGuards.State("A reversal cannot be reversed.");
        }

        if (await db.AccountItems.AnyAsync(i => i.ReversalOf == item.ItemId, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State("The item was already reversed.");
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var reversal = new AccountItemRow
        {
            ItemId = Guid.CreateVersion7(), LegalEntityId = legalEntity, ContractId = item.ContractId, ContractYear = item.ContractYear, LayerNo = item.LayerNo,
            ReinsurerPartyId = item.ReinsurerPartyId, Kind = item.Kind, SourceKind = "REVERSAL", SourceId = item.ItemId.ToString("D"), SourceGroup = item.SourceGroup,
            Currency = item.Currency, Amount = -item.Amount, Period = AccountItemViews.PeriodOf(today), ItemDate = today, ReversalOf = item.ItemId, Reason = request.Reason,
            CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.AccountItems.Add(reversal);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("The item was already reversed.");
        }

        return new ItemReverseResponse { Reversal = AccountItemViews.ToView(reversal) };
    }
}

/// <summary>Audit facts of <c>ri.Item.reverse</c>.</summary>
internal sealed class ReverseItemAuditor : ICommandAuditor<ReverseItem, ItemReverseResponse>
{
    public CommandAuditFacts Describe(ReverseItem command, Result<ItemReverseResponse>? result)
    {
        var subject = new ObjectRef(ModuleCode.RI, "AccountItem", command.Request.ItemId.ToString("D"));
        var keys = BusinessKeys.Empty.With("accountItemId", command.Request.ItemId.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            BusinessKeys = keys.With("riContractId", ok.Value.Reversal.ContractId.Value.ToString("D")),
            Changes = AuditDiff.Compute(new { reversed = false }, new { reversed = true, reversalId = ok.Value.Reversal.ItemId.ToString("D"), reason = command.Request.Reason }),
        };
    }
}

/// <summary><c>ri.Item.list</c> and <c>ri.Balance.report</c>: the technical account as stored.</summary>
internal sealed class AccountItemReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<Result<ItemListPage>> ListAsync(
        Guid? contractId, Guid? reinsurerPartyId, string? kind, string? period, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        var offset = 0;
        if (cursor is not null && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
        }

        if (kind is not null && !AccountItemKinds.All.Contains(kind))
        {
            return RiErrors.Validation("kind", "KIND", "The kind is not known.");
        }

        if (period is not null && !AccountItemViews.IsPeriod(period))
        {
            return RiErrors.Validation("period", "PERIOD", "The period is yyyy-MM.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var query = db.AccountItems.AsNoTracking().Where(i => i.LegalEntityId == legalEntity);
        if (contractId is { } contract)
        {
            var typed = new RiContractId(contract);
            query = query.Where(i => i.ContractId == typed);
        }

        if (reinsurerPartyId is { } party)
        {
            query = query.Where(i => i.ReinsurerPartyId == party);
        }

        if (kind is not null)
        {
            query = query.Where(i => i.Kind == kind);
        }

        if (period is not null)
        {
            query = query.Where(i => i.Period == period);
        }

        var rows = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.ItemId).Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ItemListPage
        {
            Items = [.. rows.Take(size).Select(r => new ItemListItem { Item = AccountItemViews.ToView(r) })],
            NextCursor = rows.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null,
            Limit = size,
        };
    }

    /// <summary>
    /// Balances per reinsurer, currency and period. The running balance carries every earlier period, so a report from a later period still
    /// opens with the right figure. A reversal nets its original in the period it is posted in.
    /// </summary>
    public async Task<Result<BalanceReportResponse>> ReportAsync(BalanceReportRequest request, CancellationToken cancellationToken)
    {
        if (request.PeriodFrom is not null && !AccountItemViews.IsPeriod(request.PeriodFrom) || request.PeriodTo is not null && !AccountItemViews.IsPeriod(request.PeriodTo))
        {
            return RiErrors.Validation("period", "PERIOD", "The period is yyyy-MM.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var query = db.AccountItems.AsNoTracking().Where(i => i.LegalEntityId == legalEntity);
        if (request.ContractId is { } contract)
        {
            query = query.Where(i => i.ContractId == contract);
        }

        if (request.ReinsurerPartyId is { } party)
        {
            query = query.Where(i => i.ReinsurerPartyId == party.Value);
        }

        if (request.PeriodTo is { } to)
        {
            query = query.Where(i => string.Compare(i.Period, to, StringComparison.Ordinal) <= 0);
        }

        var items = await query.Select(i => new { i.ReinsurerPartyId, i.Currency, i.Period, i.Kind, i.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<BalanceLine>();
        foreach (var account in items.GroupBy(i => (i.ReinsurerPartyId, Currency: i.Currency.Trim())).OrderBy(g => g.Key.ReinsurerPartyId).ThenBy(g => g.Key.Currency))
        {
            var running = 0m;
            foreach (var period in account.GroupBy(i => i.Period).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var premium = period.Where(i => AccountItemKinds.IsPremium(i.Kind)).Sum(i => i.Amount);
                var commission = period.Where(i => i.Kind == AccountItemKinds.Commission).Sum(i => i.Amount);
                var recoveries = period.Where(i => i.Kind == AccountItemKinds.Recovery).Sum(i => i.Amount);
                var movement = premium + commission + recoveries;
                running += movement;
                if (request.PeriodFrom is { } from && string.CompareOrdinal(period.Key, from) < 0)
                {
                    continue;
                }

                lines.Add(new BalanceLine
                {
                    ReinsurerPartyId = new PartyId(account.Key.ReinsurerPartyId), Currency = account.Key.Currency, Period = period.Key,
                    Premium = AccountingViews.Eur(premium, account.Key.Currency), Commission = AccountingViews.Eur(commission, account.Key.Currency),
                    Recoveries = AccountingViews.Eur(recoveries, account.Key.Currency), Movement = AccountingViews.Eur(movement, account.Key.Currency),
                    Closing = AccountingViews.Eur(running, account.Key.Currency), ItemCount = period.Count(),
                });
            }
        }

        return new BalanceReportResponse { Balances = lines };
    }
}
