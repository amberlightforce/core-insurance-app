using System.Globalization;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>Shared loading and view building of batches.</summary>
internal static class Batches
{
    public static async Task<BatchRow?> LoadAsync(MigrationDbContext db, LegalEntityId legalEntity, Guid batchId, CancellationToken cancellationToken) =>
        await db.Batches.SingleOrDefaultAsync(b => b.BatchId == batchId && b.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);

    public static async Task<BatchView> ViewAsync(MigrationDbContext db, BatchRow batch, CancellationToken cancellationToken)
    {
        var counts = await db.Items.AsNoTracking().Where(i => i.BatchId == batch.BatchId)
            .GroupBy(i => i.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new BatchView(
            batch.BatchId, batch.Name, batch.Status, batch.BaselineHash, batch.ContentHash,
            batch.DryRunHash is not null && batch.DryRunHash == batch.ContentHash, batch.StartedBy, batch.ApprovalRequestId,
            counts.Sum(c => c.Count), counts.ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal));
    }

    public static OutcomeView Outcome(BatchItemRow i) => new(i.Seq, i.ModuleName, i.ObjectType, i.LegacyId, i.Outcome, i.CoreId, i.ErrorCode, i.Message);

    public static async Task<BatchResponse> ResponseAsync(MigrationDbContext db, BatchRow batch, CancellationToken cancellationToken)
    {
        var items = await db.Items.AsNoTracking().Where(i => i.BatchId == batch.BatchId).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new BatchResponse(await ViewAsync(db, batch, cancellationToken).ConfigureAwait(false), [.. items.Select(Outcome)]);
    }
}

/// <summary>The content hash of a batch: its ordered items and the baseline hash, recomputable from the stored rows (NX-EC-09).</summary>
internal static class BatchContent
{
    public static string Canonical(IEnumerable<(string Module, string ObjectType, string LegacyId, string Payload)> items, string? baseline) =>
        string.Join('\n', items.Select(i => $"{i.Module}|{i.ObjectType}|{i.LegacyId}|{i.Payload}")) + "\n" + baseline;

    /// <summary>The hash of the items as they are stored now. Differs from the stored <c>ContentHash</c> when a row changed after creation.</summary>
    public static string CurrentHash(BatchRow batch, IEnumerable<BatchItemRow> items) =>
        Sha256Hash.ComputeUtf8(Canonical(items.OrderBy(i => i.Seq).Select(i => (i.ModuleName, i.ObjectType, i.LegacyId, i.Payload)), batch.BaselineHash)).Value;
}

/// <summary>mig.Batch.create: stores the ordered items of a batch and records the baseline configuration hash H0.</summary>
internal sealed record CreateBatch(CreateBatchRequest Request) : ICommand<BatchResponse>;

internal sealed class CreateBatchValidator : AbstractValidator<CreateBatch>
{
    public CreateBatchValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Items).NotEmpty().Must(i => i.Count <= 5000).WithMessage("A batch holds at most 5000 items.");
        RuleForEach(c => c.Request.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Module).NotEmpty().MaximumLength(8);
            item.RuleFor(i => i.ObjectType).NotEmpty().MaximumLength(64);
            item.RuleFor(i => i.LegacyId).NotEmpty().MaximumLength(200);
            item.RuleFor(i => i.Payload).Must(p => p.ValueKind != System.Text.Json.JsonValueKind.Undefined).WithMessage("A payload is required.");
        });
    }
}

