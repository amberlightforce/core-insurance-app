using System.Globalization;
using System.Text;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Commands;

internal sealed record SubmitManualJournal(ManualJournalSubmitInput Request) : ICommand<ManualJournalEnvelope>;

internal sealed record DecideManualJournal(ManualJournalDecideInput Request) : ICommand<ManualJournalEnvelope>;

/// <summary>
/// The maker-checker binding of a manual journal to <c>plt.Approval</c> (BO-FIN-01b): the approval type and authority type
/// (<c>FIN.MANUAL_JOURNAL</c>, limited by the total of the journal), the subject, and the content hash the checker approves.
/// The hash covers everything that posts, so a changed journal can never be posted on an earlier approval (NX-EC-09).
/// </summary>
internal static class ManualJournalApproval
{
    public const string Type = "FIN.MANUAL_JOURNAL";

    /// <summary>The inbox role a referred request lands in (illustrative: finance staff; an amount above a grant refers to the next holder).</summary>
    public const string ReferralRole = "Staff.Finance";

    public static AuthorityTypeCode Authority { get; } = AuthorityTypeCode.Parse(Type);

    public static AuthorityTypeDefinition Definition { get; } = new(
        Authority, ModuleCode.FIN, new LocalizedText("Έγκριση χειρόγραφου εγγράφου", "Manual journal approval"),
        [new AuthorityDimensionDefinition("amount", DimensionKind.Money)]);

    public static ObjectRef Subject(Guid id) => new(ModuleCode.FIN, "ManualJournal", id.ToString("D"));

    /// <summary>The journal's size for the authority check: the total of its debits.</summary>
    public static Money Total(ManualJournalRow row, IEnumerable<ManualJournalLineRow> lines) =>
        new(lines.Where(l => l.Side == Sides.Debit).Sum(l => l.Amount), Currency.FromCode(row.Currency.Trim()));

    public static Sha256Hash Hash(ManualJournalRow row, IEnumerable<ManualJournalLineRow> lines)
    {
        var text = new StringBuilder("FIN.MANUAL_JOURNAL/v1|")
            .Append(row.ManualJournalId.ToString("D")).Append('|').Append(row.Book).Append('|')
            .Append(row.AccountingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|').Append(row.Currency.Trim()).Append('|')
            .Append(row.Description).Append('|').Append(row.EvidenceRef);
        foreach (var line in lines.OrderBy(l => l.LineNo))
        {
            text.Append('|').Append(line.AccountCode).Append(':').Append(line.Side).Append(':').Append(ManualJournals.Money(line.Amount));
        }

        return Sha256Hash.ComputeUtf8(text.ToString());
    }
}

/// <summary>Constants of the journals a manual journal posts (they are not produced by a posting rule).</summary>
internal static class ManualJournalPosting
{
    public const string RuleCode = "MANUAL";
    public const string SourceModule = "FIN";
    public const string SourceEventType = "ManualJournal";
}

/// <summary>
/// <c>fin.ManualJournal.submit</c>: the maker sends a DRAFT for approval. FIN re-checks the open period, then requests a
/// <c>plt.Approval</c> (in process, <c>request</c> is not on REST) for the content hash and the total. Only the maker submits.
/// </summary>
internal sealed class SubmitManualJournalHandler(
    FinanceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, FinancePeriods periods, IPlatformApprovalService approvals)
    : ICommandHandler<SubmitManualJournal, ManualJournalEnvelope>
{
    public async Task<Result<ManualJournalEnvelope>> HandleAsync(SubmitManualJournal command, CancellationToken cancellationToken)
    {
        if (command.Request?.Id is not { } id)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "id is required.");
        }

