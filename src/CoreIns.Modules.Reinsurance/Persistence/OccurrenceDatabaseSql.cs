namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// The SQL the occurrence / adjustment migration adds beside the EF model: <c>ri.recovery_adjustment</c> is append-only for every
/// writer, the app role included (PITFALLS 8, D-ARC-34): a manual adjustment is corrected only by a later adjustment.
/// </summary>
internal static class OccurrenceDatabaseSql
{
    /// <summary>Statements run by the migration's Up.</summary>
    public const string Up =
        "CREATE TRIGGER tr_recovery_adjustment_seal BEFORE UPDATE OR DELETE ON ri.recovery_adjustment FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();\n" +
        "CREATE TRIGGER tr_recovery_adjustment_no_truncate BEFORE TRUNCATE ON ri.recovery_adjustment FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();\n";

    /// <summary>Statements run by the migration's Down (before the tables are dropped).</summary>
    public const string Down =
        "DROP TRIGGER IF EXISTS tr_recovery_adjustment_seal ON ri.recovery_adjustment;\n" +
        "DROP TRIGGER IF EXISTS tr_recovery_adjustment_no_truncate ON ri.recovery_adjustment;\n";
}
