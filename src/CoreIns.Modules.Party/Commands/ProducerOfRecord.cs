using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
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

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.ProducerOfRecord.change</c> (BO-PTY-09, REQ-PTY-218).</summary>
internal sealed record ChangeProducerOfRecord(ProducerOfRecordChangeRequest Request) : ICommand<ProducerOfRecordChangeResponse>;

/// <summary><c>pty.ProducerOfRecord.bulkTransfer</c>: the change rules per term, one result each.</summary>
internal sealed record BulkTransferProducerOfRecord(ProducerOfRecordBulkTransferRequest Request) : ICommand<ProducerOfRecordBulkTransferResponse>;

/// <summary><c>pty.ProducerOfRecord.cancel</c>: withdraw a change that has not taken effect yet.</summary>
internal sealed record CancelProducerOfRecord(ProducerOfRecordCancelRequest Request) : ICommand<ProducerOfRecordCancelResponse>;

internal sealed class ChangeProducerOfRecordValidator : AbstractValidator<ChangeProducerOfRecord>
{
    public ChangeProducerOfRecordValidator()
    {
        RuleFor(c => c.Request.TermId.Value).NotEmpty();
        RuleFor(c => c.Request.ProducerCode).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.Note).MaximumLength(500);
        RuleFor(c => c.Request.LetterReference).MaximumLength(100);
        RuleFor(c => c.Request.SplitShare).Must(s => s is null || (s > 0m && s <= 100m)).WithMessage("The split share must be above 0 and at most 100.");
    }
}

internal sealed class BulkTransferProducerOfRecordValidator : AbstractValidator<BulkTransferProducerOfRecord>
{
    public BulkTransferProducerOfRecordValidator()
    {
        RuleFor(c => c.Request.Terms).NotEmpty().Must(t => t.Count <= 500).WithMessage("A bulk transfer takes at most 500 terms.");
        RuleFor(c => c.Request.ProducerCode).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Request.Note).MaximumLength(500);
        RuleFor(c => c.Request.LetterReference).MaximumLength(100);
    }
}

