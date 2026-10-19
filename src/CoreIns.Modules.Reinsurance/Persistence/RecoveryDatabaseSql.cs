namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// The SQL the recovery migration adds beside the EF model: the seal triggers (PITFALLS 8, D-ARC-34). The recovery rows,
/// their batch (with the trace) and the intake exception log are append-only for every writer, the app role included:
/// a booked delta is corrected only by a later delta, never by an update or a delete.
/// </summary>
internal static class RecoveryDatabaseSql
{
    private static readonly string[] SealedTables = ["recovery_batch", "recovery", "intake_exception"];

    /// <summary>Statements run by the migration's Up.</summary>
    public static readonly string Up = Build("CREATE");

    /// <summary>Statements run by the migration's Down (before the tables are dropped).</summary>
    public static readonly string Down = Build("DROP");

    private static string Build(string verb)
    {
        var sql = new System.Text.StringBuilder();
        foreach (var table in SealedTables)
        {
            if (verb == "CREATE")
            {
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_seal BEFORE UPDATE OR DELETE ON ri.{table} FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();");
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_no_truncate BEFORE TRUNCATE ON ri.{table} FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();");
            }
            else
            {
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_seal ON ri.{table};");
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_no_truncate ON ri.{table};");
            }
        }

        return sql.ToString();
    }
}