internal sealed class CreateBatchHandler(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IServiceProvider services)
    : ICommandHandler<CreateBatch, BatchResponse>
{
    public async Task<Result<BatchResponse>> HandleAsync(CreateBatch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var items = request.Items.Select(i => i with { Module = i.Module.ToUpperInvariant() }).ToList();
        var dup = items.GroupBy(i => (i.Module, i.ObjectType, i.LegacyId)).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"Legacy id {dup.Key.Module}/{dup.Key.ObjectType}/{dup.Key.LegacyId} appears twice in the batch.");
        }

        // H0: the configuration state the migrated transactions reference (SL5 MKT state history).
        string? baseline = null;
        if (services.GetService<IMarketConfigurationService>() is { } market)
        {
            baseline = (await market.CurrentHashAsync(cancellationToken).ConfigureAwait(false)).Hash?.Value;
        }

        var canonical = string.Join('\n', items.Select(i => $"{i.Module}|{i.ObjectType}|{i.LegacyId}|{i.Payload.GetRawText()}")) + "\n" + baseline;
        var now = clock.Now;
        var batch = new BatchRow
        {
            BatchId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            Name = request.Name,
            Status = BatchStatus.Draft,
            BaselineHash = baseline,
            ContentHash = Sha256Hash.ComputeUtf8(canonical).Value,
            CreatedAt = now,
            UpdatedAt = now,
            RecordVersion = 1,
        };
        db.Batches.Add(batch);
        var seq = 0;
        foreach (var item in items)
        {
            db.Items.Add(new BatchItemRow
            {
                ItemId = Guid.CreateVersion7(), BatchId = batch.BatchId, Seq = ++seq, ModuleName = item.Module, ObjectType = item.ObjectType,
                LegacyId = item.LegacyId, Payload = item.Payload.GetRawText(), Outcome = ItemOutcome.Pending,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await Batches.ResponseAsync(db, batch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Dispatches batch items to the module importers. Idempotent: an item whose legacy id has an active xref is
/// SKIPPED_EXISTING and the importer is not called.
/// </summary>
internal sealed class BatchRunner(MigrationDbContext db, IEnumerable<IMigrationImporter> importers, RequestContext context, IClock clock)
{
    public async Task<List<OutcomeView>> RunAsync(BatchRow batch, IReadOnlyList<BatchItemRow> items, bool dryRun, CancellationToken cancellationToken)
    {
        var results = new List<OutcomeView>(items.Count);
        var importContext = new MigrationImportContext(batch.BatchId, dryRun, batch.BaselineHash);
        var origin = context.Origin;
        context.Origin = EventOrigin.Migration;
        try
        {
            foreach (var item in items.OrderBy(i => i.Seq))
            {
                await RunItemAsync(batch, item, importContext, dryRun, cancellationToken).ConfigureAwait(false);
                results.Add(Batches.Outcome(item));
            }
        }
        finally
        {
            context.Origin = origin;
        }

        return results;
    }

    private async Task RunItemAsync(BatchRow batch, BatchItemRow item, MigrationImportContext importContext, bool dryRun, CancellationToken cancellationToken)
    {
        var xref = await db.Xrefs.SingleOrDefaultAsync(
            x => x.LegalEntityId == batch.LegalEntityId && x.Source == item.ModuleName && x.ObjectType == item.ObjectType && x.LegacyKey == item.LegacyId,
            cancellationToken).ConfigureAwait(false);
        if (xref is { Status: "ACTIVE" })
        {
            Set(item, ItemOutcome.SkippedExisting, xref.CoreId, null, null);
            return;
        }

        var importer = importers.FirstOrDefault(i => string.Equals(i.ModuleName, item.ModuleName, StringComparison.OrdinalIgnoreCase));
        if (importer is null)
        {
            Set(item, ItemOutcome.Failed, null, "MIG-ERR-DEPENDENCY", $"Module {item.ModuleName} has no migration importer.");
            return;
        }

        using var payload = System.Text.Json.JsonDocument.Parse(item.Payload);
        var outcome = await importer.ImportAsync(new MigrationImportItem(item.ObjectType, item.LegacyId, payload.RootElement.Clone()), importContext, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status == ImportStatus.Failed || (!dryRun && outcome.CoreId is null))
        {
            Set(item, ItemOutcome.Failed, null, outcome.ErrorCode ?? "MIG-ERR-VALIDATION", outcome.Message ?? "The importer did not return a core id.");
            return;
        }

        // A dry run reports what would be imported; nothing is stored for it.
        Set(item, ItemOutcome.Imported, outcome.CoreId, null, outcome.Message);
        if (dryRun)
        {
            return;
        }

        if (xref is null)
        {
            db.Xrefs.Add(new XrefRow
            {
                XrefId = Guid.CreateVersion7(), LegalEntityId = batch.LegalEntityId, Source = item.ModuleName, ObjectType = item.ObjectType,
                LegacyKey = item.LegacyId, CoreId = outcome.CoreId!.Value, BatchId = batch.BatchId, CreatedAt = clock.Now,
            });
        }
        else
        {
            // A reversed object imported again.
            xref.CoreId = outcome.CoreId!.Value;
            xref.BatchId = batch.BatchId;
            xref.Status = "ACTIVE";
        }
    }

    private static void Set(BatchItemRow item, string outcome, Guid? coreId, string? errorCode, string? message)
    {
        item.Outcome = outcome;
        item.CoreId = coreId;
        item.ErrorCode = errorCode;
        item.Message = message;
    }
}

/// <summary>mig.Batch.start: a dry run (writes nothing) or the real run request (needs approval by another user, MIG-005).</summary>
internal sealed record StartBatch(StartBatchRequest Request) : ICommand<BatchResponse>;

internal sealed class StartBatchHandler(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, BatchRunner runner, IServiceProvider services)
    : ICommandHandler<StartBatch, BatchResponse>
{
    public async Task<Result<BatchResponse>> HandleAsync(StartBatch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var batch = await Batches.LoadAsync(db, legalEntity, request.BatchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return MigrationErrors.NotFound("The batch");
        }

        if (batch.Status != BatchStatus.Draft)
        {
            return MigrationErrors.State($"A batch in status {batch.Status} cannot be started.");
        }

        var items = await db.Items.Where(i => i.BatchId == batch.BatchId).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        if (request.DryRun)
        {
            var outcomes = await runner.RunAsync(batch, items, dryRun: true, cancellationToken).ConfigureAwait(false);

            // Item rows were only used to carry the dry-run results: nothing of them is stored.
            db.ChangeTracker.Entries<BatchItemRow>().ToList().ForEach(e => e.State = EntityState.Detached);
            // The dry run vouches for the content as it is stored now, not for the hash written at creation.
            batch.ContentHash = BatchContent.CurrentHash(batch, items);
            batch.DryRunHash = batch.ContentHash;
            batch.DryRunAt = now;
            batch.UpdatedAt = now;
            batch.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new BatchResponse(await Batches.ViewAsync(db, batch, cancellationToken).ConfigureAwait(false), outcomes);
        }

        if (batch.DryRunHash != batch.ContentHash)
        {
            return DomainError.Of(ModuleCode.MIG, "DRYRUN-REQUIRED", "Run the batch dry before the real run.");
        }

        // A content change after the dry run invalidates it (NX-EC-09): the approval must cover what was dry-run.
        if (BatchContent.CurrentHash(batch, items) != batch.ContentHash)
        {
            return DomainError.Of(ModuleCode.MIG, "DRYRUN-REQUIRED", "The batch content changed after the dry run; run the batch dry again.");
        }

        var approvals = services.GetService<IPlatformApprovalService>();
        if (approvals is null)
        {
            return DomainError.Of(ModuleCode.MIG, "APPROVAL-REQUIRED", "The approval service is not available.");
        }

        try
        {
            var response = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = BatchApproval.Type,
                    ObjectRef = BatchApproval.Subject(batch.BatchId),
                    PayloadHash = Sha256Hash.Parse(batch.ContentHash),
                    Authority = new ApprovalAuthority
                    {
                        Type = BatchApproval.Type,
                        Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [BatchApproval.ScopeDimension] = BatchApproval.Scope },
                    },
                    ReferralRole = BatchApproval.ReferralRole,
                    Reason = $"Run migration batch '{batch.Name}' ({items.Count} items, baseline {batch.BaselineHash ?? "none"}).",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            batch.ApprovalRequestId = response.Request.RequestId;
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        batch.Status = BatchStatus.PendingApproval;
        batch.StartedBy = context.Actor.ToString();
        batch.UpdatedAt = now;
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await Batches.ResponseAsync(db, batch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>mig.Batch.approve: a user other than the starter approves (and so runs) or rejects the real run.</summary>
internal sealed record ApproveBatch(ApproveBatchRequest Request) : ICommand<BatchResponse>;

internal sealed class ApproveBatchHandler(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, BatchRunner runner, IServiceProvider services)
    : ICommandHandler<ApproveBatch, BatchResponse>
{
    public async Task<Result<BatchResponse>> HandleAsync(ApproveBatch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var batch = await Batches.LoadAsync(db, legalEntity, request.BatchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return MigrationErrors.NotFound("The batch");
        }

        if (batch.Status != BatchStatus.PendingApproval || batch.ApprovalRequestId is not { } approvalRequestId)
        {
            return MigrationErrors.State($"A batch in status {batch.Status} is not waiting for approval.");
        }

        var reject = string.Equals(request.Decision, "REJECT", StringComparison.OrdinalIgnoreCase);
        if (!reject && !string.Equals(request.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "Decision must be APPROVE or REJECT.");
        }

        // The content must still be what was dry-run and approved (NX-EC-09), checked before deciding or running anything.
        var storedItems = await db.Items.AsNoTracking().Where(i => i.BatchId == batch.BatchId).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (BatchContent.CurrentHash(batch, storedItems) != batch.ContentHash)
        {
            return DomainError.Of(ModuleCode.MIG, "APPROVAL-STALE", "The batch content changed after the dry run and the approval request; run it dry and request approval again.");
        }

        var approvals = services.GetRequiredService<IPlatformApprovalService>();
        var hash = Sha256Hash.Parse(batch.ContentHash);
        var now = clock.Now;
        try
        {
            // Platform maker-checker: a human other than the starter, holding MIG.BATCH_RUN authority.
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = approvalRequestId,
                    PayloadHash = hash,
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                    Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);

            if (!reject)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = approvalRequestId, Hash = hash, Type = BatchApproval.Type, ObjectRef = BatchApproval.Subject(batch.BatchId) },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok)
                {
                    return DomainError.Of(ModuleCode.MIG, "APPROVAL-REQUIRED", "The approval does not cover this batch content.");
                }
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        if (reject)
        {
            batch.Status = BatchStatus.Rejected;
        }
        else
        {
            batch.Status = BatchStatus.Running;
            var items = await db.Items.Where(i => i.BatchId == batch.BatchId).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
            await runner.RunAsync(batch, items, dryRun: false, cancellationToken).ConfigureAwait(false);
            batch.Status = items.Any(i => i.Outcome == ItemOutcome.Failed) ? BatchStatus.CompletedWithErrors : BatchStatus.Completed;
        }

        batch.UpdatedAt = now;
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await Batches.ResponseAsync(db, batch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>mig.Batch.pause.</summary>
internal sealed record PauseBatch(Guid BatchId) : ICommand<BatchResponse>;

/// <summary>mig.Batch.resume.</summary>
internal sealed record ResumeBatch(Guid BatchId) : ICommand<BatchResponse>;

internal sealed class PauseBatchHandler(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<PauseBatch, BatchResponse>, ICommandHandler<ResumeBatch, BatchResponse>
{
    public Task<Result<BatchResponse>> HandleAsync(PauseBatch command, CancellationToken cancellationToken) => ChangeAsync(command.BatchId, pause: true, cancellationToken);

    public Task<Result<BatchResponse>> HandleAsync(ResumeBatch command, CancellationToken cancellationToken) => ChangeAsync(command.BatchId, pause: false, cancellationToken);

    private async Task<Result<BatchResponse>> ChangeAsync(Guid batchId, bool pause, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var batch = await Batches.LoadAsync(db, legalEntity, batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return MigrationErrors.NotFound("The batch");
        }

        // TODO(hardening): batches run in one request, so only a batch that has not run yet can be held.
        if (pause)
        {
            if (batch.Status is not (BatchStatus.Draft or BatchStatus.PendingApproval))
            {
                return MigrationErrors.State($"A batch in status {batch.Status} cannot be paused.");
            }

            batch.PausedFrom = batch.Status;
            batch.Status = BatchStatus.Paused;
        }
        else
        {
            if (batch.Status != BatchStatus.Paused || batch.PausedFrom is null)
            {
                return MigrationErrors.State("Only a paused batch can be resumed.");
            }

            batch.Status = batch.PausedFrom;
            batch.PausedFrom = null;
        }

        batch.UpdatedAt = clock.Now;
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await Batches.ResponseAsync(db, batch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// mig.Batch.reverse (D-SL6-05): each module's reverser undoes its untouched migrated objects; objects with post-migration
/// activity are listed MIG-ERR-REVERSAL-BLOCKED; a module without a reverser makes the batch NOT_REVERSIBLE.
/// </summary>
internal sealed record ReverseBatch(ReverseBatchRequest Request) : ICommand<BatchResponse>;

internal sealed class ReverseBatchHandler(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IEnumerable<IMigrationReverser> reversers)
    : ICommandHandler<ReverseBatch, BatchResponse>
{
    public async Task<Result<BatchResponse>> HandleAsync(ReverseBatch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var batch = await Batches.LoadAsync(db, legalEntity, request.BatchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return MigrationErrors.NotFound("The batch");
        }

        if (batch.Status is not (BatchStatus.Completed or BatchStatus.CompletedWithErrors or BatchStatus.ReversalBlocked or BatchStatus.NotReversible))
        {
            return MigrationErrors.State($"A batch in status {batch.Status} cannot be reversed.");
        }

        var items = await db.Items.Where(i => i.BatchId == batch.BatchId).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var imported = items.Where(i => i.Outcome is ItemOutcome.Imported or ItemOutcome.ReversalBlocked).ToList();
        var modules = imported.Select(i => i.ModuleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = modules.Where(m => !reversers.Any(r => string.Equals(r.ModuleName, m, StringComparison.OrdinalIgnoreCase))).ToList();
        var now = clock.Now;
        if (missing.Count > 0)
        {
            if (!request.DryRun)
            {
                batch.Status = BatchStatus.NotReversible;
                batch.UpdatedAt = now;
                batch.RecordVersion++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var view = await Batches.ViewAsync(db, batch, cancellationToken).ConfigureAwait(false);
            return new BatchResponse(request.DryRun ? view with { Status = BatchStatus.NotReversible } : view, [.. imported.Select(i => Batches.Outcome(i) with
            {
                ErrorCode = missing.Contains(i.ModuleName, StringComparer.OrdinalIgnoreCase) ? "MIG-ERR-NOT-REVERSIBLE" : null,
                Message = missing.Contains(i.ModuleName, StringComparer.OrdinalIgnoreCase) ? $"Module {i.ModuleName} has no reverser." : null,
            })]);
        }

        var results = new List<OutcomeView>();
        foreach (var module in modules)
        {
            var reverser = reversers.First(r => string.Equals(r.ModuleName, module, StringComparison.OrdinalIgnoreCase));
            var outcomes = await reverser.ReverseAsync(batch.BatchId, request.DryRun, cancellationToken).ConfigureAwait(false);
            foreach (var item in imported.Where(i => string.Equals(i.ModuleName, module, StringComparison.OrdinalIgnoreCase)))
            {
                var outcome = outcomes.FirstOrDefault(o => o.CoreId == item.CoreId && o.ObjectType == item.ObjectType);
                var reversed = outcome?.Reversed == true;
                var next = reversed ? ItemOutcome.Reversed : ItemOutcome.ReversalBlocked;
                var code = reversed ? null : outcome?.ErrorCode ?? MigrationReversalCodes.Blocked;
                var message = reversed ? null : outcome?.Message ?? "The module did not report the object as reversed.";
                results.Add(Batches.Outcome(item) with { Outcome = next, ErrorCode = code, Message = message });
                if (request.DryRun)
                {
                    continue;
                }

                item.Outcome = next;
                item.ErrorCode = code;
                item.Message = message;
                if (reversed)
                {
                    var xref = await db.Xrefs.SingleOrDefaultAsync(
                        x => x.LegalEntityId == legalEntity && x.Source == item.ModuleName && x.ObjectType == item.ObjectType && x.LegacyKey == item.LegacyId,
                        cancellationToken).ConfigureAwait(false);
                    xref?.Status = "REVERSED";
                }
            }
        }

        var allReversed = results.All(r => r.Outcome == ItemOutcome.Reversed);
        if (request.DryRun)
        {
            var preview = await Batches.ViewAsync(db, batch, cancellationToken).ConfigureAwait(false);
            return new BatchResponse(preview with { Status = allReversed ? BatchStatus.Reversed : BatchStatus.ReversalBlocked }, results);
        }

        batch.Status = allReversed ? BatchStatus.Reversed : BatchStatus.ReversalBlocked;
        batch.UpdatedAt = now;
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BatchResponse(await Batches.ViewAsync(db, batch, cancellationToken).ConfigureAwait(false), results);
    }
}

internal static class BatchCursor
{
    public static int Decode(string? cursor) => int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
