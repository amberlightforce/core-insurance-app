namespace CoreIns.ArchitectureTests;

/// <summary>
/// ARCH-11 (PRD-17 REQ-MKT-332): no module other than MKT registers or reads a <c>tax.treatment.*</c> key. Only the Market
/// implementation and contracts, and the country packs that supply its rows as data, may name such a key.
/// </summary>
public sealed class TreatmentKeyGuardTests
{
    private static readonly string[] Allowed =
        [SolutionModel.ModulePrefix + "Market", SolutionModel.ModulePrefix + "Market.Contracts", "CoreIns.CountryPacks.GR", "CoreIns.CountryPacks.CY"];

    [Fact]
    public void ARCH_11_only_market_and_the_packs_name_a_tax_treatment_key()
    {
        var source = Path.Combine(SolutionModel.RepositoryRoot, "src");
        var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !Allowed.Contains(Project(source, file)))
            .Where(file => File.ReadAllText(file).Contains("tax.treatment.", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(source, file))
            .ToList();

        offenders.ShouldBeEmpty("ARCH-11: tax.treatment.* keys belong to MKT; call ITaxCalculator.TreatmentAsync instead");
    }

    [Fact]
    public void ARCH_11_the_guard_finds_the_market_keys_it_protects()
    {
        var source = Path.Combine(SolutionModel.RepositoryRoot, "src", "CoreIns.Modules.Market");
        Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Any(file => File.ReadAllText(file).Contains("tax.treatment.", StringComparison.Ordinal))
            .ShouldBeTrue();
    }

    private static string Project(string source, string file) =>
        Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar)[0];
}
