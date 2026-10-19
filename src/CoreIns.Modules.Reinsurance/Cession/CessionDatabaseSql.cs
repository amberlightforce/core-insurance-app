using System.Globalization;
using System.Text;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>Seal triggers of the cession migration: the premium ledger, the cession rows, the exception log and the tracker are append-only (PITFALLS 8).</summary>
internal static class CessionDatabaseSql
{
    private static readonly string[] SealedTables = ["premium_delta", "cession", "cession_exception", "tracker"];

    public static readonly string Up = Build(create: true);

    public static readonly string Down = Build(create: false);

    private static string Build(bool create)
    {
        var sql = new StringBuilder();
        foreach (var table in SealedTables)
        {
            if (create)
            {
                sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_seal BEFORE UPDATE OR DELETE ON ri.{table} FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();");
                sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_no_truncate BEFORE TRUNCATE ON ri.{table} FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();");
            }
            else
            {
                sql.AppendLine(CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_seal ON ri.{table};");
                sql.AppendLine(CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_no_truncate ON ri.{table};");
            }
        }

        return sql.ToString();
    }
}
