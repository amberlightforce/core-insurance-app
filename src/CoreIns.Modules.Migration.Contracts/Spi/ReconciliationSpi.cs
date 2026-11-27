namespace CoreIns.Modules.Migration.Contracts.Spi;

/// <summary>
/// One aggregate a module holds for migrated data: a count and an amount per object type, optional sub-key and currency (BO-MIG-10).
/// The legacy control totals are compared with these figures to the cent.
/// </summary>
/// <param name="ObjectType">Import object type, e.g. <c>Receivable</c>, <c>OpeningBalance</c>, <c>Claim</c>, <c>TreatyYear</c>.</param>
/// <param name="Key">Sub-dimension of the figure (e.g. a ledger account code); empty for the whole object type.</param>
/// <param name="Currency">ISO currency code.</param>
/// <param name="Count">Number of migrated objects.</param>
/// <param name="Amount">Sum of the module's migrated amount (receivable outstanding, opening balance debit, opening reserve, opening aggregate).</param>
public sealed record ReconciliationFigure(string ObjectType, string Key, string Currency, long Count, decimal Amount);

/// <summary>
/// Implemented by a module that accepts migrated data, as a scoped service: reads (never writes) what the module holds of migrated objects
/// in the caller's legal entity, so the Migration module can reconcile it with the legacy control totals without reading the module schema.
/// </summary>
public interface IMigrationReconciliationReader
{
    /// <summary>The module code, e.g. <c>BIL</c>.</summary>
    string ModuleName { get; }

    /// <summary>The module figures, optionally restricted to one batch; migrated objects only.</summary>
    Task<IReadOnlyList<ReconciliationFigure>> ReadAsync(Guid? batchId, CancellationToken cancellationToken);
}
