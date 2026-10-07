namespace CoreIns.IntegrationTests;

/// <summary>Locates repository files so tests use the same scripts as the local stack.</summary>
internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string PgInit => Path.Combine(Root, "infra", "local", "pg-init");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreIns.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("CoreIns.sln not found above " + AppContext.BaseDirectory);
    }
}
