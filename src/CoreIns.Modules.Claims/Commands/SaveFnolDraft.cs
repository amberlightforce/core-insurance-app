using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.Fnol.saveDraft</c> (BO-CLM-02, D-SL2-11).</summary>
internal sealed record SaveFnolDraft(FnolSaveDraftRequest Request) : ICommand<FnolSaveDraftResponse>;

/// <summary>Shape rules of the FNOL so far: the shape rules of a submit, none of the mandatory-field checks.</summary>
internal sealed class SaveFnolDraftValidator : AbstractValidator<SaveFnolDraft>
{
    public SaveFnolDraftValidator()
    {
        RuleFor(c => c.Request.FnolPayload).SetValidator(new FnolRequestValidator());
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request.ExpectedRecordVersion is not null);
        RuleFor(c => c.Request.ExpectedRecordVersion).NotNull().When(c => c.Request.DraftId is not null).WithErrorCode("VERSION_REQUIRED");
    }
}

/// <summary>
/// Saves (creates or updates) an incomplete FNOL (REQ-CLM-001): the payload, whatever is missing, goes into <c>clm.fnol_draft</c> as a P2
/// envelope bound to the draft id. A draft is not a claim: no claim number is drawn, no exposure or clock exists, no event is published, and
/// it never shows in claim lists or searches. Only its creator updates or reads it (another user's draft is "not found"); a draft that was
/// submitted or changed meanwhile answers CLM-ERR-STALE. Submitting runs the ordinary <c>clm.Fnol.submit</c> with <c>draftId</c>.
/// TODO(hardening): draft expiry, discard, a "my drafts" list, and DSAR coverage of drafts.
/// </summary>
internal sealed class SaveFnolDraftHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection) : ICommandHandler<SaveFnolDraft, FnolSaveDraftResponse>
{
    public async Task<Result<FnolSaveDraftResponse>> HandleAsync(SaveFnolDraft command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var actor = context.Actor.ToString();
        var now = clock.Now;
        FnolDraftRow row;
        if (request.DraftId is { } draftId)
        {
            var loaded = await db.FnolDrafts.SingleOrDefaultAsync(d => d.DraftId == draftId && d.LegalEntityId == legalEntity && d.CreatedBy == actor, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return ClaimSupport.NotFound("FNOL draft");
            }

            if (loaded.Status != FnolDraftRow.Draft || loaded.RecordVersion != request.ExpectedRecordVersion)
            {
                return DomainError.Of(ModuleCode.CLM, "STALE", "The draft changed meanwhile or was already submitted.");
            }

            row = loaded;
            row.RecordVersion++;
        }
        else
        {
            row = new FnolDraftRow
            {
                DraftId = Guid.CreateVersion7(),
                LegalEntityId = legalEntity,
                Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
                CreatedAt = now,
                CreatedBy = actor,
            };
            db.FnolDrafts.Add(row);
        }

        row.PayloadEncrypted = await protection.EncryptAsync(
            legalEntity, ClaimProtection.FnolDraftPayloadField, row.DraftId, JsonSerializer.Serialize(request.FnolPayload, SharedKernelJson.Options), cancellationToken).ConfigureAwait(false);
        row.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.CLM, "STALE", "The draft changed meanwhile or was already submitted.");
        }

        return new FnolSaveDraftResponse { DraftId = row.DraftId, RecordVersion = row.RecordVersion, SavedAt = now };
    }
}

/// <summary>Audit facts of <c>clm.Fnol.saveDraft</c>: the draft id only, never the content (P2).</summary>
internal sealed class SaveFnolDraftAuditor : ICommandAuditor<SaveFnolDraft, FnolSaveDraftResponse>
{
    public CommandAuditFacts Describe(SaveFnolDraft command, Result<FnolSaveDraftResponse>? result) =>
        result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.CLM, "FnolDraft", ok.Value.DraftId.ToString()),
                BusinessKeys = BusinessKeys.Empty.With("draftId", ok.Value.DraftId.ToString()),
                Changes = AuditDiff.Compute(null, new { recordVersion = ok.Value.RecordVersion }),
            }
            : new CommandAuditFacts();
}

/// <summary><c>clm.Fnol.getDraft</c>: the creator's draft, decrypted on read (never stored decrypted, never logged).</summary>
internal sealed class FnolDraftReader(ClaimsDbContext db, RequestContext context, ClaimProtection protection)
{
    public async Task<FnolDraftGetResponse?> GetAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var actor = context.Actor.ToString();
        var row = await db.FnolDrafts.AsNoTracking()
            .SingleOrDefaultAsync(d => d.DraftId == draftId && d.LegalEntityId == legalEntity && d.CreatedBy == actor, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var json = await protection.DecryptAsync(legalEntity, ClaimProtection.FnolDraftPayloadField, row.DraftId, row.PayloadEncrypted, cancellationToken).ConfigureAwait(false);
        return new FnolDraftGetResponse
        {
            DraftId = row.DraftId,
            RecordVersion = row.RecordVersion,
            Status = Enum.Parse<FnolDraftGetResponse.StatusValue>(row.Status, ignoreCase: true),
            SavedAt = row.UpdatedAt,
            ClaimId = row.ClaimId,
            FnolPayload = JsonSerializer.Deserialize<FnolSubmitRequest>(json, SharedKernelJson.Options)!,
        };
    }
}
