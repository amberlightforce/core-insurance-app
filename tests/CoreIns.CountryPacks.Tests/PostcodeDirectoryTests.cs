using CoreIns.CountryPacks.GR.Addresses;
using CoreIns.Modules.Market.Contracts.ReferenceData;

namespace CoreIns.CountryPacks.Tests;

/// <summary>
/// D-ARC-22: the <see cref="IPostcodeDirectory"/> contract with the REQ-PTY-076 acceptance vector as a seeded fixture
/// (postcode 11526 → «Αθήνα»). The fixture is test data only; no postcode list ships in the pack.
/// </summary>
public sealed class PostcodeDirectoryTests
{
    [Fact]
    public async Task Postcode_11526_proposes_Athina_when_the_locality_is_missing()
    {
        var formatter = new GreekAddressFormatter(new SeededDirectory());

        var address = await formatter.ParseAsync(["Λεωφ. Κηφισίας 124", "11526"], "GR", TestContext.Current.CancellationToken);

        address.Postcode.ShouldBe("11526");
        address.Locality.ShouldBe("Αθήνα");
    }

    [Fact]
    public async Task A_keyed_locality_is_never_overwritten_and_unknown_postcodes_are_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var formatter = new GreekAddressFormatter(new SeededDirectory());

        (await formatter.ParseAsync(["Οδός 1, 11526 Αμπελόκηποι"], "GR", ct)).Locality.ShouldBe("Αμπελόκηποι");
        (await formatter.ParseAsync(["Οδός 1", "10557"], "GR", ct)).Locality.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_directory_there_is_no_autofill()
    {
        var address = await new GreekAddressFormatter().ParseAsync(["Λεωφ. Κηφισίας 124", "11526"], "GR", TestContext.Current.CancellationToken);
        address.Locality.ShouldBeNull();
    }

    /// <summary>Fixture: only the PRD-01 REQ-PTY-076 acceptance vector.</summary>
    private sealed class SeededDirectory : IPostcodeDirectory
    {
        public string Version => "test-fixture-REQ-PTY-076";

        public ValueTask<IReadOnlyList<PostcodeLocality>> LookupAsync(string country, string postcode, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PostcodeLocality> result = country == "GR" && postcode == "11526" ? [new PostcodeLocality("11526", "Αθήνα")] : [];
            return ValueTask.FromResult(result);
        }
    }
}
