using System.Globalization;
using System.Text;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Accounting;

/// <summary>The statuses of a reinsurer statement (BO-RI-14).</summary>
internal static class StatementStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Issued = "ISSUED";
    public const string Agreed = "AGREED";
    public const string Disputed = "DISPUTED";
    public const string Superseded = "SUPERSEDED";
}

/// <summary>Building, reading and mapping statements: the content is derived from the technical account items only.</summary>
internal static class StatementSupport
{
    /// <summary>
    /// The statement content of a reinsurer, period and currency: the items of the period are the lines, the items of the earlier periods the
    /// opening balance (the same arithmetic as the balance report, so the closing agrees with it). Reversals net automatically.
    /// </summary>
    public static async Task<(StatementRow Header, List<StatementLineRow> Lines)?> BuildAsync(
        ReinsuranceDbContext db, LegalEntityId legalEntity, Guid reinsurer, string period, string currency, CancellationToken cancellationToken)
    {
        var items = await db.AccountItems.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.ReinsurerPartyId == reinsurer && i.Currency == currency && i.Period.CompareTo(period) <= 0)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var mine = items.Where(i => i.Period == period).OrderBy(i => i.ItemDate.Value).ThenBy(i => i.ItemId).ToList();
        if (mine.Count == 0)
        {
            return null;
        }