internal sealed class CancelProducerOfRecordValidator : AbstractValidator<CancelProducerOfRecord>
{
    public CancelProducerOfRecordValidator()
    {
        RuleFor(c => c.Request.PorId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>Wire-string conversion of the contract enums (the same names the JSON uses), so rows keep the contract's codes.</summary>
internal static class PorWire
{
    public static string Of<T>(T value)
        where T : struct, Enum => JsonSerializer.Serialize(value).Trim('"');

    public static T Parse<T>(string wire)
        where T : struct, Enum => JsonSerializer.Deserialize<T>($"\"{wire}\"");
}

/// <summary>The inputs of one producer-of-record change, shared by change and bulk transfer.</summary>
internal sealed record PorChange(
    string ProducerCode, BusinessDate? EffectiveDate, bool AtRenewal, string Reason, string? Note, string Consequence, decimal? SplitShare, string? LetterReference,
    Guid? TransferId);

/// <summary>
/// The producer-of-record rules (REQ-PTY-218). A change inserts the new segment from the effective date and replaces whatever was current from
/// that date on; superseded rows stay as history. The code must pass <c>pty.ProducerCode.validate</c> (active code, active intermediary, active
/// register, appointed product); a different intermediary needs the broker-of-record letter reference; the effective date lies inside the term and
/// after the commission settled date. The term and policy are read through the POL in-process contract.
/// </summary>
internal sealed class ProducerOfRecordWriter(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, IntermediaryQueries intermediaries,
    IPolicyTermService terms, IOptions<CommissionOptions> commission)
{
    private const string ChangeTransaction = "PRODUCER_CHANGE";

    public static DomainError NotFound() => DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The policy term or producer-of-record row does not exist.");

    public static ProducerOfRecordEntry View(ProducerOfRecordRow r) => new()
    {
        PorId = r.PorId,
        PolicyId = r.PolicyId,
        PolicyTermId = r.PolicyTermId,
        IntermediaryId = r.IntermediaryId,
        ProducerCode = r.ProducerCode,
        ValidFrom = r.ValidFrom,
        ValidTo = r.ValidTo,
        Reason = PorWire.Parse<ProducerOfRecordEntry.ReasonValue>(r.Reason),
        Note = r.Note,
        CommissionConsequence = PorWire.Parse<ProducerOfRecordEntry.CommissionConsequenceValue>(r.CommissionConsequence),
        SplitShare = r.SplitShare,
        LetterReference = r.LetterReference,
        TransferId = r.TransferId,
        Status = r.RecordedTo is null ? ProducerOfRecordEntry.StatusValue.Current : ProducerOfRecordEntry.StatusValue.Superseded,
        RecordedAt = r.RecordedFrom,
        RecordedBy = r.CreatedBy,
    };

    public async Task<Result<(ProducerOfRecordEntry Entry, string? Previous)>> ChangeAsync(PolicyTermId termId, PorChange change, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        TermGetResponse found;
        try
        {
            found = await terms.GetAsync(termId.Value.ToString("D"), null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        var (policy, term) = (found.Policy, found.Term);
        if (term.State is not (TermStateCode.Scheduled or TermStateCode.InForce or TermStateCode.PendingCancellation))
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "The producer of record can only change on a scheduled, in-force or pending-cancellation term.");
        }

        var zone = BusinessTimeZone.Athens;
        var start = term.Period.Start.ToBusinessDate(zone);
        var end = term.Period.End?.ToBusinessDate(zone);
        BusinessDate effective;
        if (change.AtRenewal)
        {
            if (end is null)
            {
                return RoleSupport.Invalid("atRenewal", "NO_END", "The term has no end date, so there is no renewal to transfer at.");
            }

            effective = end.Value;
        }
        else if (change.EffectiveDate is { } given)
        {
            effective = given;
            if (effective < start || (end is { } last && effective >= last))
            {
                return RoleSupport.Invalid("effectiveDate", "OUTSIDE_TERM", "The effective date must lie inside the term.");
            }
        }
        else
        {
            return RoleSupport.Invalid("effectiveDate", "REQUIRED", "Give an effective date, or ask for the change at renewal.");
        }

        if (CommissionSupport.CheckBackdate(effective, commission.Value, RoleSupport.Today(clock)) is { } backdated)
        {
            return backdated;
        }

        var validation = await intermediaries.ValidateAsync(
            new ProducerCodeValidateRequest { ProducerCode = change.ProducerCode, Product = policy.ProductCode, TransactionType = ChangeTransaction }, effective, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Valid)
        {
            return DomainError.Of(ModuleCode.PTY, "POR-CODE-INVALID", $"The producer code cannot take the policy: {string.Join(", ", validation.Reasons)}.");
        }

        var newCode = await db.ProducerCodes.AsNoTracking()
            .SingleOrDefaultAsync(c => c.LegalEntityId == legalEntity && c.Code == change.ProducerCode, cancellationToken).ConfigureAwait(false);
        if (newCode is null)
        {
            return DomainError.Of(ModuleCode.PTY, "POR-CODE-INVALID", "The producer code is unknown.");
        }

        // Serialise changes of one policy (the unique index on the current start date catches the races this does not).
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({policy.PolicyId.Value.ToString("D")}, 0))", cancellationToken).ConfigureAwait(false);
        var current = await db.ProducerOfRecords
            .Where(r => r.LegalEntityId == legalEntity && r.PolicyId == policy.PolicyId && r.RecordedTo == null)
            .OrderBy(r => r.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);

        // A policy that never had a change has the term's own producer code from the term start: that is the first segment.
        ProducerOfRecordRow? baseline = null;
        if (current.Count == 0 && term.ProducerCode is { } termCode)
        {
            var baseCode = await db.ProducerCodes.AsNoTracking().SingleOrDefaultAsync(c => c.LegalEntityId == legalEntity && c.Code == termCode, cancellationToken).ConfigureAwait(false);
            if (baseCode is not null)
            {
                baseline = Row(legalEntity, policy.PolicyId, term.TermId, baseCode, start, null, "OTHER", "Term producer code", "NEW_PRODUCER_FROM_DATE", null, null, null, clock.Now);
                current.Add(baseline);
            }
        }
        else if (current.Count > 0 && effective < current[0].ValidFrom)
        {
            return RoleSupport.Invalid("effectiveDate", "BEFORE_HISTORY", $"The effective date is before the first recorded segment ({current[0].ValidFrom}); change it from a later date.");
        }

        var segment = current.LastOrDefault(r => r.ValidFrom <= effective && (r.ValidTo is null || effective < r.ValidTo));
        var previous = segment?.ProducerCode ?? term.ProducerCode;
        if (segment is not null && segment.ProducerCode == change.ProducerCode)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", $"{change.ProducerCode} is already the producer of record on {effective}.");
        }

        if (segment is not null && segment.IntermediaryId != newCode.IntermediaryId && string.IsNullOrWhiteSpace(change.LetterReference))
        {
            return DomainError.Of(ModuleCode.PTY, "POR-LETTER-REQUIRED", "A change of intermediary needs the broker-of-record letter reference.");
        }

        var now = clock.Now;
        foreach (var row in current.Where(r => !ReferenceEquals(r, baseline) && (r.ValidTo is null || r.ValidTo > effective)))
        {
            row.RecordedTo = now;
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (segment is not null && segment.ValidFrom < effective)
        {
            db.ProducerOfRecords.Add(Copy(segment, effective, now));
        }

        var entry = Row(
            legalEntity, policy.PolicyId, term.TermId, newCode, effective, null, change.Reason, change.Note, change.Consequence, change.SplitShare, change.LetterReference,
            change.TransferId, now, context.Actor.ToString());
        db.ProducerOfRecords.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return DomainError.Of(ModuleCode.PTY, "STALE", "The producer of record of this policy changed meanwhile; reload and try again.");
        }

        Publish(entry, term.TermId, previous);
        return (View(entry), previous);
    }

    /// <summary>Withdraws a not-yet-effective segment: the previous segment is extended to where the withdrawn one ended.</summary>
    public async Task<Result<IReadOnlyList<ProducerOfRecordEntry>>> CancelAsync(Guid porId, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var row = await db.ProducerOfRecords.SingleOrDefaultAsync(r => r.PorId == porId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.RecordedTo is not null)
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "That producer-of-record row was already superseded.");
        }

