using CoreIns.Host.Hosting;
using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NetArchTest.Rules;

namespace CoreIns.ArchitectureTests;

/// <summary>
/// F-1b rules: the platform never depends on modules; modules depend only on the platform, the shared kernel and
/// other modules' contracts; each module's DbContext is private to it and maps only its own schema; nothing reads the
/// system clock except the time service (contract §3.9.13, REQ-PLT-332).
/// </summary>
public sealed class PlatformRulesTests
{
    public static TheoryData<string> Modules => [.. SolutionModel.ModuleNames];

    /// <summary>Every production assembly (core, host, country packs).</summary>
    public static TheoryData<string> ProductionAssemblies =>
        [.. SolutionModel.CoreProjects.Append(SolutionModel.Host).Concat(SolutionModel.CountryPacks)];

    [Fact]
    public void Platform_never_references_a_module()
    {
        SolutionModel.ProjectReferences(SolutionModel.Platform).ShouldBe([SolutionModel.SharedKernel]);

        var result = SolutionModel.TypesOf(SolutionModel.Platform)
            .ShouldNot().HaveDependencyOnAny("CoreIns.Modules", "CoreIns.CountryPacks", SolutionModel.Host)
            .GetResult();
        result.IsSuccessful.ShouldBeTrue($"Platform depends on: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    /// <summary>
    /// A module may use the platform (CoreIns.Platform and its sub-libraries such as DataProtection), the shared kernel,
    /// the shared rule engine (D-ARC-10) and other modules' contracts — nothing else.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modules))]
    public void A_module_references_only_the_platform_the_shared_kernel_and_contracts(string module)
    {
        var implementation = SolutionModel.Implementation(module);
        SolutionModel.ProjectReferences(implementation)
            .Where(reference => reference != SolutionModel.Platform && reference != SolutionModel.SharedKernel
                                && !reference.StartsWith(SolutionModel.Platform + ".", StringComparison.Ordinal)
                                && reference != "CoreIns.Rules"
                                && !reference.EndsWith(SolutionModel.ContractsSuffix, StringComparison.Ordinal))
            .ShouldBeEmpty($"{implementation} may reference only Platform, SharedKernel, the rule engine and *.Contracts");

        var contracts = SolutionModel.Contracts(module);
        SolutionModel.ProjectReferences(contracts)
            .Where(reference => reference != SolutionModel.SharedKernel && !reference.EndsWith(SolutionModel.ContractsSuffix, StringComparison.Ordinal))
            .ShouldBeEmpty($"{contracts} may reference only SharedKernel and other *.Contracts");
    }

    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void DbContexts_are_private_to_the_platform_or_their_module(string project)
    {
        using var assembly = Read(project);
        var contexts = assembly.MainModule.Types
            .SelectMany(Nested)
            .Where(type => !type.IsAbstract && DerivesFromDbContext(type))
            .ToList();

        if (project == SolutionModel.Platform || (project.StartsWith(SolutionModel.ModulePrefix, StringComparison.Ordinal)
                                                  && !project.EndsWith(SolutionModel.ContractsSuffix, StringComparison.Ordinal)))
        {
            contexts.Where(type => type.IsPublic || type.IsNestedPublic)
                .Select(type => type.FullName)
                .ShouldBeEmpty("a DbContext is internal to its module: no other module can use it");
        }
        else
        {
            contexts.Select(type => type.FullName).ShouldBeEmpty($"{project} must not contain a DbContext");
        }
    }

    [Fact]
    public void Each_module_database_maps_only_its_own_schema()
    {
        ModuleCatalog.Databases.ShouldNotBeEmpty();
        foreach (var database in ModuleCatalog.Databases)
        {
            using var context = database.CreateForMigration("Host=localhost;Database=design;Username=design");
            var schemas = context.Model.GetEntityTypes().Select(entity => entity.GetSchema()).Distinct().ToList();
            schemas.ShouldBe([database.Schema], $"{database.Module} maps only schema {database.Schema}");
            ModuleCatalog.Schemas.ShouldContain(database.Schema);
        }
    }

    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void Nothing_reads_the_system_clock_except_the_time_service(string project)
    {
        using var assembly = Read(project);
        var offenders = new List<string>();
        foreach (var type in assembly.MainModule.Types.SelectMany(Nested))
        {
            if (type.FullName == "CoreIns.Platform.Time.SystemClock")
            {
                continue;
            }

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Ldftn)
                        && instruction.Operand is MethodReference target
                        && IsSystemClock(target))
                    {
                        offenders.Add($"{type.FullName}.{method.Name} → {target.DeclaringType.Name}.{target.Name}");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty("use CoreIns.Platform.Time.IClock");
    }

    [Fact]
    public void The_system_clock_analyzer_is_suppressed_only_in_the_time_service()
    {
        var allowed = Path.Combine("src", "CoreIns.Platform", "Time", "Clock.cs");
        var suppressions = Directory.EnumerateFiles(Path.Combine(SolutionModel.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("COREINS002", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(SolutionModel.RepositoryRoot, file))
            .ToList();

        suppressions.ShouldBe([allowed]);
    }

    private static bool IsSystemClock(MethodReference method) => method.DeclaringType.FullName switch
    {
        "System.DateTime" => method.Name is "get_Now" or "get_UtcNow" or "get_Today",
        "System.DateTimeOffset" => method.Name is "get_Now" or "get_UtcNow",
        _ => false,
    };

    private static bool DerivesFromDbContext(TypeDefinition type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.FullName == "Microsoft.EntityFrameworkCore.DbContext")
            {
                return true;
            }

            current = current.Resolve()?.BaseType;
        }

        return false;
    }

    private static IEnumerable<TypeDefinition> Nested(TypeDefinition type) => type.NestedTypes.SelectMany(Nested).Prepend(type);

    private static AssemblyDefinition Read(string project)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        return AssemblyDefinition.ReadAssembly(SolutionModel.AssemblyPath(project), new ReaderParameters { AssemblyResolver = resolver });
    }
}