        var statementId = Guid.CreateVersion7();
        var opening = items.Where(i => i.Period != period).Sum(i => i.Amount);
        var premium = mine.Where(i => AccountItemKinds.IsPremium(i.Kind)).Sum(i => i.Amount);
        var commission = mine.Where(i => i.Kind == AccountItemKinds.Commission).Sum(i => i.Amount);
        var recoveries = mine.Where(i => i.Kind == AccountItemKinds.Recovery).Sum(i => i.Amount);
        var movement = premium + commission + recoveries;
        var lines = mine.Select((i, n) => new StatementLineRow
        {
            StatementId = statementId, LineNo = n + 1, ItemId = i.ItemId, ContractId = i.ContractId, Kind = i.Kind, SourceKind = i.SourceKind, SourceId = i.SourceId,
            ItemDate = i.ItemDate, Amount = i.Amount,
        }).ToList();
        var header = new StatementRow
        {
            StatementId = statementId, LegalEntityId = legalEntity, ReinsurerPartyId = reinsurer, Period = period, Currency = currency, Opening = opening, Premium = premium,
            Commission = commission, Recoveries = recoveries, Movement = movement, Closing = opening + movement, LineCount = lines.Count,
        };
        header.ContentHash = HashOf(header, lines);
        return (header, lines);
    }

    /// <summary>The SHA-256 of the sealed content (header figures and every line in order), independent of ids that change per version.</summary>
    public static string HashOf(StatementRow header, IReadOnlyList<StatementLineRow> lines)
    {
        var text = new StringBuilder();
        text.Append(header.ReinsurerPartyId.ToString("D")).Append('|').Append(header.Period).Append('|').Append(header.Currency).Append('|')
            .Append(AccountingViews.Fixed(header.Opening)).Append('|').Append(AccountingViews.Fixed(header.Premium)).Append('|').Append(AccountingViews.Fixed(header.Commission)).Append('|')
            .Append(AccountingViews.Fixed(header.Recoveries)).Append('|').Append(AccountingViews.Fixed(header.Closing));
        foreach (var line in lines)
        {
            text.Append('\n').Append(line.ItemId.ToString("D")).Append('|').Append(line.Kind).Append('|').Append(line.ContractId.Value.ToString("D")).Append('|')
                .Append(line.ItemDate.ToString()).Append('|').Append(AccountingViews.Fixed(line.Amount));
        }

        return Sha256Hash.ComputeUtf8(text.ToString()).Value;
    }

    /// <summary>The statement row, tracked and row-locked until the command's transaction ends; null when not found in the legal entity.</summary>
    public static async Task<StatementRow?> LockAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, Guid statementId, CancellationToken cancellationToken)
    {
        var rows = await db.Statements
            .FromSql($"SELECT * FROM ri.statement WHERE statement_id = {statementId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static async Task<StatementView> ViewAsync(ReinsuranceDbContext db, StatementRow row, CancellationToken cancellationToken)
    {
        var lines = await db.StatementLines.AsNoTracking().Where(l => l.StatementId == row.StatementId).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToView(row, lines);
    }

    public static StatementView ToView(StatementRow row, IReadOnlyList<StatementLineRow> lines) => new()
    {
        StatementId = row.StatementId, ReinsurerPartyId = new PartyId(row.ReinsurerPartyId), Period = row.Period, Currency = row.Currency.Trim(), Version = row.Version,
        PreviousStatementId = row.PreviousStatementId, Status = Enum.Parse<StatementView.StatusValue>(row.Status, ignoreCase: true),
        Opening = AccountingViews.Eur(row.Opening, row.Currency), Premium = AccountingViews.Eur(row.Premium, row.Currency), Commission = AccountingViews.Eur(row.Commission, row.Currency),
        Recoveries = AccountingViews.Eur(row.Recoveries, row.Currency), Movement = AccountingViews.Eur(row.Movement, row.Currency), Closing = AccountingViews.Eur(row.Closing, row.Currency),
        ContentHash = row.ContentHash.Trim(),
        Lines =
        [
            .. lines.Select(l => new StatementLine
            {
                ItemId = l.ItemId, ContractId = l.ContractId, Kind = Enum.Parse<StatementLine.KindValue>(l.Kind.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
                SourceKind = l.SourceKind, SourceId = l.SourceId, ItemDate = l.ItemDate, Amount = AccountingViews.Eur(l.Amount, row.Currency),
            }),
        ],
        CreatedAt = row.CreatedAt, CreatedBy = row.CreatedBy, SubmittedAt = row.SubmittedAt, SubmittedBy = row.SubmittedBy, ApprovedAt = row.ApprovedAt, ApprovedBy = row.ApprovedBy,
        IssuedAt = row.IssuedAt, IssuedBy = row.IssuedBy, Response = row.Response is null ? null : Enum.Parse<StatementView.ResponseValue>(row.Response, ignoreCase: true),
        ResponseReason = row.ResponseReason, RespondedAt = row.RespondedAt, RespondedBy = row.RespondedBy, SupersededBy = row.SupersededBy,
    };

    public static CommandAuditFacts Facts(Guid statementId, Guid? reinsurer, object before, object after, bool ok) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.RI, "Statement", statementId.ToString("D")),
        BusinessKeys = reinsurer is { } party
            ? BusinessKeys.Empty.With("statementId", statementId.ToString("D")).With("reinsurerPartyId", party.ToString("D"))
            : BusinessKeys.Empty.With("statementId", statementId.ToString("D")),
        Changes = ok ? AuditDiff.Compute(before, after) : AuditDiff.Compute(before, before),
    };

    public static DomainError Wrong(StatementRow row, string action) => AccountingGuards.State($"The statement is {row.Status}; it cannot be {action}.");
}

/// <summary><c>ri.Statement.generate</c> as a command of the platform pipeline.</summary>
internal sealed record GenerateStatement(StatementGenerateRequest Request) : ICommand<StatementGenerateResponse>;

/// <summary>Shape rules: the period is yyyy-MM.</summary>
internal sealed class GenerateStatementValidator : AbstractValidator<GenerateStatement>
{
    public GenerateStatementValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Period).Must(AccountItemViews.IsPeriod).WithErrorCode("PERIOD").When(c => c.Request is not null);
    }
}