        if (row.ValidFrom <= RoleSupport.Today(clock))
        {
            return DomainError.Of(ModuleCode.PTY, "STATE", "The change is already in force and cannot be cancelled; change the producer of record again.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({row.PolicyId.Value.ToString("D")}, 0))", cancellationToken).ConfigureAwait(false);
        var previous = await db.ProducerOfRecords.SingleOrDefaultAsync(
            r => r.PolicyId == row.PolicyId && r.RecordedTo == null && r.ValidTo == row.ValidFrom, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        row.RecordedTo = now;
        ProducerOfRecordRow? restored = null;
        if (previous is not null)
        {
            previous.RecordedTo = now;
            restored = Copy(previous, row.ValidTo, now);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (restored is not null)
        {
            db.ProducerOfRecords.Add(restored);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Publish(restored, row.PolicyTermId, row.ProducerCode, "CORRECTION");
        }

        return (await HistoryAsync(legalEntity, row.PolicyId, cancellationToken).ConfigureAwait(false)).ToList();
    }

    /// <summary>The producer of record of a policy on a date and the history, oldest first. No row means the term's own producer code applies.</summary>
    public async Task<ProducerOfRecordGetResponse> GetAsync(LegalEntityId legalEntity, PolicyId policyId, BusinessDate date, CancellationToken cancellationToken)
    {
        var history = await HistoryAsync(legalEntity, policyId, cancellationToken).ConfigureAwait(false);
        var current = history.LastOrDefault(e => e.Status == ProducerOfRecordEntry.StatusValue.Current && e.ValidFrom <= date && (e.ValidTo is null || date < e.ValidTo));
        return new ProducerOfRecordGetResponse { PolicyId = policyId, Date = date, Current = current, History = history };
    }

    public async Task<IReadOnlyList<ProducerOfRecordEntry>> HistoryAsync(LegalEntityId legalEntity, PolicyId policyId, CancellationToken cancellationToken) =>
        (await db.ProducerOfRecords.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.PolicyId == policyId)
            .OrderBy(r => r.ValidFrom).ThenBy(r => r.RecordedFrom).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(View).ToList();

    private ProducerOfRecordRow Row(
        LegalEntityId legalEntity, PolicyId policyId, PolicyTermId termId, ProducerCodeRow code, BusinessDate from, BusinessDate? to, string reason, string? note,
        string consequence, decimal? split, string? letter, Guid? transferId, Instant now, string? actor = null) => new()
        {
            PorId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            PolicyId = policyId,
            PolicyTermId = termId,
            IntermediaryId = code.IntermediaryId,
            ProducerCode = code.Code,
            ValidFrom = from,
            ValidTo = to,
            Reason = reason,
            Note = note,
            CommissionConsequence = consequence,
            SplitShare = split,
            LetterReference = letter,
            TransferId = transferId,
            RecordedFrom = now,
            CreatedBy = actor ?? context.Actor.ToString(),
        };

    private ProducerOfRecordRow Copy(ProducerOfRecordRow source, BusinessDate? validTo, Instant now) => new()
    {
        PorId = Guid.CreateVersion7(),
        LegalEntityId = source.LegalEntityId,
        PolicyId = source.PolicyId,
        PolicyTermId = source.PolicyTermId,
        IntermediaryId = source.IntermediaryId,
        ProducerCode = source.ProducerCode,
        ValidFrom = source.ValidFrom,
        ValidTo = validTo,
        Reason = source.Reason,
        Note = source.Note,
        CommissionConsequence = source.CommissionConsequence,
        SplitShare = source.SplitShare,
        LetterReference = source.LetterReference,
        TransferId = source.TransferId,
        RecordedFrom = now,
        CreatedBy = context.Actor.ToString(),
    };

    private void Publish(ProducerOfRecordRow row, PolicyTermId termId, string? oldCode, string? reasonOverride = null) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ProducerOfRecordChangedV1.Descriptor), "PolicyTerm", termId.Value.ToString("D"),
            new ProducerOfRecordChangedV1
            {
                PolicyTermRef = termId,
                PolicyRef = row.PolicyId,
                OldProducerCode = oldCode,
                NewProducerCode = row.ProducerCode,
                EffectiveDate = row.ValidFrom,
                Reason = reasonOverride ?? row.Reason,
                CommissionConsequence = row.CommissionConsequence,
                SplitShare = row.SplitShare,
                TransferId = row.TransferId,
            },
            BusinessKeys.Empty.With("policyTermId", termId.Value.ToString("D"))));
}