        var scope = ManualJournalScope.Of(context, legalEntities);
        var row = scope is { } s
            ? await db.ManualJournals.SingleOrDefaultAsync(r => r.ManualJournalId == id && r.LegalEntityId == s.Id.Value, cancellationToken).ConfigureAwait(false)
            : null;
        if (row is null)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The manual journal does not exist.");
        }

        var actor = context.Actor.Id;
        if (!string.Equals(row.CreatedBy, actor, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOD", "Only the maker of a manual journal can submit it.");
        }

        if (row.Status != ManualJournalStatus.Draft)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} manual journal cannot be submitted.");
        }

        if (await periods.StateAsync(row.LegalEntityId, FinancePeriods.CodeOf(row.AccountingDate), cancellationToken).ConfigureAwait(false) != FinancePeriods.Open)
        {
            return DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"The accounting period {FinancePeriods.CodeOf(row.AccountingDate)} is not open.");
        }

        var lines = await db.ManualJournalLines.AsNoTracking().Where(l => l.ManualJournalId == id).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = ManualJournalApproval.Type,
                    ObjectRef = ManualJournalApproval.Subject(id),
                    PayloadHash = ManualJournalApproval.Hash(row, lines),
                    Authority = new ApprovalAuthority { Type = ManualJournalApproval.Type, Amount = ManualJournalApproval.Total(row, lines) },
                    ReferralRole = ManualJournalApproval.ReferralRole,
                    Reason = $"Manual journal {row.Number}: {row.Description}",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            row.ApprovalRequestId = response.Request.RequestId;
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.Status = ManualJournalStatus.Submitted;
        row.SubmittedBy = actor;
        row.SubmittedAt = clock.Now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The manual journal changed meanwhile; load it and try again.");
        }

        return ManualJournalReader.ToEnvelope(row, lines);
    }
}

