using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>Redrive body.</summary>
internal sealed record RedriveBatchRequest(Guid BatchId);

/// <summary>
/// mig.Batch.redrive: runs the FAILED items of an approved batch again (the approval covers the batch content, MIG-005).
/// Only COMPLETED_WITH_ERRORS batches qualify; items already imported are never touched (the runner skips active xrefs).
/// </summary>
internal sealed record RedriveBatch(RedriveBatchRequest Request) : ICommand<BatchResponse>;

internal sealed class RedriveBatchHandler(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, BatchRunner runner)
    : ICommandHandler<RedriveBatch, BatchResponse>
{
    public async Task<Result<BatchResponse>> HandleAsync(RedriveBatch command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var batch = await Batches.LoadAsync(db, legalEntity, command.Request.BatchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return MigrationErrors.NotFound("The batch");
        }

        if (batch.Status != BatchStatus.CompletedWithErrors || batch.ApprovalRequestId is null)
        {
            return MigrationErrors.State($"A batch in status {batch.Status} cannot be redriven; only an approved batch that completed with errors can.");
        }

        var failed = await db.Items.Where(i => i.BatchId == batch.BatchId && i.Outcome == ItemOutcome.Failed).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        await runner.RunAsync(batch, failed, dryRun: false, cancellationToken).ConfigureAwait(false);
        var stillFailed = failed.Any(i => i.Outcome == ItemOutcome.Failed);
        batch.Status = stillFailed ? BatchStatus.CompletedWithErrors : BatchStatus.Completed;
        batch.UpdatedAt = clock.Now;
        batch.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await Batches.ResponseAsync(db, batch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Conformance body: the module code and optionally one operation (import or reverse).</summary>
internal sealed record ConformanceBody(string Module, string? Operation);

/// <summary>One checked element of the import contract.</summary>
internal sealed record ConformanceElement(string Element, bool Passed, string Detail);

/// <summary>Conformance result.</summary>
internal sealed record ConformanceResponse(string Module, bool Passed, IReadOnlyList<ConformanceElement> PassFailPerContractElement);

/// <summary>mig.ImportContract.conformance: static check of the module's import SPI registrations (importer, reverser). Runs nothing and writes nothing.</summary>
internal sealed record CheckConformance(ConformanceBody Body) : ICommand<ConformanceResponse>;

internal sealed class CheckConformanceHandler(IEnumerable<IMigrationImporter> importers, IEnumerable<IMigrationReverser> reversers)
    : ICommandHandler<CheckConformance, ConformanceResponse>
{
    private static readonly HashSet<string> Operations = new(StringComparer.OrdinalIgnoreCase) { "import", "reverse" };

    public Task<Result<ConformanceResponse>> HandleAsync(CheckConformance command, CancellationToken cancellationToken)
    {
        var body = command.Body;
        var module = body.Module?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(module) || module.Length > 8 || !Enum.TryParse<ModuleCode>(module, ignoreCase: false, out _))
        {
            return Task.FromResult<Result<ConformanceResponse>>(DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "module must be a known module code."));
        }

        if (!string.IsNullOrEmpty(body.Operation) && !Operations.Contains(body.Operation))
        {
            return Task.FromResult<Result<ConformanceResponse>>(DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "operation must be import or reverse."));
        }

        var elements = new List<ConformanceElement>();
        var all = string.IsNullOrEmpty(body.Operation);
        if (all || string.Equals(body.Operation, "import", StringComparison.OrdinalIgnoreCase))
        {
            var count = importers.Count(i => string.Equals(i.ModuleName, module, StringComparison.OrdinalIgnoreCase));
            elements.Add(new ConformanceElement("IMigrationImporter", count == 1, count switch { 0 => "No importer is registered.", 1 => "Registered.", _ => "More than one importer is registered." }));
        }

        if (all || string.Equals(body.Operation, "reverse", StringComparison.OrdinalIgnoreCase))
        {
            var count = reversers.Count(r => string.Equals(r.ModuleName, module, StringComparison.OrdinalIgnoreCase));
            elements.Add(new ConformanceElement("IMigrationReverser", count == 1, count switch { 0 => "No reverser is registered; batches of this module are NOT_REVERSIBLE.", 1 => "Registered.", _ => "More than one reverser is registered." }));
        }

        // TODO(hardening): also run the module importer against a sample payload (dry run) to check idempotency and error codes.
        return Task.FromResult<Result<ConformanceResponse>>(new ConformanceResponse(module, elements.All(e => e.Passed), elements));
    }
}
