using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using AuthorityCheckRequest = CoreIns.Platform.Authority.AuthorityCheckRequest;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands.FriendlySettlement;

/// <summary><c>clm.FriendlySettlement.importStatement</c> (REQ-CLM-160): the month's statement from the clearing SPI, matched at once.</summary>
internal sealed record ImportFsStatement(FsImportStatementRequest Request) : ICommand<FsImportStatementResponse>;

/// <summary><c>clm.FriendlySettlement.approveNet</c> (REQ-CLM-160, -264): approves one counterparty's net and hands it to BIL.</summary>
internal sealed record ApproveFsNet(FsApproveNetRequest Request) : ICommand<FsApproveNetResponse>;

internal sealed class ImportFsStatementValidator : AbstractValidator<ImportFsStatement>
{
    public ImportFsStatementValidator() => RuleFor(c => c.Request.Period).Must(p => p.End is not null);
}

internal sealed class ApproveFsNetValidator : AbstractValidator<ApproveFsNet>
{
    public ApproveFsNetValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.CounterpartyCode).NotEmpty();
        RuleFor(c => c.Request.ExpectedNet.Currency.Code).Equal("EUR");
    }
}

/// <summary>
/// Imports the statement of a calendar month through the SPI (REQ-CLM-160). Per counterparty one net row is created (or reused),
/// each line is stored once (re-importing is safe: matched lines stay, unmatched ones are retried) and matched at once by the clearing
/// reference. A matched own line records the FS recovery, a matched at-fault line issues the CLEARING payment; every other line is an
/// exception that blocks its counterparty's net. A row whose net is already approved or handed over is not changed.
/// </summary>
internal sealed class ImportFsStatementHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, FsGate gate, FsStatementMatcher matcher)
    : ICommandHandler<ImportFsStatement, FsImportStatementResponse>
{
    public async Task<Result<FsImportStatementResponse>> HandleAsync(ImportFsStatement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var entity = protection.Current(context);
        if (!FsStatements.TryMonth(request.Period, out var firstDay))
        {
            return FnolAssessment.Invalid("period", "PERIOD", "The period must be one calendar month, from its first day to the first day of the next month.");
        }

        if (request.CounterpartyCode is not null && !FsCases.TryParty(request.CounterpartyCode, out _))
        {
            return FnolAssessment.Invalid("counterpartyCode", "COUNTERPARTY", "The counterparty code must be an insurer party id.");
        }

        var spi = await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (spi.IsFailure)
        {
            return spi.Error!;
        }

        FsSettlementStatement statement;
        try
        {
            statement = await spi.Value.SettlementStatementAsync(new FsStatementPeriod(firstDay, firstDay.AddMonths(1)), request.CounterpartyCode, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex)
        {
            return FsCases.FromSpi(ex);
        }

        foreach (var line in statement.Lines)
        {
            if (!FsCases.TryParty(line.CounterpartyCode, out _) || line.Amount.Currency != "EUR" || line.Amount.Amount <= 0m || string.IsNullOrWhiteSpace(line.ClearingReference)
                || string.IsNullOrWhiteSpace(line.LineRef))
            {
                return FnolAssessment.Invalid("lines", "STATEMENT_LINE", "A statement line needs an insurer counterparty, a positive EUR amount, a clearing reference and a line reference.");
            }
        }

        var now = clock.Now;
        var period = FsStatements.PeriodCode(firstDay);
        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        var rows = new Dictionary<PartyId, FsStatementRow>();
        foreach (var party in statement.Lines.Select(l => { FsCases.TryParty(l.CounterpartyCode, out var p); return p; }).Distinct())
        {
            var row = await db.FsStatements.SingleOrDefaultAsync(s => s.LegalEntityId == entity && s.Period == period && s.CounterpartyInsurerPartyId == party, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                row = new FsStatementRow
                {
                    FsStatementId = Guid.CreateVersion7(), Period = period, CounterpartyInsurerPartyId = party, ExternalReference = statement.StatementId,
                    NetAmount = 0m, Currency = "EUR", Direction = FsStatements.Receivable, Status = FsStatements.Pending,
                    LegalEntityId = entity, Jurisdiction = jurisdiction, CreatedAt = now, CreatedBy = context.Actor.ToString(), UpdatedAt = now,
                };
                db.FsStatements.Add(row);
            }

            rows[party] = row;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ClaimSupport.IsUniqueViolation(ex, "ux_fs_statement_period_counterparty"))
        {
            return ClaimSupport.Stale();
        }

        var touched = new HashSet<Guid>();
        try
        {
            foreach (var spiLine in statement.Lines)
            {
                FsCases.TryParty(spiLine.CounterpartyCode, out var party);
                var row = rows[party];
                var direction = spiLine.Direction == FsDirection.Payable ? FsStatements.Payable : FsStatements.Receivable;
                var existing = await db.FsStatementLines.FirstOrDefaultAsync(
                    l => l.FsStatementId == row.FsStatementId && l.ClearingReference == spiLine.ClearingReference && l.Direction == direction, cancellationToken).ConfigureAwait(false);
                if (!FsStatements.IsOpen(row) || (existing is not null && FsStatements.IsMatched(existing)))
                {
                    continue; // frozen net (approved or handed over) or a line that already matched: nothing to redo.
                }

                if (existing is null)
                {
                    var reference = spiLine.LineRef;
                    if (await db.FsStatementLines.AnyAsync(l => l.FsStatementId == row.FsStatementId && l.ExternalReference == reference, cancellationToken).ConfigureAwait(false))
                    {
                        reference = $"{spiLine.LineRef}/{spiLine.ClearingReference}"; // the clearing house renumbered its lines
                    }

                    existing = new FsStatementLineRow
                    {
                        FsStatementLineId = Guid.CreateVersion7(), FsStatementId = row.FsStatementId, ExternalReference = reference, ClearingReference = spiLine.ClearingReference,
                        Direction = direction, Amount = spiLine.Amount.Amount, Currency = "EUR", MatchStatus = "UNMATCHED",
                        LegalEntityId = entity, Jurisdiction = jurisdiction, CreatedAt = now, CreatedBy = context.Actor.ToString(), UpdatedAt = now,
                    };
                    db.FsStatementLines.Add(existing);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                await matcher.MatchAsync(existing, row, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                touched.Add(row.FsStatementId);
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        foreach (var row in rows.Values.Where(r => touched.Contains(r.FsStatementId)))
        {
            var lines = await db.FsStatementLines.AsNoTracking().Where(l => l.FsStatementId == row.FsStatementId).ToListAsync(cancellationToken).ConfigureAwait(false);
            (row.NetAmount, row.Direction) = FsStatements.NetOf(lines);
            row.UpdatedAt = now;
            row.RecordVersion++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var anchor = rows.Values.OrderBy(r => r.CreatedAt).ThenBy(r => r.FsStatementId).FirstOrDefault();
        if (anchor is null)
        {
            return ClaimSupport.NotFound("clearing statement lines of the period");
        }

        return new FsImportStatementResponse { Statement = (await FsStatements.ViewAsync(db, entity, anchor.FsStatementId, cancellationToken).ConfigureAwait(false))! };
    }
}

/// <summary>
/// Approves the net of one counterparty (REQ-CLM-160, -264; D-SL4-12). Every line of the counterparty must be matched; the net is
/// recomputed from the stored lines and must equal <c>expectedNet</c> (never taken from the client). <c>CLM.FS_NET_SETTLEMENT</c>
/// decides: within the approver's authority the net goes to BIL at once (Requested); above it a PLT approval is requested (the net
/// stays Pending with the approval, and BIL is called when it is approved). A net of zero settles without cash.
/// </summary>
internal sealed class ApproveFsNetHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, FsGate gate, IAuthorityService authority,
    IPlatformApprovalService approvals, FsNetHandOver handOver, FsNetSettler settler) : ICommandHandler<ApproveFsNet, FsApproveNetResponse>
{
    private const decimal FourEyesAbove = 50000m;

    public async Task<Result<FsApproveNetResponse>> HandleAsync(ApproveFsNet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var entity = protection.Current(context);
        var gateResult = await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (gateResult.IsFailure)
        {
            return gateResult.Error!;
        }

        var rows = await FsStatements.RowsAsync(db, entity, request.FsStatementId, cancellationToken).ConfigureAwait(false);
        var code = request.CounterpartyCode.ToLowerInvariant();
        var target = rows.FirstOrDefault(r => FsCases.Code(r.CounterpartyInsurerPartyId) == code);
        if (target is null)
        {
            return ClaimSupport.NotFound("statement net");
        }

        var version = rows.Sum(r => r.RecordVersion);
        if (version != request.ExpectedRecordVersion)
        {
            return ClaimSupport.Stale(version);
        }

        var locked = await db.FsStatements.FromSql($"SELECT * FROM clm.fs_statement WHERE fs_statement_id = {target.FsStatementId} AND legal_entity_id = {entity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var row = locked[0];
        if (!FsStatements.IsOpen(row))
        {
            return DomainError.Of(ModuleCode.CLM, "ILLEGAL-TRANSITION", "Only a Pending net without an open approval can be approved.");
        }

        var lines = await db.FsStatementLines.Where(l => l.FsStatementId == row.FsStatementId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0 || lines.Any(l => !FsStatements.IsMatched(l)))
        {
            return DomainError.Of(ModuleCode.CLM, "ALLOCATION", "Unmatched or differing lines remain for the counterparty; resolve them before approving the net.");
        }

        var (amount, direction) = FsStatements.NetOf(lines);
        if (amount != request.ExpectedNet.Amount || amount != row.NetAmount || direction != row.Direction)
        {
            return DomainError.Of(ModuleCode.CLM, "STALE", $"The computed net is {direction} {amount:0.00}, not the net that was approved.");
        }

        var now = clock.Now;
        if (amount == 0m)
        {
            if (!context.DryRun)
            {
                await settler.SettleAsync(row, cancellationToken).ConfigureAwait(false);
            }

            return await ResponseAsync(entity, row, FsApproveNetResponse.OutcomeValue.Requested, cancellationToken).ConfigureAwait(false);
        }

        var subject = DisbursementApproval.FsNetSubject(FsStatements.Ref(row.FsStatementId));
        var check = await authority.CheckAsync(new AuthorityCheckRequest(
            context.Actor, context.Roles, ClaimsAuthorityTypes.FsNetSettlement,
            new Dictionary<string, DimensionValue>(StringComparer.Ordinal) { [ClaimsAuthorityTypes.AmountDimension] = DimensionValue.Of(ClaimMoney.Eur(amount)) },
            subject, now), cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision == AuthorityDecision.Allow && amount > FourEyesAbove)
        {
            check = check with { Decision = AuthorityDecision.Refer, ReasonCode = "FOUR_EYES_REQUIRED", ReferralTargets = [new ReferralTarget("ROLE", "Staff.ClaimsManager")] };
        }

        if (check.Decision == AuthorityDecision.Deny)
        {
            return DomainError.Of(ModuleCode.CLM, "AUTHORITY", $"{check.Type} on {amount:0.00}: {check.ReasonCode}.");
        }

        if (check.Decision == AuthorityDecision.Refer)
        {
            if (context.DryRun)
            {
                return await ResponseAsync(entity, row, FsApproveNetResponse.OutcomeValue.PendingApproval, cancellationToken).ConfigureAwait(false);
            }

            var role = check.ReferralTargets.FirstOrDefault(t => t.Kind == "ROLE")?.Id ?? check.ReferralTargets[0].Id;
            var response = await approvals.RequestAsync(new ApprovalRequestRequest
            {
                Type = DisbursementApproval.FsNetType,
                ObjectRef = subject,
                PayloadHash = FsNetHandOver.ContentHash(row, direction == FsStatements.Payable ? handOver.PayeeAccountOf(row) : null),
                Authority = new ApprovalAuthority { Type = ClaimsAuthorityTypes.FsNetSettlement.Value, Amount = ClaimMoney.Eur(amount) },
                ReferralRole = role,
                Reason = $"Friendly Settlement {row.Period} net {direction} {amount:0.00} EUR with counterparty {code} is above the approver's authority ({check.ReasonCode}).",
            }, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            row.ApprovalRequestId = new ApprovalRequestId(response.Request.RequestId);
            row.UpdatedAt = now;
            row.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return await ResponseAsync(entity, row, FsApproveNetResponse.OutcomeValue.PendingApproval, cancellationToken).ConfigureAwait(false);
        }

        if (context.DryRun)
        {
            return await ResponseAsync(entity, row, FsApproveNetResponse.OutcomeValue.Requested, cancellationToken).ConfigureAwait(false);
        }

        var requested = await handOver.RequestAsync(row, $"CLM/{DisbursementApproval.FsNetSubjectType}/{FsStatements.Ref(row.FsStatementId)}", cancellationToken).ConfigureAwait(false);
        if (requested.IsFailure)
        {
            return requested.Error!;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ResponseAsync(entity, row, FsApproveNetResponse.OutcomeValue.Requested, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FsApproveNetResponse> ResponseAsync(LegalEntityId entity, FsStatementRow row, FsApproveNetResponse.OutcomeValue outcome, CancellationToken cancellationToken)
    {
        var view = (await FsStatements.ViewAsync(db, entity, row.FsStatementId, cancellationToken).ConfigureAwait(false))!;
        var code = FsCases.Code(row.CounterpartyInsurerPartyId);
        return new FsApproveNetResponse { Statement = view, Net = view.Nets.Single(n => n.CounterpartyCode == code), Outcome = outcome };
    }
}

internal sealed class ImportFsStatementAuditor : ICommandAuditor<ImportFsStatement, FsImportStatementResponse>
{
    public CommandAuditFacts Describe(ImportFsStatement command, Result<FsImportStatementResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.CLM, "FsStatement", ok.Value.Statement.FsStatementId.ToString("D")),
                BusinessKeys = BusinessKeys.Empty.With("fsStatementId", ok.Value.Statement.FsStatementId.ToString("D")),
                Changes = AuditDiff.Compute(null, new { lines = ok.Value.Statement.Lines.Count, exceptions = ok.Value.Statement.ExceptionCount }),
            }
            : new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.CLM, "FsStatement", Guid.Empty.ToString("D")) };
}

internal sealed class ApproveFsNetAuditor : ICommandAuditor<ApproveFsNet, FsApproveNetResponse>
{
    public CommandAuditFacts Describe(ApproveFsNet command, Result<FsApproveNetResponse>? result) =>
        new()
        {
            ObjectRef = new ObjectRef(ModuleCode.CLM, "FsStatement", command.Request.FsStatementId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("fsStatementId", command.Request.FsStatementId.ToString("D")).With("counterpartyCode", command.Request.CounterpartyCode),
            Changes = AuditDiff.Compute(null, new
            {
                outcome = result is { IsSuccess: true } ok ? ok.Value.Outcome.ToString() : null,
                net = result is { IsSuccess: true } done ? done.Value.Net.Amount.ToString() : null,
            }),
        };
}