/// <summary>
/// <c>fin.ManualJournal.decide</c> (REQ-FIN-215..-226): a checker approves or rejects a SUBMITTED journal. FIN refuses the maker, then
/// <c>plt.Approval.decide</c> checks a human checker who is not the maker or an editor and holds <c>FIN.MANUAL_JOURNAL</c> authority for
/// the total (re-checked now). An approval posts one sealed journal through <see cref="JournalWriter"/> (source MANUAL) into an open period,
/// exactly once: the status graph is APPROVED then POSTED, and a repeated decision returns the current state without posting again.
/// </summary>
internal sealed class DecideManualJournalHandler(
    FinanceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, FinancePeriods periods, ReferenceData reference,
    JournalWriter writer, IEventPublisher events, IPlatformApprovalService approvals)
    : ICommandHandler<DecideManualJournal, ManualJournalEnvelope>
{
    public async Task<Result<ManualJournalEnvelope>> HandleAsync(DecideManualJournal command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request?.Id is not { } id)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "id is required.");
        }

        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT"))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "decision must be APPROVE or REJECT.");
        }

        var approve = decision == "APPROVE";
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (!approve && comment is null)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A rejection needs a comment.");
        }

        var scope = ManualJournalScope.Of(context, legalEntities);
        var row = scope is { } s
            ? await db.ManualJournals.SingleOrDefaultAsync(r => r.ManualJournalId == id && r.LegalEntityId == s.Id.Value, cancellationToken).ConfigureAwait(false)
            : null;
        if (row is null || scope is not { } resolved)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The manual journal does not exist.");
        }

        var legalEntity = resolved.Id;
        var code = resolved.Code;
        var lines = await db.ManualJournalLines.AsNoTracking().Where(l => l.ManualJournalId == id).OrderBy(l => l.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);

        // A repeated decision is a replay, not a second posting.
        if ((approve && row.Status == ManualJournalStatus.Posted) || (!approve && row.Status == ManualJournalStatus.Rejected))
        {
            return ManualJournalReader.ToEnvelope(row, lines);
        }

        if (row.Status != ManualJournalStatus.Submitted || row.ApprovalRequestId is not { } approvalRequestId)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} manual journal cannot be decided.");
        }

        var checker = context.Actor.Id;
        if (string.Equals(row.CreatedBy, checker, StringComparison.Ordinal) || string.Equals(row.SubmittedBy, checker, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOD", "The maker of a manual journal cannot decide it (maker is not checker).");
        }

        var hash = ManualJournalApproval.Hash(row, lines);
        var money = ManualJournalApproval.Total(row, lines);
        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        BookSetup? setup = null;
        if (approve)
        {
            // Everything that can refuse the posting is checked before the approval is decided, so a refusal leaves the request open.
            if (await periods.StateAsync(legalEntity.Value, FinancePeriods.CodeOf(row.AccountingDate), cancellationToken).ConfigureAwait(false) != FinancePeriods.Open)
            {
                return DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"The accounting period {FinancePeriods.CodeOf(row.AccountingDate)} is not open (soft-closed or locked); the journal cannot be posted.");
            }

            var profile = await reference.BookProfileAsync(code.Value, cancellationToken).ConfigureAwait(false);
            setup = profile is null ? null : await reference.BookSetupAsync(profile, row.Book, row.AccountingDate, cancellationToken).ConfigureAwait(false);
            if (setup is null)
            {
                return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Book {row.Book} has no posting rule set in force on {row.AccountingDate.Value:yyyy-MM-dd}.");
            }

            foreach (var account in lines.Select(l => l.AccountCode).Distinct(StringComparer.Ordinal))
            {
                if (!setup.Accounts.TryGetValue(account, out var chart) || !chart.Active)
                {
                    return DomainError.Of(ModuleCode.FIN, "TEMPLATE-ACCOUNT", $"Account {account} is no longer an active account of book {row.Book}.");
                }
            }
        }

        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = approvalRequestId,
                    PayloadHash = hash,
                    Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                    Comment = comment,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            if (approve)
            {
                // The decided approval must be for exactly this subject, type, content and total: nothing is posted that was not approved.
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = approvalRequestId, Hash = hash, Type = ManualJournalApproval.Type, ObjectRef = ManualJournalApproval.Subject(id) },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != ManualJournalApproval.Type || verified.Authority.Amount != money)
                {
                    return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", "The approval does not cover this journal's content and total; nothing is posted that was not approved.");
                }
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.DecidedBy = checker;
        row.DecidedAt = clock.Now;
        row.DecisionReason = comment;
        row.Status = approve ? ManualJournalStatus.Approved : ManualJournalStatus.Rejected;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (!approve)
            {
                return ManualJournalReader.ToEnvelope(row, lines);
            }

            var functional = setup!.FunctionalCurrency;
            var drafts = lines.Select(l => new JournalLineDraft(
                l.LineNo, l.AccountCode, l.Side, new Money(l.Amount, functional), new Money(l.Amount, functional), ManualJournalPosting.RuleCode, LineDimensions.None)).ToList();
            var draft = new JournalDraft(row.Book, row.AccountingDate, row.AccountingDate, SourceTypes.Manual, setup.RuleSet.RuleSetId, setup.RuleSet.Version, [ManualJournalPosting.RuleCode], drafts);
            var source = new JournalSource(legalEntity, code.Value, jurisdiction.Value, ManualJournalPosting.SourceModule, ManualJournalPosting.SourceEventType, [row.ManualJournalId], row.Number);
            var written = await writer.WriteAsync(draft, source, functional, null, null, cancellationToken).ConfigureAwait(false);
            row.Status = ManualJournalStatus.Posted;
            row.PostedJournalIds = [written.JournalId];
            row.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            events.Publish(written.Posted);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The manual journal changed meanwhile; load it and try again.");
        }

        return ManualJournalReader.ToEnvelope(row, lines);
    }
}

/// <summary>
/// Blocks the MANUAL_JOURNALS close task while the period still has manual journals waiting for a decision: SUBMITTED, or APPROVED but not
/// yet POSTED. DRAFTs do not block (they change no ledger) but a DRAFT cannot be submitted into a closed period.
/// </summary>
internal sealed class ManualJournalsCloseGate(FinanceDbContext db) : ICloseTaskGate
{
    public string Task => PeriodClose.ManualJournals;

    public async Task<string?> BlockerAsync(Guid legalEntity, string period, CancellationToken cancellationToken)
    {
        var waiting = await db.ManualJournals.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && (r.Status == ManualJournalStatus.Submitted || r.Status == ManualJournalStatus.Approved))
            .Select(r => r.AccountingDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        var count = waiting.Count(d => FinancePeriods.CodeOf(d) == period);
        return count == 0 ? null : $"{count} manual journal(s) of the period are submitted but not yet posted or rejected.";
    }
}
