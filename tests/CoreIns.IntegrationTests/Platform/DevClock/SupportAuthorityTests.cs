using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Host.Hosting;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CoreIns.IntegrationTests.Platform.DevClock;

/// <summary>
/// SL3-PLT-SUPPORT: the Staff.BillingManager role and dev user, the BIL.Refund and POL.EffectiveDateOverride authority
/// types with their illustrative grants (D-SL3-14, D-SL2-03), and the permission files. Reads the real files of the Host.
/// </summary>
public sealed class SupportAuthorityTests
{
    private static readonly string HostDirectory = Path.Combine(RepositoryPaths.Root, "src", "CoreIns.Host");

    private static readonly Instant Now = Instant.Parse("2026-10-08T09:00:00Z");

    private static AuthorityOptions RealOptions(IHostEnvironment environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ModuleSettings.Load(Path.Combine(HostDirectory, "permissions"))).Build();
        var options = configuration.GetSection(AuthorityOptions.Section).Get<AuthorityOptions>() ?? new AuthorityOptions();
        AuthorityOptions.DropIllustrativeIn(options, environment);
        return options;
    }

    private static ConfiguredAuthorityService Service(AuthorityOptions options) =>
        new(new AuthorityTypeRegistry(SupportAuthorityTypes.Definitions), Options.Create(options), new ManualClock(Now));

    private static Task<AuthorityCheckResult> RefundAsync(ConfiguredAuthorityService service, string role, string amount, string actor = "u1", bool payeeChanged = false) =>
        service.CheckAsync(
            new AuthorityCheckRequest(
                ActorRef.User(actor),
                [role],
                SupportAuthorityTypes.Refund,
                new Dictionary<string, DimensionValue>
                {
                    [SupportAuthorityTypes.AmountDimension] = DimensionValue.Of(new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Currency.FromCode("EUR"))),
                    [SupportAuthorityTypes.CurrencyDimension] = DimensionValue.OfCodes("EUR"),
                    [SupportAuthorityTypes.PayeeChangedDimension] = DimensionValue.OfCodes(SupportAuthorityTypes.PayeeChangedCode(payeeChanged)),
                    [SupportAuthorityTypes.ReasonDimension] = DimensionValue.OfCodes("CUSTOMER_REQUEST"),
                },
                null,
                Now),
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("Staff.Billing", "0.01", AuthorityDecision.Allow)]
    [InlineData("Staff.Billing", "500.00", AuthorityDecision.Allow)]
    [InlineData("Staff.Billing", "500.01", AuthorityDecision.Refer)]
    [InlineData("Staff.Billing", "5000.00", AuthorityDecision.Refer)]
    [InlineData("Staff.Billing", "5000.01", AuthorityDecision.Deny)]
    [InlineData("Staff.BillingManager", "500.01", AuthorityDecision.Allow)]
    [InlineData("Staff.BillingManager", "5000.00", AuthorityDecision.Allow)]
    [InlineData("Staff.BillingManager", "5000.01", AuthorityDecision.Deny)]
    [InlineData("Staff.ClaimsManager", "1.00", AuthorityDecision.Deny)]
    [InlineData("Platform.Admin", "1.00", AuthorityDecision.Deny)]
    public async Task Refund_authority_follows_the_illustrative_limits(string role, string amount, AuthorityDecision expected)
    {
        var service = Service(RealOptions(new Environment("Development")));

        var check = await RefundAsync(service, role, amount);

        check.Decision.ShouldBe(expected, $"{role} {amount}: {check.ReasonCode}");
        if (expected == AuthorityDecision.Refer)
        {
            check.ReferralTargets.ShouldBe([new ReferralTarget("ROLE", "Staff.BillingManager")]);
        }
    }

    [Fact]
    public async Task The_refund_grants_are_illustrative_and_dropped_in_Production_so_every_refund_is_denied()
    {
        var production = RealOptions(new Environment("Production"));
        production.Grants.Where(g => g.Type == "BIL.REFUND").ShouldBeEmpty();
        RealOptions(new Environment("Development")).Grants.Where(g => g.Type == "BIL.REFUND").ShouldAllBe(g => g.Illustrative);

        foreach (var role in new[] { "Staff.Billing", "Staff.BillingManager" })
        {
            (await RefundAsync(Service(production), role, "1.00")).Decision.ShouldBe(AuthorityDecision.Deny);
        }
    }

    [Fact]
    public async Task The_effective_date_override_has_no_grants_in_the_slice_so_nobody_holds_it()
    {
        var service = Service(RealOptions(new Environment("Development")));
        RealOptions(new Environment("Development")).Grants.Where(g => g.Type == "POL.EFFECTIVE_DATE_OVERRIDE").ShouldBeEmpty();

        foreach (var role in new[] { "Staff.Underwriter", "Staff.UnderwritingManager", "Staff.BillingManager", "Platform.Admin" })
        {
            var check = await service.CheckAsync(
                new AuthorityCheckRequest(
                    ActorRef.User("u1"),
                    [role],
                    SupportAuthorityTypes.EffectiveDateOverride,
                    new Dictionary<string, DimensionValue>
                    {
                        ["product"] = DimensionValue.OfCodes("MOTOR"),
                        ["transactionType"] = DimensionValue.OfCodes("NEW_BUSINESS"),
                        ["days"] = DimensionValue.Of(45m),
                    },
                    null,
                    Now),
                TestContext.Current.CancellationToken);

            check.Decision.ShouldBe(AuthorityDecision.Deny, role);
            check.ReasonCode.ShouldBe("NO_GRANT");
        }
    }

    [Theory]
    [InlineData("Staff.Billing", "10.00", AuthorityDecision.Refer)]
    [InlineData("Staff.Billing", "500.00", AuthorityDecision.Refer)]
    [InlineData("Staff.BillingManager", "10.00", AuthorityDecision.Allow)]
    [InlineData("Staff.BillingManager", "5000.00", AuthorityDecision.Allow)]
    [InlineData("Staff.BillingManager", "5000.01", AuthorityDecision.Deny)]
    public async Task A_changed_payee_refers_the_clerk_up_to_the_billing_manager_at_any_amount(string role, string amount, AuthorityDecision expected)
    {
        var service = Service(RealOptions(new Environment("Development")));

        var check = await RefundAsync(service, role, amount, payeeChanged: true);

        check.Decision.ShouldBe(expected, $"{role} {amount}: {check.ReasonCode}");
        if (role == "Staff.Billing")
        {
            check.ReasonCode.ShouldBe("PAYEECHANGED_NOT_ALLOWED");
            check.ReferralTargets.ShouldContain(t => t.Id == "Staff.BillingManager");
        }
    }

    [Fact]
    public async Task The_payeeChanged_dimension_travels_as_a_code_so_plt_approval_can_carry_and_recheck_it()
    {
        var registry = new AuthorityTypeRegistry(SupportAuthorityTypes.Definitions);
        registry.TryGet(SupportAuthorityTypes.Refund, out var refund).ShouldBeTrue();
        refund.Dimensions.ShouldContain(new AuthorityDimensionDefinition("payeeChanged", DimensionKind.Code));
        refund.Dimensions.ShouldContain(new AuthorityDimensionDefinition("reason", DimensionKind.Code));

        // The dimension as plt.Approval re-checks it at decide time: money amount plus code dimensions only.
        var service = Service(RealOptions(new Environment("Development")));
        var dimensions = ApprovalDimensionsForTest("true");
        var asManager = await service.CheckAsync(
            new AuthorityCheckRequest(ActorRef.User("mgr"), ["Staff.BillingManager"], SupportAuthorityTypes.Refund, dimensions, null, Now), TestContext.Current.CancellationToken);
        asManager.Decision.ShouldBe(AuthorityDecision.Allow);
        var asClerk = await service.CheckAsync(
            new AuthorityCheckRequest(ActorRef.User("clerk"), ["Staff.Billing"], SupportAuthorityTypes.Refund, dimensions, null, Now), TestContext.Current.CancellationToken);
        asClerk.Decision.ShouldBe(AuthorityDecision.Refer);
        await Task.CompletedTask;
    }

    private static Dictionary<string, DimensionValue> ApprovalDimensionsForTest(string payeeChanged) => new()
    {
        ["amount"] = DimensionValue.Of(new Money(100m, Currency.FromCode("EUR"))),
        ["currency"] = DimensionValue.OfCodes("EUR"),
        ["payeeChanged"] = DimensionValue.OfCodes(payeeChanged),
        ["reason"] = DimensionValue.OfCodes("CUSTOMER_REQUEST"),
    };

    [Fact]
    public void The_two_authority_types_are_registered_with_their_dimensions()
    {
        var registry = new AuthorityTypeRegistry(SupportAuthorityTypes.Definitions);

        registry.TryGet(SupportAuthorityTypes.Refund, out var refund).ShouldBeTrue();
        refund.OwningModule.ShouldBe(ModuleCode.BIL);
        refund.Dimensions.Select(d => d.Name).ShouldBe(["amount", "currency", "payeeChanged", "reason"]);
        registry.TryGet(SupportAuthorityTypes.EffectiveDateOverride, out var overrideType).ShouldBeTrue();
        overrideType.OwningModule.ShouldBe(ModuleCode.POL);
        overrideType.Dimensions.Select(d => d.Name).ShouldBe(["product", "transactionType", "days"]);
    }

    [Fact]
    public void Only_the_billing_manager_decides_refunds_and_billing_staff_read_them()
    {
        var permissions = new ConfigurationBuilder().AddInMemoryCollection(ModuleSettings.Load(Path.Combine(HostDirectory, "permissions"))).Build()
            .GetSection("Platform:Permissions:Grants");

        permissions.GetSection("bil.Refund.decide").Get<string[]>().ShouldBe(["Staff.BillingManager"]);
        permissions.GetSection("bil.Refund.get").Get<string[]>()!.ShouldContain("Staff.Billing");
        permissions.GetSection("bil.Refund.list").Get<string[]>()!.ShouldContain("Staff.Billing");
        permissions.GetSection("plt.DevClock.advance").Get<string[]>().ShouldBe(["Platform.Admin"]);
        permissions.GetSection("plt.DevClock.get").Get<string[]>().ShouldBe(["Platform.Admin"]);
    }

    [Fact]
    public void The_billingmgr_dev_user_exists_and_the_superuser_holds_the_role_too()
    {
        var users = JsonNode.Parse(
            File.ReadAllText(Path.Combine(HostDirectory, "dev-users.Development.json")),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!["DevAuthentication"]!["Users"]!.AsArray();
        JsonNode User(string id) => users.Single(u => u!["Id"]!.GetValue<string>() == id)!;

        User("billingmgr")["Name"]!.GetValue<string>().ShouldBe("Billing Manager");
        User("billingmgr")["Roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["Staff.BillingManager"]);
        User("superuser")["Roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("Staff.BillingManager");
        User("billing")["Roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldNotContain("Staff.BillingManager");
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