/// <summary>
/// Generates the DRAFT statement (version 1) of a reinsurer and period from the technical account items, sealing the lines, totals and hash. A live
/// statement of the same reinsurer, period and currency exists once: a correction is a reissue, never a second generation.
/// </summary>
internal sealed class GenerateStatementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<GenerateStatement, StatementGenerateResponse>
{
    public async Task<Result<StatementGenerateResponse>> HandleAsync(GenerateStatement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var currency = request.Currency?.Code ?? "EUR";
        var party = request.ReinsurerPartyId.Value;
        if (await db.Statements.AnyAsync(s => s.LegalEntityId == legalEntity && s.ReinsurerPartyId == party && s.Period == request.Period && s.Currency == currency && s.Status != StatementStatus.Superseded, cancellationToken).ConfigureAwait(false))
        {
            return AccountingGuards.State("A statement of this reinsurer, period and currency exists; reissue it after a reversal or adjustment.");
        }

        var built = await StatementSupport.BuildAsync(db, legalEntity, party, request.Period, currency, cancellationToken).ConfigureAwait(false);
        if (built is null)
        {
            return AccountingGuards.State("The reinsurer has no account items in the period.");
        }

        var (header, lines) = built.Value;
        header.Version = 1;
        header.Status = StatementStatus.Draft;
        header.CreatedAt = clock.Now;
        header.CreatedBy = context.Actor.ToString();
        db.Statements.Add(header);
        db.StatementLines.AddRange(lines);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AccountingGuards.State("Another request generated the statement; try again.");
        }

        return new StatementGenerateResponse { Statement = StatementSupport.ToView(header, lines) };
    }
}

/// <summary>Audit facts of <c>ri.Statement.generate</c>.</summary>
internal sealed class GenerateStatementAuditor : ICommandAuditor<GenerateStatement, StatementGenerateResponse>
{
    public CommandAuditFacts Describe(GenerateStatement command, Result<StatementGenerateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? StatementSupport.Facts(
                ok.Value.Statement.StatementId, command.Request.ReinsurerPartyId.Value, new { status = "NONE" },
                new { status = "DRAFT", period = command.Request.Period, closing = ok.Value.Statement.Closing.Amount.ToString(CultureInfo.InvariantCulture), hash = ok.Value.Statement.ContentHash }, true)
            : new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.RI, "Statement", "new"),
                BusinessKeys = BusinessKeys.Empty.With("reinsurerPartyId", command.Request.ReinsurerPartyId.Value.ToString("D")),
            };
}

/// <summary><c>ri.Statement.submit</c> as a command of the platform pipeline.</summary>
internal sealed record SubmitStatement(StatementSubmitRequest Request) : ICommand<StatementSubmitResponse>;