internal sealed class ChangeProducerOfRecordHandler(ProducerOfRecordWriter writer) : ICommandHandler<ChangeProducerOfRecord, ProducerOfRecordChangeResponse>
{
    public async Task<Result<ProducerOfRecordChangeResponse>> HandleAsync(ChangeProducerOfRecord command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var result = await writer.ChangeAsync(
            r.TermId,
            new PorChange(r.ProducerCode, r.EffectiveDate, r.AtRenewal == true, PorWire.Of(r.Reason), r.Note, PorWire.Of(r.CommissionConsequence), r.SplitShare, r.LetterReference, null),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new ProducerOfRecordChangeResponse { Entry = result.Value.Entry, PreviousProducerCode = result.Value.Previous }
            : result.Error;
    }
}

/// <summary>
/// Moves many terms to one producer code. Every term is its own unit: a refused term (inactive code, unappointed product, missing letter,
/// state) is reported and the others still go through. TODO(hardening): run as a background job once the book is large; today it is
/// synchronous with a 500-term cap.
/// </summary>
internal sealed class BulkTransferProducerOfRecordHandler(ProducerOfRecordWriter writer) : ICommandHandler<BulkTransferProducerOfRecord, ProducerOfRecordBulkTransferResponse>
{
    public async Task<Result<ProducerOfRecordBulkTransferResponse>> HandleAsync(BulkTransferProducerOfRecord command, CancellationToken cancellationToken)
    {
        var r = command.Request;
        var transferId = Guid.CreateVersion7();
        var change = new PorChange(r.ProducerCode, r.EffectiveDate, r.AtRenewal == true, PorWire.Of(r.Reason), r.Note, PorWire.Of(r.CommissionConsequence), null, r.LetterReference, transferId);
        var results = new List<ProducerOfRecordBulkTransferResult>();
        foreach (var termId in r.Terms.Select(t => t.TermId).Distinct())
        {
            var outcome = await writer.ChangeAsync(termId, change, cancellationToken).ConfigureAwait(false);
            results.Add(outcome.IsSuccess
                ? new ProducerOfRecordBulkTransferResult { TermId = termId, Outcome = ProducerOfRecordBulkTransferResult.OutcomeValue.Transferred, Entry = outcome.Value.Entry }
                : new ProducerOfRecordBulkTransferResult
                {
                    TermId = termId, Outcome = ProducerOfRecordBulkTransferResult.OutcomeValue.Refused, ErrorCode = outcome.Error.Code.Value, Message = outcome.Error.Detail,
                });
        }

        var transferred = results.Count(x => x.Outcome == ProducerOfRecordBulkTransferResult.OutcomeValue.Transferred);
        return new ProducerOfRecordBulkTransferResponse { TransferId = transferId, Transferred = transferred, Refused = results.Count - transferred, Results = results };
    }
}

internal sealed class CancelProducerOfRecordHandler(ProducerOfRecordWriter writer, PartyDbContext db) : ICommandHandler<CancelProducerOfRecord, ProducerOfRecordCancelResponse>
{
    public async Task<Result<ProducerOfRecordCancelResponse>> HandleAsync(CancelProducerOfRecord command, CancellationToken cancellationToken)
    {
        var porId = command.Request.PorId;
        var result = await writer.CancelAsync(porId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        var policy = await db.ProducerOfRecords.AsNoTracking().Where(r => r.PorId == porId).Select(r => r.PolicyId).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new ProducerOfRecordCancelResponse { PolicyId = policy, History = result.Value };
    }
}

/// <summary>Audit facts of the producer-of-record commands: the policy and the rows only, no personal values.</summary>
internal sealed class ProducerOfRecordAuditor :
    ICommandAuditor<ChangeProducerOfRecord, ProducerOfRecordChangeResponse>,
    ICommandAuditor<BulkTransferProducerOfRecord, ProducerOfRecordBulkTransferResponse>,
    ICommandAuditor<CancelProducerOfRecord, ProducerOfRecordCancelResponse>
{
    public CommandAuditFacts Describe(ChangeProducerOfRecord command, Result<ProducerOfRecordChangeResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.PTY, "ProducerOfRecord", ok.Value.Entry.PorId.ToString("D")),
                BusinessKeys = BusinessKeys.Empty.With("policyId", ok.Value.Entry.PolicyId.Value.ToString("D")).With("policyTermId", command.Request.TermId.Value.ToString("D")),
                Changes = AuditDiff.Compute(
                    new { producerCode = ok.Value.PreviousProducerCode }, new { producerCode = ok.Value.Entry.ProducerCode, effectiveDate = ok.Value.Entry.ValidFrom.ToString() }),
            }
            : new CommandAuditFacts { BusinessKeys = BusinessKeys.Empty.With("policyTermId", command.Request.TermId.Value.ToString("D")) };

    public CommandAuditFacts Describe(BulkTransferProducerOfRecord command, Result<ProducerOfRecordBulkTransferResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.PTY, "ProducerOfRecordTransfer", ok.Value.TransferId.ToString("D")),
                BusinessKeys = BusinessKeys.Empty.With("transferId", ok.Value.TransferId.ToString("D")),
                Changes = AuditDiff.Compute(
                    new { producerCode = (string?)null }, new { producerCode = command.Request.ProducerCode, transferred = ok.Value.Transferred.ToString(CultureInfo.InvariantCulture), refused = ok.Value.Refused.ToString(CultureInfo.InvariantCulture) }),
            }
            : new CommandAuditFacts();

    public CommandAuditFacts Describe(CancelProducerOfRecord command, Result<ProducerOfRecordCancelResponse>? result) =>
        new()
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "ProducerOfRecord", command.Request.PorId.ToString("D")),
            BusinessKeys = result is { IsSuccess: true } ok ? BusinessKeys.Empty.With("policyId", ok.Value.PolicyId.Value.ToString("D")) : BusinessKeys.Empty,
        };
}
