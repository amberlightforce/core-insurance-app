namespace CoreIns.ContractGen;

/// <summary>Module codes, owner projects, namespaces and output paths (repository-relative, '/' separators).</summary>
internal sealed class Layout(string repoRoot)
{
    /// <summary>Module code → module name (the <c>CoreIns.Modules.&lt;Name&gt;</c> project; PLT is <c>CoreIns.Platform</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Modules = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["BIL"] = "Billing",
        ["CHN"] = "Channels",
        ["CLM"] = "Claims",
        ["CMP"] = "Compliance",
        ["DAT"] = "Data",
        ["DOC"] = "Documents",
        ["FIN"] = "Finance",
        ["MIG"] = "Migration",
        ["MKT"] = "Market",
        ["PFC"] = "Product",
        ["PLT"] = "Platform",
        ["POL"] = "Policy",
        ["PTY"] = "Party",
        ["RAT"] = "Rating",
        ["RI"] = "Reinsurance",
        ["UW"] = "Underwriting",
        ["WRK"] = "Work",
    };

    public string RepoRoot { get; } = Path.GetFullPath(repoRoot);

    public static string Name(string module) => Modules[module];

    /// <summary>The owner contracts project directory (repo-relative).</summary>
    public static string ContractsProject(string module) =>
        module == "PLT" ? "src/CoreIns.Platform.Contracts" : $"src/CoreIns.Modules.{Name(module)}.Contracts";

    public static string RootNamespace(string module) =>
        module == "PLT" ? "CoreIns.Platform.Contracts" : $"CoreIns.Modules.{Name(module)}.Contracts";

    public static string ApiNamespace(string module) => RootNamespace(module) + ".Api";

    public static string EventsNamespace(string module) => RootNamespace(module) + ".Events";

    public static string Generated(string module) => ContractsProject(module) + "/Generated";

    public const string TestingProject = "tests/CoreIns.Testing.Contracts";

    public const string ContractTestsProject = "tests/CoreIns.Contracts.Tests";

    public const string FakesNamespace = "CoreIns.Testing.Contracts.Fakes";

    /// <summary>Every directory the generator owns completely (stale files there fail <c>--check</c>).</summary>
    public static IEnumerable<string> OwnedDirectories =>
        Modules.Keys.Select(Generated).Append(TestingProject + "/Generated").Append(ContractTestsProject + "/Generated");

    public static string ModuleOfOpenApiFile(string file)
    {
        var code = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        return Modules.ContainsKey(code) ? code : throw new InvalidDataException($"{file} is not a module OpenAPI document.");
    }
}