/// <summary>Shape rules: a body.</summary>
internal sealed class SubmitStatementValidator : AbstractValidator<SubmitStatement>
{
    public SubmitStatementValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>Sends a DRAFT statement to the checker.</summary>
internal sealed class SubmitStatementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<SubmitStatement, StatementSubmitResponse>
{
    public async Task<Result<StatementSubmitResponse>> HandleAsync(SubmitStatement command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await StatementSupport.LockAsync(db, legalEntity, command.Request.StatementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("statement");
        }

        if (row.Status != StatementStatus.Draft)
        {
            return StatementSupport.Wrong(row, "submitted");
        }

        row.Status = StatementStatus.Submitted;
        row.SubmittedAt = clock.Now;
        row.SubmittedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new StatementSubmitResponse { Statement = await StatementSupport.ViewAsync(db, row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.Statement.submit</c>.</summary>
internal sealed class SubmitStatementAuditor : ICommandAuditor<SubmitStatement, StatementSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitStatement command, Result<StatementSubmitResponse>? result) =>
        StatementSupport.Facts(command.Request.StatementId, null, new { status = "DRAFT" }, new { status = "SUBMITTED" }, result is { IsSuccess: true });
}

/// <summary><c>ri.Statement.approve</c> as a command of the platform pipeline.</summary>
internal sealed record ApproveStatement(StatementApproveRequest Request) : ICommand<StatementApproveResponse>;

/// <summary>Shape rules: a body.</summary>
internal sealed class ApproveStatementValidator : AbstractValidator<ApproveStatement>
{
    public ApproveStatementValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>Maker-checker: a SUBMITTED statement is approved by someone who neither generated nor submitted it (<c>RI-ERR-SOD</c>).</summary>
internal sealed class ApproveStatementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ApproveStatement, StatementApproveResponse>
{
    public async Task<Result<StatementApproveResponse>> HandleAsync(ApproveStatement command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await StatementSupport.LockAsync(db, legalEntity, command.Request.StatementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("statement");
        }

        if (row.Status != StatementStatus.Submitted)
        {
            return StatementSupport.Wrong(row, "approved");
        }

        var keys = ContractSupport.ActorKeys(context);
        if (keys.Contains(row.CreatedBy, StringComparer.Ordinal) || row.SubmittedBy is { } submitter && keys.Contains(submitter, StringComparer.Ordinal))
        {
            return RiErrors.Sod("The person who generated or submitted the statement cannot approve it.");
        }

        row.Status = StatementStatus.Approved;
        row.ApprovedAt = clock.Now;
        row.ApprovedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new StatementApproveResponse { Statement = await StatementSupport.ViewAsync(db, row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.Statement.approve</c>.</summary>
internal sealed class ApproveStatementAuditor : ICommandAuditor<ApproveStatement, StatementApproveResponse>
{
    public CommandAuditFacts Describe(ApproveStatement command, Result<StatementApproveResponse>? result) =>
        StatementSupport.Facts(command.Request.StatementId, null, new { status = "SUBMITTED" }, new { status = "APPROVED" }, result is { IsSuccess: true });
}

/// <summary><c>ri.Statement.issue</c> as a command of the platform pipeline.</summary>
internal sealed record IssueStatement(StatementIssueRequest Request) : ICommand<StatementIssueResponse>;

/// <summary>Shape rules: a body.</summary>
internal sealed class IssueStatementValidator : AbstractValidator<IssueStatement>
{
    public IssueStatementValidator() => RuleFor(c => c.Request).NotNull();
}

/// <summary>
/// Issues an APPROVED statement, once. The content is already sealed at generation; issuing fixes the moment and the person.
/// TODO(hardening): store the rendered statement as a DOC document through the Documents contract once the template catalogue (BO-DOC-05) and the composer (BO-DOC-06) are in.
/// </summary>
internal sealed class IssueStatementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<IssueStatement, StatementIssueResponse>
{
    public async Task<Result<StatementIssueResponse>> HandleAsync(IssueStatement command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await StatementSupport.LockAsync(db, legalEntity, command.Request.StatementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("statement");
        }

        if (row.Status != StatementStatus.Approved)
        {
            return StatementSupport.Wrong(row, "issued");
        }

        row.Status = StatementStatus.Issued;
        row.IssuedAt = clock.Now;
        row.IssuedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new StatementIssueResponse { Statement = await StatementSupport.ViewAsync(db, row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.Statement.issue</c>.</summary>
internal sealed class IssueStatementAuditor : ICommandAuditor<IssueStatement, StatementIssueResponse>
{
    public CommandAuditFacts Describe(IssueStatement command, Result<StatementIssueResponse>? result) =>
        StatementSupport.Facts(command.Request.StatementId, null, new { status = "APPROVED" }, new { status = "ISSUED" }, result is { IsSuccess: true });
}

/// <summary><c>ri.Statement.recordResponse</c> as a command of the platform pipeline.</summary>
internal sealed record RecordStatementResponse(StatementRecordResponseRequest Request) : ICommand<StatementRecordResponseResponse>;

/// <summary>Shape rules: the response needs its reason.</summary>
internal sealed class RecordStatementResponseValidator : AbstractValidator<RecordStatementResponse>
{
    public RecordStatementResponseValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Response).IsInEnum().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Records the reinsurer's answer to an ISSUED statement, once: agreed, or disputed. A dispute does not edit anything; the accountant posts a
/// reversal or an adjustment (<c>ri.Item.reverse</c>, <c>ri.Adjustment.calculate</c>) and then reissues the statement.
/// </summary>
internal sealed class RecordStatementResponseHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<RecordStatementResponse, StatementRecordResponseResponse>
{
    public async Task<Result<StatementRecordResponseResponse>> HandleAsync(RecordStatementResponse command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var row = await StatementSupport.LockAsync(db, legalEntity, request.StatementId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return AccountingGuards.NotFound("statement");
        }

        if (row.Status != StatementStatus.Issued)
        {
            return StatementSupport.Wrong(row, "answered");
        }

        var agreed = request.Response == StatementRecordResponseRequest.ResponseValue.Agreed;
        row.Status = agreed ? StatementStatus.Agreed : StatementStatus.Disputed;
        row.Response = row.Status;
        row.ResponseReason = request.Reason;
        row.RespondedAt = clock.Now;
        row.RespondedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new StatementRecordResponseResponse { Statement = await StatementSupport.ViewAsync(db, row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of <c>ri.Statement.recordResponse</c>: the answer and its reason.</summary>
internal sealed class RecordStatementResponseAuditor : ICommandAuditor<RecordStatementResponse, StatementRecordResponseResponse>
{
    public CommandAuditFacts Describe(RecordStatementResponse command, Result<StatementRecordResponseResponse>? result) =>
        StatementSupport.Facts(
            command.Request.StatementId, null, new { status = "ISSUED" },
            new { status = command.Request.Response.ToString().ToUpperInvariant(), reason = command.Request.Reason }, result is { IsSuccess: true });
}

/// <summary><c>ri.Statement.reissue</c> as a command of the platform pipeline.</summary>
internal sealed record ReissueStatement(StatementReissueRequest Request) : ICommand<StatementReissueResponse>;

/// <summary>Shape rules: the reissue needs its reason.</summary>
internal sealed class ReissueStatementValidator : AbstractValidator<ReissueStatement>
{
    public ReissueStatementValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Reissues an ISSUED or DISPUTED statement as the next version after a reversal or adjustment: the content is rebuilt from the current items, the
/// old version becomes SUPERSEDED and stays as it was issued, and the new DRAFT goes through submit, approve and issue again. Nothing changed means no
/// new version.
/// </summary>
internal sealed class ReissueStatementHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ReissueStatement, StatementReissueResponse>
{
    public async Task<Result<StatementReissueResponse>> HandleAsync(ReissueStatement command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var old = await StatementSupport.LockAsync(db, legalEntity, command.Request.StatementId, cancellationToken).ConfigureAwait(false);
        if (old is null)
        {
            return AccountingGuards.NotFound("statement");
        }

        if (old.Status is not (StatementStatus.Issued or StatementStatus.Disputed))
        {
            return StatementSupport.Wrong(old, "reissued");
        }

        var built = await StatementSupport.BuildAsync(db, legalEntity, old.ReinsurerPartyId, old.Period, old.Currency, cancellationToken).ConfigureAwait(false);
        if (built is null || built.Value.Header.ContentHash == old.ContentHash)
        {
            return AccountingGuards.State("Nothing changed since the statement was issued; post a reversal or an adjustment first (ri.Item.reverse, ri.Adjustment.calculate).");
        }

        var (header, lines) = built.Value;
        header.Version = old.Version + 1;
        header.PreviousStatementId = old.StatementId;
        header.Status = StatementStatus.Draft;
        header.Reason = command.Request.Reason;
        header.CreatedAt = clock.Now;
        header.CreatedBy = context.Actor.ToString();

        // The old version leaves the live slot first (one live statement per reinsurer, period and currency).
        old.Status = StatementStatus.Superseded;
        old.SupersededBy = header.StatementId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.Statements.Add(header);
        db.StatementLines.AddRange(lines);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new StatementReissueResponse { Statement = StatementSupport.ToView(header, lines) };
    }
}

/// <summary>Audit facts of <c>ri.Statement.reissue</c>: on the superseded statement, with the new version.</summary>
internal sealed class ReissueStatementAuditor : ICommandAuditor<ReissueStatement, StatementReissueResponse>
{
    public CommandAuditFacts Describe(ReissueStatement command, Result<StatementReissueResponse>? result) =>
        StatementSupport.Facts(
            command.Request.StatementId, null, new { status = "ISSUED" },
            new
            {
                status = "SUPERSEDED", reason = command.Request.Reason, newStatementId = result is { IsSuccess: true } ok ? ok.Value.Statement.StatementId.ToString("D") : null,
            }, result is { IsSuccess: true });
}
