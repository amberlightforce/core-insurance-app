using CoreIns.Platform.Authority;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Options;

namespace CoreIns.Platform.Tests;

/// <summary><c>plt.Authority.check</c> shape (REQ-PLT-003/103) with the configuration-backed default service.</summary>
public sealed class AuthorityTests
{
    private static readonly AuthorityTypeCode Reserve = AuthorityTypeCode.Parse("CLM_RESERVE");
    private static readonly Instant Now = Instant.Parse("2026-10-07T10:00:00Z");

    private static ConfiguredAuthorityService Service(out IAuthorityTypeRegistry registry)
    {
        registry = new AuthorityTypeRegistry(
        [
            new AuthorityTypeDefinition(Reserve, ModuleCode.CLM, new LocalizedText("Αποθεματοποίηση", "Reserve"),
                [new AuthorityDimensionDefinition("amount", DimensionKind.Money), new AuthorityDimensionDefinition("lob", DimensionKind.Code)]),
        ]);
        var options = new AuthorityOptions();
        options.Grants.Add(Grant("g-handler", role: "Claims.Handler", limit: "5000", lobs: ["MOTOR"]));
        options.Grants.Add(Grant("g-manager", role: "Claims.Manager", limit: "50000", lobs: ["MOTOR", "HOME"]));
        options.Grants.Add(Grant("g-expired", role: "Claims.Old", limit: "999999", lobs: ["MOTOR"], validTo: "2026-01-01T00:00:00Z"));
        options.Grants.Add(Grant("g-user", user: "user-7", limit: "100", lobs: ["MOTOR"]));
        return new ConfiguredAuthorityService(registry, Options.Create(options), new ManualClock(Now));
    }

    private static AuthorityGrantOptions Grant(string id, string? role = null, string? user = null, string limit = "0", string[]? lobs = null, string? validTo = null)
    {
        var grant = new AuthorityGrantOptions { Id = id, Role = role, User = user, Type = "CLM_RESERVE", ValidTo = validTo };
        grant.Limits.Add(new AuthorityLimitOptions { Dimension = "amount", Comparison = LimitComparison.AtMost, Amount = limit, Currency = "EUR" });
        var lob = new AuthorityLimitOptions { Dimension = "lob", Comparison = LimitComparison.InSet };
        foreach (var code in lobs ?? [])
        {
            lob.Codes.Add(code);
        }

        grant.Limits.Add(lob);
        return grant;
    }

    private static AuthorityCheckRequest Request(ActorRef actor, string[] roles, string amount, string currency = "EUR", string lob = "MOTOR") =>
        new(actor, roles, Reserve,
            new Dictionary<string, DimensionValue>
            {
                ["amount"] = DimensionValue.Of(Money.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency)),
                ["lob"] = DimensionValue.OfCodes(lob),
            },
            null, Now);

    [Fact]
    public async Task Within_the_limit_is_allowed_with_the_source_grant()
    {
        var result = await Service(out _).CheckAsync(Request(ActorRef.User("u1"), ["Claims.Handler"], "4999.99"), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(AuthorityDecision.Allow);
        result.ReasonCode.ShouldBe("WITHIN_LIMIT");
        result.SourceGrant.ShouldBe("g-handler");
        result.ApplicableLimits.Count.ShouldBe(2);
        result.CheckId.Value.Version.ShouldBe(7);
        result.ValidAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Above_the_limit_is_referred_to_holders_of_enough_authority()
    {
        var result = await Service(out _).CheckAsync(Request(ActorRef.User("u1"), ["Claims.Handler"], "5000.01"), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(AuthorityDecision.Refer);
        result.ReasonCode.ShouldBe("AMOUNT_ABOVE_LIMIT");
        result.SourceGrant.ShouldBe("g-handler");
        result.ReferralTargets.ShouldBe([new ReferralTarget("ROLE", "Claims.Manager")]);
    }

    [Fact]
    public async Task Nobody_holding_enough_authority_means_deny()
    {
        var result = await Service(out _).CheckAsync(Request(ActorRef.User("u1"), ["Claims.Manager"], "60000"), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(AuthorityDecision.Deny);
        result.ReasonCode.ShouldBe("AMOUNT_ABOVE_LIMIT");
    }

    [Fact]
    public async Task No_grant_expired_grant_ai_actor_and_set_limits()
    {
        var service = Service(out _);
        var ct = TestContext.Current.CancellationToken;

        (await service.CheckAsync(Request(ActorRef.User("u1"), ["Viewer"], "1"), ct)).ReasonCode.ShouldBe("NO_GRANT");
        (await service.CheckAsync(Request(ActorRef.User("u1"), ["Claims.Old"], "1"), ct)).ReasonCode.ShouldBe("NO_GRANT");
        (await service.CheckAsync(Request(new ActorRef(ActorKind.AiAgent, "agent"), ["Claims.Manager"], "1"), ct)).ReasonCode.ShouldBe("AI_ACTOR_NOT_GRANTED");
        (await service.CheckAsync(Request(ActorRef.User("u1"), ["Claims.Handler"], "1", lob: "HOME"), ct)).ReasonCode.ShouldBe("LOB_NOT_ALLOWED");
        (await service.CheckAsync(Request(ActorRef.User("user-7"), [], "100"), ct)).Decision.ShouldBe(AuthorityDecision.Allow);
    }

    [Fact]
    public async Task Limits_in_another_currency_are_not_converted_yet()
    {
        var result = await Service(out _).CheckAsync(Request(ActorRef.User("u1"), ["Claims.Handler"], "1", currency: "USD"), TestContext.Current.CancellationToken);

        result.Decision.ShouldNotBe(AuthorityDecision.Allow);
        result.ReasonCode.ShouldBe("CURRENCY_NOT_CONVERTIBLE");
    }

    [Fact]
    public async Task Unknown_types_and_conflicting_registrations_fail_with_platform_codes()
    {
        var service = Service(out var registry);
        var unknown = Request(ActorRef.User("u1"), ["Claims.Handler"], "1") with { Type = AuthorityTypeCode.Parse("NOPE") };

        (await Should.ThrowAsync<DomainException>(() => service.CheckAsync(unknown, TestContext.Current.CancellationToken)))
            .Error.Code.Value.ShouldBe("PLT-ERR-UNKNOWN-TYPE");
        Should.Throw<DomainException>(() => registry.Register(new AuthorityTypeDefinition(Reserve, ModuleCode.POL, new LocalizedText("x", "x"), [])))
            .Error.Code.Value.ShouldBe("PLT-ERR-TYPE-EXISTS");
        registry.Register(registry.All.Single());
    }

    [Theory]
    [InlineData("Production", 1)]
    [InlineData("Development", 2)]
    [InlineData("Testing", 2)]
    public void D_SL2_03_illustrative_grants_never_apply_in_Production(string environment, int remaining)
    {
        var options = new AuthorityOptions();
        options.Grants.Add(Grant("g-approved", role: "Claims.Handler", limit: "1"));
        var illustrative = Grant("g-illustrative", role: "Claims.Handler", limit: "5000");
        illustrative.Illustrative = true;
        options.Grants.Add(illustrative);

        AuthorityOptions.DropIllustrativeIn(options, new HostEnvironment(environment));

        options.Grants.Count.ShouldBe(remaining);
        options.Grants.ShouldContain(g => g.Id == "g-approved");
    }

    private sealed class HostEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
