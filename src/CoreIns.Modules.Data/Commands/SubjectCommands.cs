using System.Text.Json;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Contracts.Events;
using CoreIns.Modules.Data.Domain;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Commands;

/// <summary><c>dat.Subject.export</c> (DSAR access).</summary>
internal sealed record ExportSubject(SubjectExportRequest Request) : ICommand<SubjectExportResponse>;

/// <summary><c>dat.Erasure.execute</c>: crypto-shred, idempotent on the request id.</summary>
internal sealed record ExecuteErasure(ErasureExecuteRequest Request) : ICommand<ErasureExecuteResponse>;

/// <summary><c>dat.Restriction.apply</c>.</summary>
internal sealed record ApplyRestriction(RestrictionApplyRequest Request) : ICommand<RestrictionApplyResponse>;

internal sealed class ExportSubjectValidator : AbstractValidator<ExportSubject>
{
    public ExportSubjectValidator() => RuleFor(c => c.Request.SubjectToken).NotEmpty().MaximumLength(128);
}

internal sealed class ExecuteErasureValidator : AbstractValidator<ExecuteErasure>
{
    public ExecuteErasureValidator()
    {
        RuleFor(c => c.Request.RequestId).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.SubjectToken).NotEmpty().MaximumLength(128);
    }
}

internal sealed class ApplyRestrictionValidator : AbstractValidator<ApplyRestriction>
{
    public ApplyRestrictionValidator() => RuleFor(c => c.Request.SubjectToken).NotEmpty().MaximumLength(128);
}

/// <summary>Exports the facts of a subject by decrypting them under the subject key; an erased subject yields none.</summary>
internal sealed class ExportSubjectHandler(DataDbContext db) : ICommandHandler<ExportSubject, SubjectExportResponse>
{
    public async Task<Result<SubjectExportResponse>> HandleAsync(ExportSubject command, CancellationToken cancellationToken)
    {
        var token = command.Request.SubjectToken;
        var key = await db.SubjectKeys.AsNoTracking().SingleOrDefaultAsync(k => k.SubjectToken == token, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return new SubjectExportResponse { SubjectToken = token, Status = SubjectExportResponse.StatusValue.Active, FactCount = 0, KeyDestroyed = false, Facts = [] };
        }

        if (key.KeyMaterial is null)
        {
            return new SubjectExportResponse { SubjectToken = token, Status = SubjectExportResponse.StatusValue.Erased, FactCount = 0, KeyDestroyed = true, Facts = [] };
        }

        var rows = await db.SubjectFacts.AsNoTracking().Where(f => f.SubjectToken == token && !f.Blanked)
            .OrderBy(f => f.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var facts = rows.Select(f => new SubjectExportResponse.FactItem
        {
            Category = f.Category,
            Key = f.FactKey,
            Value = SubjectCrypto.Open(key.KeyMaterial, f.Cipher!),
        }).ToList();
        return new SubjectExportResponse
        {
            SubjectToken = token,
            Status = key.RestrictedAt is null ? SubjectExportResponse.StatusValue.Active : SubjectExportResponse.StatusValue.Restricted,
            FactCount = facts.Count,
            KeyDestroyed = false,
            Facts = facts,
        };
    }
}

/// <summary>
/// Erases a subject: destroys the subject key once (crypto-shred) and blanks the facts, synchronously. Idempotent on the
/// request id: a repeat returns the stored result and does nothing (no second key destruction, no second event).
/// A restricted subject is refused (DAT-ERR-060, fail closed). LakehouseErasureCompleted is audit evidence only (R-97).
/// </summary>
internal sealed class ExecuteErasureHandler(DataDbContext db, IClock clock, IEventPublisher events) : ICommandHandler<ExecuteErasure, ErasureExecuteResponse>
{
    public async Task<Result<ErasureExecuteResponse>> HandleAsync(ExecuteErasure command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var token = request.SubjectToken;

        var prior = await db.ErasureRequests.AsNoTracking().SingleOrDefaultAsync(r => r.RequestId == request.RequestId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            // A request id is bound to one subject: reusing it for another is refused, never replayed.
            return prior.SubjectToken == token
                ? JsonSerializer.Deserialize<ErasureExecuteResponse>(prior.Result, SharedKernelJson.Options)!
                : DomainError.Of(ModuleCode.DAT, "IDEMPOTENCY-MISMATCH", "The request id was already used for another subject.");
        }

        var now = clock.Now;
        var key = await db.SubjectKeys.SingleOrDefaultAsync(k => k.SubjectToken == token, cancellationToken).ConfigureAwait(false);
        if (key?.RestrictedAt is not null && key.KeyMaterial is not null)
        {
            return DomainError.Of(ModuleCode.DAT, "060", "The subject is under restriction; erasure is refused until the restriction is lifted.");
        }

        var blanked = 0;
        var destroyedNow = false;
        if (key is null)
        {
            // Unknown subject: a tombstone keeps later fact writes refused.
            db.SubjectKeys.Add(new SubjectKeyRow { SubjectToken = token, KeyId = Guid.CreateVersion7(), CreatedAt = now, DestroyedAt = now });
        }
        else if (key.KeyMaterial is not null)
        {
            key.KeyMaterial = null;
            key.DestroyedAt = now;
            destroyedNow = true;
        }

        var facts = await db.SubjectFacts.Where(f => f.SubjectToken == token && !f.Blanked).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var fact in facts)
        {
            fact.Cipher = null;
            fact.Blanked = true;
            blanked++;
        }

