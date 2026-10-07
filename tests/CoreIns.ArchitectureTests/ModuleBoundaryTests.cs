using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mono.Cecil;
using NetArchTest.Rules;

namespace CoreIns.ArchitectureTests;

/// <summary>ADR §2 rule 8: module boundaries are enforced in code.</summary>
public sealed partial class ModuleBoundaryTests
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    public static TheoryData<string> Modules => [.. SolutionModel.ModuleNames];

    public static TheoryData<string> CoreProjects => [.. SolutionModel.CoreProjects];

    [Fact]
    public void Every_module_has_an_implementation_and_a_contracts_project()
    {
        foreach (var project in SolutionModel.ModuleProjects)
        {
            File.Exists(SolutionModel.ProjectFile(project)).ShouldBeTrue($"missing project {project}");
            using var assembly = ModuleDefinition.ReadModule(SolutionModel.AssemblyPath(project));
            assembly.Assembly.Name.Name.ShouldBe(project);
        }
    }

    [Theory]
    [InlineData("CoreIns.Modules.Billing", "Party", true)]
    [InlineData("CoreIns.Modules.Billing.Internal", "Party", true)]
    [InlineData("CoreIns.Modules.Billing.Contracts", "Party", false)]
    [InlineData("CoreIns.Modules.Billing.Contracts.Invoices", "Party", false)]
    [InlineData("CoreIns.Modules.Party", "Party", false)]
    [InlineData("CoreIns.Modules.PartyExtras", "Party", false)]
    [InlineData("CoreIns.SharedKernel", "Party", false)]
    public void Boundary_rule_tells_implementation_from_contracts(string name, string ownModule, bool violates) =>
        SolutionModel.IsOtherModuleImplementation(name, ownModule).ShouldBe(violates);

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_implementation_references_only_other_modules_contracts(string module)
    {
        var project = SolutionModel.Implementation(module);

        var badProjectReferences = SolutionModel.ProjectReferences(project)
            .Where(reference => SolutionModel.IsOtherModuleImplementation(reference, module)
                                || reference == SolutionModel.Host)
            .ToList();
        badProjectReferences.ShouldBeEmpty($"{project} references another module's implementation");

        var rule = new NoOtherModuleImplementationRule(module);
        var result = SolutionModel.TypesOf(project).Should().MeetCustomRule(rule).GetResult();
        result.IsSuccessful.ShouldBeTrue($"{project} uses: {string.Join(", ", rule.Violations)}");
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_contracts_reference_no_module_implementation(string module)
    {
        var project = SolutionModel.Contracts(module);

        SolutionModel.ProjectReferences(project)
            .Where(reference => reference.StartsWith(SolutionModel.ModulePrefix, StringComparison.Ordinal)
                                && !reference.EndsWith(SolutionModel.ContractsSuffix, StringComparison.Ordinal))
            .ShouldBeEmpty($"{project} references a module implementation");

        var rule = new NoOtherModuleImplementationRule(ownModule: string.Empty);
        var result = SolutionModel.TypesOf(project).Should().MeetCustomRule(rule).GetResult();
        result.IsSuccessful.ShouldBeTrue($"{project} uses: {string.Join(", ", rule.Violations)}");
    }

    [Theory]
    [MemberData(nameof(CoreProjects))]
    public void Core_never_references_a_country_pack(string project)
    {
        SolutionModel.ProjectReferences(project)
            .Where(reference => reference.StartsWith(SolutionModel.CountryPackPrefix, StringComparison.Ordinal))
            .ShouldBeEmpty($"{project} references a country pack");

        var result = SolutionModel.TypesOf(project)
            .ShouldNot().HaveDependencyOn("CoreIns.CountryPacks")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue($"{project} depends on a country pack: {FailingTypes(result)}");
    }

    [Fact]
    public void SharedKernel_references_nothing_internal()
    {
        SolutionModel.ProjectReferences(SolutionModel.SharedKernel).ShouldBeEmpty();

        var result = SolutionModel.TypesOf(SolutionModel.SharedKernel)
            .ShouldNot().HaveDependencyOnAny(
                SolutionModel.Platform, "CoreIns.Modules", "CoreIns.CountryPacks", SolutionModel.Host)
            .GetResult();
        result.IsSuccessful.ShouldBeTrue($"SharedKernel depends on: {FailingTypes(result)}");
    }

    [Fact]
    public void Platform_data_protection_references_only_the_shared_kernel()
    {
        SolutionModel.ProjectReferences(SolutionModel.DataProtection)
            .Where(reference => reference != SolutionModel.SharedKernel)
            .ShouldBeEmpty();

        var result = SolutionModel.TypesOf(SolutionModel.DataProtection)
            .ShouldNot().HaveDependencyOnAny("CoreIns.Modules", "CoreIns.CountryPacks", SolutionModel.Host)
            .GetResult();
        result.IsSuccessful.ShouldBeTrue($"{SolutionModel.DataProtection} depends on: {FailingTypes(result)}");

        // CoreIns.Platform itself (outbox, audit, Hangfire) is a namespace prefix of this project, so check assemblies.
        using var assembly = ModuleDefinition.ReadModule(SolutionModel.AssemblyPath(SolutionModel.DataProtection));
        assembly.AssemblyReferences.Select(reference => reference.Name)
            .Where(name => name.StartsWith("CoreIns.", StringComparison.Ordinal) && name != SolutionModel.SharedKernel)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// F-1c part 3: the contract-level abstractions every module's Contracts project shares (IEventPayload, common event
    /// value types, PLT contracts) depend on the SharedKernel only, so referencing them never pulls in the platform
    /// implementation (Npgsql, ASP.NET Core, EF Core) or another module.
    /// </summary>
    [Fact]
    public void Platform_contracts_reference_only_the_shared_kernel()
    {
        SolutionModel.ProjectReferences(SolutionModel.PlatformContracts)
            .Where(reference => reference != SolutionModel.SharedKernel)
            .ShouldBeEmpty();

        using var assembly = ModuleDefinition.ReadModule(SolutionModel.AssemblyPath(SolutionModel.PlatformContracts));
        assembly.AssemblyReferences.Select(reference => reference.Name)
            .Where(name => (name.StartsWith("CoreIns.", StringComparison.Ordinal) && name != SolutionModel.SharedKernel)
                           || name.StartsWith("Npgsql", StringComparison.Ordinal)
                           || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                           || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    /// <summary>A module's Contracts project references only the SharedKernel, Platform.Contracts and other modules' Contracts.</summary>
    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_contracts_reference_only_kernel_platform_contracts_and_contracts(string module)
    {
        SolutionModel.ProjectReferences(SolutionModel.Contracts(module))
            .Where(reference => reference != SolutionModel.SharedKernel
                                && reference != SolutionModel.PlatformContracts
                                && !(reference.StartsWith(SolutionModel.ModulePrefix, StringComparison.Ordinal)
                                     && reference.EndsWith(SolutionModel.ContractsSuffix, StringComparison.Ordinal)))
            .ShouldBeEmpty($"{SolutionModel.Contracts(module)} references more than contracts");
    }

    /// <summary>
    /// D-ARC-20: the only pack-to-pack reference allowed is CY → GR (the Cyprus stub reuses the Greek ELOT 743 engine).
    /// Any other one must move the shared algorithm to <c>CoreIns.CountryPacks.Common</c> instead.
    /// </summary>
    [Fact]
    public void Only_the_Cyprus_stub_references_another_pack_and_only_the_Greek_pack()
    {
        (string From, string To)[] allowed = [("CoreIns.CountryPacks.CY", "CoreIns.CountryPacks.GR")];

        foreach (var pack in SolutionModel.CountryPacks)
        {
            var projectReferences = SolutionModel.ProjectReferences(pack)
                .Where(reference => reference.StartsWith(SolutionModel.CountryPackPrefix, StringComparison.Ordinal));

            using var assembly = ModuleDefinition.ReadModule(SolutionModel.AssemblyPath(pack));
            var assemblyReferences = assembly.AssemblyReferences
                .Select(reference => reference.Name)
                .Where(name => name.StartsWith(SolutionModel.CountryPackPrefix, StringComparison.Ordinal));

            projectReferences.Concat(assemblyReferences)
                .Distinct(StringComparer.Ordinal)
                .Where(target => !allowed.Contains((pack, target)))
                .ShouldBeEmpty($"{pack} references another country pack outside D-ARC-20");
        }
    }

    [Fact]
    public void Country_packs_reference_no_module_implementation()
    {
        foreach (var pack in SolutionModel.CountryPacks)
        {
            SolutionModel.ProjectReferences(pack)
                .Where(reference => reference.StartsWith(SolutionModel.ModulePrefix, StringComparison.Ordinal)
                                    && !reference.EndsWith(SolutionModel.ContractsSuffix, StringComparison.Ordinal))
                .ShouldBeEmpty($"{pack} references a module implementation");
        }
    }

    [Fact]
    public void Every_module_exposes_a_unique_schema_and_a_registration_hook()
    {
        var schemas = new List<string>();
        foreach (var module in SolutionModel.ModuleNames.Prepend("Platform"))
        {
            var project = module == "Platform" ? SolutionModel.Platform : SolutionModel.Implementation(module);
            using var assembly = ModuleDefinition.ReadModule(SolutionModel.AssemblyPath(project));
            var type = assembly.GetType($"{project}.{module}Module");
            type.ShouldNotBeNull($"{project}.{module}Module");

            // Every public `const string ...Schema` names a PostgreSQL schema owned by the module.
            var owned = type.Fields
                .Where(field => field is { IsPublic: true, IsLiteral: true, HasConstant: true }
                                && field.Name.EndsWith("Schema", StringComparison.Ordinal))
                .ToDictionary(field => field.Name, field => (string)field.Constant);
            owned.ShouldContainKey("Schema", $"{type.Name}.Schema");
            owned.Values.ShouldAllBe(schema => SchemaName().IsMatch(schema));
            schemas.AddRange(owned.Values);

            type.Methods.ShouldContain(
                method => method.Name == $"Add{module}Module" && method.IsPublic && method.IsStatic,
                $"{type.Name}.Add{module}Module");
            type.Properties.ShouldContain(property => property.Name == "Schemas", $"{type.Name}.Schemas");
        }

        schemas.ShouldBeUnique();
        schemas.Count.ShouldBe(SolutionModel.ModuleNames.Count + 1 + 3, "one schema per module and platform, plus rpt_raw/rpt_conformed/rpt_mart");
    }

    [Fact]
    public void Forbidden_commercially_licensed_packages_are_not_used()
    {
        string[] forbidden = ["MediatR", "AutoMapper", "MassTransit", "FluentAssertions"];
        var packages = XDocument.Load(Path.Combine(SolutionModel.RepositoryRoot, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .ToList();

        packages.ShouldNotBeEmpty();
        packages.Where(package => forbidden.Any(f => package.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ShouldBeEmpty();
    }

    private static string FailingTypes(NetArchTest.Rules.TestResult result) =>
        string.Join(", ", result.FailingTypeNames ?? []);
}
