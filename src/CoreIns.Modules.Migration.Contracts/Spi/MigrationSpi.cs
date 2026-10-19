using System.Text.Json;

namespace CoreIns.Modules.Migration.Contracts.Spi;

/// <summary>Result status of importing one legacy object (SL6-MIG-FRAME).</summary>
public enum ImportStatus
{
    /// <summary>The object was created in the core (or, in a dry run, would be).</summary>
    Imported,

    /// <summary>The legacy id was already imported: nothing was written. Set by the framework from the xref registry.</summary>
    SkippedExisting,

    /// <summary>The importer refused the payload; <see cref="ImportOutcome.ErrorCode"/> says why.</summary>
    Failed,
}

/// <summary>One legacy object of a batch.</summary>
/// <param name="ObjectType">Module-specific object type, e.g. <c>Party</c>.</param>
/// <param name="LegacyId">Legacy key, unique per module and object type.</param>
/// <param name="Payload">The legacy payload in the module's import shape.</param>
public sealed record MigrationImportItem(string ObjectType, string LegacyId, JsonElement Payload);

/// <summary>Context of an import call.</summary>
/// <param name="BatchId">The batch; every imported object carries it with origin=MIGRATION.</param>
/// <param name="DryRun">True: validate only, write nothing.</param>
/// <param name="BaselineHash">Configuration-state hash H0 recorded for the batch (MKT state history); migrated transactions reference it.</param>
public sealed record MigrationImportContext(Guid BatchId, bool DryRun, string? BaselineHash);

/// <summary>Outcome of importing one object.</summary>
/// <param name="Status">Imported or Failed (the framework sets SkippedExisting itself).</param>
/// <param name="CoreId">Id of the core object (null on failure).</param>
/// <param name="ErrorCode">Machine-readable reason on failure.</param>
/// <param name="Message">Human-readable detail.</param>
public sealed record ImportOutcome(ImportStatus Status, Guid? CoreId = null, string? ErrorCode = null, string? Message = null);

/// <summary>
/// Implemented by a module that accepts migrated data, registered as a scoped service. The framework dispatches batch items by
/// <see cref="IMigrationImporter.ModuleName"/>. An importer must stamp origin=MIGRATION and the batch id on what it creates and must write nothing when
/// <see cref="MigrationImportContext.DryRun"/> is set. Idempotency is the framework's (xref registry): the importer is never
/// called for a legacy id that is already imported.
/// </summary>
public interface IMigrationImporter
{
    /// <summary>The module code items are routed by, e.g. <c>PTY</c>.</summary>
    string ModuleName { get; }

    /// <summary>Imports (or in a dry run, validates) one object.</summary>
    Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext context, CancellationToken cancellationToken);
}

/// <summary>Per-object result of a reversal.</summary>
/// <param name="ObjectType">Object type.</param>
/// <param name="CoreId">Core object id.</param>
/// <param name="Reversed">True when the object was (or in a dry run would be) removed.</param>
/// <param name="ErrorCode"><c>MIG-ERR-REVERSAL-BLOCKED</c> when post-migration activity prevents reversal.</param>
/// <param name="Message">Why it was blocked.</param>
public sealed record ReversalOutcome(string ObjectType, Guid CoreId, bool Reversed, string? ErrorCode = null, string? Message = null);

/// <summary>
/// Implemented by a module able to undo its migrated objects (D-SL6-05). It reverses only objects with origin=MIGRATION and the
/// given batch id that have no post-migration activity, and lists every other object as blocked. A module without a reverser
/// makes a batch holding its objects NOT_REVERSIBLE.
/// </summary>
public interface IMigrationReverser
{
    /// <summary>The module code.</summary>
    string ModuleName { get; }

    /// <summary>Reverses the module's objects of the batch (writes nothing when <paramref name="dryRun"/>).</summary>
    Task<IReadOnlyList<ReversalOutcome>> ReverseAsync(Guid batchId, bool dryRun, CancellationToken cancellationToken);
}

/// <summary>Reversal blocked code.</summary>
public static class MigrationReversalCodes
{
    /// <summary>The object has post-migration activity.</summary>
    public const string Blocked = "MIG-ERR-REVERSAL-BLOCKED";
}