        var response = new ErasureExecuteResponse
        {
            SubjectToken = token,
            Status = ErasureExecuteResponse.StatusValue.Erased,
            FactCount = blanked,
            KeyDestroyed = true,
            Facts = [],
        };
        db.ErasureRequests.Add(new ErasureRequestRow
        {
            RequestId = request.RequestId,
            SubjectToken = token,
            DsarRef = request.DsarRef,
            Result = JsonSerializer.Serialize(response, SharedKernelJson.Options),
            ExecutedAt = now,
        });

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(LakehouseErasureCompletedV1.Descriptor), "ErasureTask", request.RequestId,
            new LakehouseErasureCompletedV1
            {
                DsarId = DsarId.From(request.DsarRef ?? Guid.CreateVersion7()),
                Outcome = LakehouseErasureCompletedV1.OutcomeValue.Erased,
                Layers = ["dat.subject_fact"],
                Counts = new Dictionary<string, int> { ["facts"] = blanked, ["keysDestroyed"] = destroyedNow ? 1 : 0 },
            },
            BusinessKeys.Empty.With("taskId", request.RequestId).With("dsarId", (request.DsarRef ?? Guid.Empty).ToString("D"))) { OccurredAt = now });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }
}

/// <summary>Restricts processing of a subject (facts stay, status RESTRICTED); an erased subject cannot be restricted.</summary>
internal sealed class ApplyRestrictionHandler(DataDbContext db, IClock clock) : ICommandHandler<ApplyRestriction, RestrictionApplyResponse>
{
    public async Task<Result<RestrictionApplyResponse>> HandleAsync(ApplyRestriction command, CancellationToken cancellationToken)
    {
        var token = command.Request.SubjectToken;
        var key = await db.SubjectKeys.SingleOrDefaultAsync(k => k.SubjectToken == token, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            key = new SubjectKeyRow { SubjectToken = token, KeyId = Guid.CreateVersion7(), KeyMaterial = SubjectCrypto.NewKey(), CreatedAt = clock.Now };
            db.SubjectKeys.Add(key);
        }

        if (key.KeyMaterial is null)
        {
            return new RestrictionApplyResponse { SubjectToken = token, Status = RestrictionApplyResponse.StatusValue.Erased, FactCount = 0, KeyDestroyed = true, Facts = [] };
        }

        key.RestrictedAt ??= clock.Now;
        var count = await db.SubjectFacts.CountAsync(f => f.SubjectToken == token && !f.Blanked, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RestrictionApplyResponse { SubjectToken = token, Status = RestrictionApplyResponse.StatusValue.Restricted, FactCount = count, KeyDestroyed = false, Facts = [] };
    }
}

/// <summary>Writes subject facts, encrypted under the subject key (created on first use). Used by seeding and tests until a lakehouse feed exists.</summary>
internal sealed class SubjectFactStore(DataDbContext db, IClock clock)
{
    public async Task<bool> RecordAsync(string subjectToken, string category, string factKey, string value, CancellationToken cancellationToken)
    {
        var key = await db.SubjectKeys.SingleOrDefaultAsync(k => k.SubjectToken == subjectToken, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            key = new SubjectKeyRow { SubjectToken = subjectToken, KeyId = Guid.CreateVersion7(), KeyMaterial = SubjectCrypto.NewKey(), CreatedAt = clock.Now };
            db.SubjectKeys.Add(key);
        }

        if (key.KeyMaterial is null)
        {
            return false; // erased: the key is gone and is never recreated
        }

        db.SubjectFacts.Add(new SubjectFactRow
        {
            FactId = Guid.CreateVersion7(),
            SubjectToken = subjectToken,
            Category = category,
            FactKey = factKey,
            Cipher = SubjectCrypto.Seal(key.KeyMaterial, value),
            CreatedAt = clock.Now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
