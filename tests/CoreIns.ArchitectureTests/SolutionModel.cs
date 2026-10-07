using NetArchTest.Rules;
using System.Xml.Linq;

namespace CoreIns.ArchitectureTests;

/// <summary>What the architecture rules know about the solution: module names, projects and their references.</summary>
internal static class SolutionModel
{
    public const string ModulePrefix = "CoreIns.Modules.";
    public const string ContractsSuffix = ".Contracts";
    public const string CountryPackPrefix = "CoreIns.CountryPacks.";
    public const string SharedKernel = "CoreIns.SharedKernel";
    public const string Platform = "CoreIns.Platform";
    public const string Host = "CoreIns.Host";
    public const string DataProtection = "CoreIns.Platform.DataProtection";
    public const string PlatformContracts = "CoreIns.Platform.Contracts";

    /// <summary>The business modules of INFRASTRUCTURE §2 (Platform is <see cref="Platform"/>).</summary>
    public static IReadOnlyList<string> ModuleNames { get; } =
    [
        "Party", "Product", "Rating", "Underwriting", "Policy", "Billing", "Claims", "Reinsurance",
        "Finance", "Documents", "Compliance", "Channels", "Work", "Data", "Migration", "Market",
    ];

    public static IReadOnlyList<string> CountryPacks { get; } = ["CoreIns.CountryPacks.GR", "CoreIns.CountryPacks.CY"];

    public static string Implementation(string module) => ModulePrefix + module;

    public static string Contracts(string module) => ModulePrefix + module + ContractsSuffix;

    /// <summary>All module implementation and contract projects.</summary>
    public static IEnumerable<string> ModuleProjects =>
        ModuleNames.SelectMany(module => new[] { Implementation(module), Contracts(module) });

    /// <summary>Core = everything except the Host (composition root) and the country packs.</summary>
    public static IEnumerable<string> CoreProjects =>
        ModuleProjects.Prepend(PlatformContracts).Prepend(DataProtection).Prepend(Platform).Prepend(SharedKernel);

    /// <summary>The repository root (the directory containing CoreIns.sln).</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ProjectFile(string project) => Path.Combine(RepositoryRoot, "src", project, project + ".csproj");

    /// <summary>Names of the projects a project references directly.</summary>
    public static IReadOnlyList<string> ProjectReferences(string project) =>
        XDocument.Load(ProjectFile(project))
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(
                ((string?)element.Attribute("Include") ?? string.Empty).Replace('\\', Path.DirectorySeparatorChar)))
            .ToList();

    /// <summary>
    /// Path of the compiled assembly next to the tests. Rules inspect metadata from the file (NetArchTest/Mono.Cecil)
    /// and never load it for execution.
    /// </summary>
    public static string AssemblyPath(string project) => Path.Combine(AppContext.BaseDirectory, project + ".dll");

    /// <summary>NetArchTest type set read from the assembly file.</summary>
    public static Types TypesOf(string project) => Types.FromFile(AssemblyPath(project));

    /// <summary>True when <paramref name="name"/> (an assembly or namespace) belongs to another module's implementation.</summary>
    public static bool IsOtherModuleImplementation(string name, string ownModule)
    {
        foreach (var module in ModuleNames.Where(m => m != ownModule))
        {
            var implementation = Implementation(module);
            var isInModule = name == implementation || name.StartsWith(implementation + ".", StringComparison.Ordinal);
            var isContracts = name == Contracts(module) || name.StartsWith(Contracts(module) + ".", StringComparison.Ordinal);
            if (isInModule && !isContracts)
            {
                return true;
            }
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreIns.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("CoreIns.sln not found above " + AppContext.BaseDirectory);
    }
}
