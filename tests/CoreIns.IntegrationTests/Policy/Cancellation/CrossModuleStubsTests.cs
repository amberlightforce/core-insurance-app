using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Services;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoreIns.IntegrationTests.Policy.Cancellation;

/// <summary>NX-HD-11: letters greet the party by name (PTY), and the PSR Work task never blocks the business flow. No database needed.</summary>
public sealed class CrossModuleStubsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PartyNameView Name(PartyNameView.FormValue form, string? given = null, string? family = null, string? org = null) =>
        new() { Form = form, Script = "Grek", GivenNames = given, FamilyName = family, OrganisationName = org };

    [Fact]
    public void The_native_person_name_is_preferred_over_the_generated_latin_one()
    {
        var picked = PartyDisplayName.Pick([Name(PartyNameView.FormValue.LatinGenerated, "Nikos", "Papadopoulos"), Name(PartyNameView.FormValue.Native, "Νίκος", "Παπαδόπουλος")]);
        picked.ShouldBe("Νίκος Παπαδόπουλος");
    }

    [Fact]
    public void An_organisation_is_addressed_by_its_organisation_name()
    {
        PartyDisplayName.Pick([Name(PartyNameView.FormValue.Native, org: "Acme ΑΕ")]).ShouldBe("Acme ΑΕ");
    }

    [Fact]
    public void No_usable_name_yields_null()
    {
        PartyDisplayName.Pick([Name(PartyNameView.FormValue.Native)]).ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_party_service_the_neutral_greeting_is_used()
    {
        var names = new PartyDisplayName(new ServiceCollection().BuildServiceProvider());
        (await names.ResolveAsync(new PartyId(Guid.NewGuid()), Ct)).ShouldBe(PartyDisplayName.Greeting);
    }

    [Fact]
    public async Task Without_a_work_service_raising_a_psr_task_is_a_logged_no_op()
    {
        var tasks = new PsrWorkTasks(new ServiceCollection().BuildServiceProvider(), NullLogger<PsrWorkTasks>.Instance);
        await tasks.RaiseAsync("WRK-POL-NONPAY-PSR-EXCEPTION", new ObjectRef(ModuleCode.POL, "Policy", Guid.NewGuid().ToString("D")), "HOLD:x", Ct);
    }
}
