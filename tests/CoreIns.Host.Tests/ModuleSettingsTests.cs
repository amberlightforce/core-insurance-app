using CoreIns.Host.Hosting;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Host.Tests;

/// <summary>D-PRG-21: the permission map and authority grants are one file per module, merged at start-up.</summary>
public sealed class ModuleSettingsTests : IDisposable
{
    private static readonly string HostDirectory = Path.Combine(FindRoot(), "src", "CoreIns.Host");

    private readonly string _temp = Directory.CreateTempSubdirectory("coreins-modsettings-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Start_up_loads_every_module_file_into_the_permission_and_authority_options()
    {
        var files = Directory.GetFiles(Path.Combine(HostDirectory, ModuleSettings.PermissionsDirectory), "*.json");
        files.Length.ShouldBeGreaterThanOrEqualTo(11);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ModuleSettings.Load(Path.Combine(HostDirectory, ModuleSettings.PermissionsDirectory))).Build();
        var permissions = new PermissionOptions();
        configuration.GetSection(PermissionOptions.Section).Bind(permissions);
        var authority = new AuthorityOptions();
        configuration.GetSection(AuthorityOptions.Section).Bind(authority);

        // Every module prefix with a file contributes, and each file's operations start with the file's module code or plt (approvals).
        foreach (var module in new[] { "pty", "mkt", "pfc", "rat", "uw", "pol", "bil", "cmp", "fin", "clm", "plt" })
        {
            permissions.Grants.Keys.ShouldContain(k => k.StartsWith(module + ".", StringComparison.Ordinal), module);
            File.Exists(Path.Combine(HostDirectory, ModuleSettings.PermissionsDirectory, module + ".json")).ShouldBeTrue(module);
        }

        permissions.Grants["uw.Issue.decide"].ShouldBe(["Staff.UnderwritingManager"]);
        permissions.Grants["clm.Claim.get"].ShouldBe(["Staff.ClaimsHandler", "Staff.ClaimsManager"]);
        permissions.Grants["pty.Party.create"].ShouldBe(["Staff.Underwriter", "Staff.Billing"]);

        // The grants of two different module files (clm, uw) both survive: arrays are re-indexed across files.
        // Other modules' files add their own grants, so this asserts the ones it knows rather than the whole list.
        foreach (var id in new[]
                 {
                     "clm-handler-reserve-illustrative", "clm-handler-payment-illustrative", "clm-manager-reserve-illustrative",
                     "clm-manager-payment-illustrative", "uw-manager-issue-approval-illustrative",
                     "bil-billing-refund-illustrative", "bil-billingmgr-refund-illustrative",
                 })
        {
            authority.Grants.Select(g => g.Id).ShouldContain(id);
        }

        authority.Grants.Select(g => g.Id).Distinct().Count().ShouldBe(authority.Grants.Count);
        authority.Grants.Single(g => g.Id == "uw-manager-issue-approval-illustrative").Limits.Single().Codes.Count.ShouldBe(3);
        authority.Grants.Single(g => g.Id == "clm-manager-payment-illustrative").Limits.Single().Amount.ShouldBe("50000.00");
    }

    [Fact]
    public void The_same_operation_in_two_files_stops_start_up_naming_both_files()
    {
        Write("a.json", """{ "Platform": { "Permissions": { "Grants": { "xxx.Thing.get": [ "R1" ] } } } }""");
        Write("b.json", """{ "Platform": { "Permissions": { "Grants": { "xxx.Thing.get": [ "R2" ] } } } }""");

        var error = Should.Throw<InvalidOperationException>(() => ModuleSettings.Load(_temp));

        error.Message.ShouldContain("xxx.Thing.get");
        error.Message.ShouldContain("a.json");
        error.Message.ShouldContain("b.json");
    }

    [Fact]
    public void The_same_authority_grant_id_in_two_files_stops_start_up()
    {
        Write("a.json", """{ "Platform": { "Authority": { "Grants": [ { "Id": "g1", "Type": "T" } ] } } }""");
        Write("b.json", """{ "Platform": { "Authority": { "Grants": [ { "Id": "g1", "Type": "T" } ] } } }""");

        Should.Throw<InvalidOperationException>(() => ModuleSettings.Load(_temp)).Message.ShouldContain("g1");
    }

    [Fact]
    public void A_file_with_foreign_content_or_bad_JSON_is_rejected_with_its_name()
    {
        Write("a.json", """{ "Other": 1 }""");
        Should.Throw<InvalidOperationException>(() => ModuleSettings.Load(_temp)).Message.ShouldContain("a.json");

        File.Delete(Path.Combine(_temp, "a.json"));
        Write("c.json", "{ not json");
        Should.Throw<InvalidOperationException>(() => ModuleSettings.Load(_temp)).Message.ShouldContain("c.json");
    }

    [Fact]
    public void A_missing_directory_is_fine_and_deny_by_default_applies()
    {
        ModuleSettings.Load(Path.Combine(_temp, "nope")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Production", 0)]
    [InlineData("Staging", -1)]
    [InlineData("Development", -1)]
    public void Illustrative_grants_from_the_module_files_are_dropped_in_Production_only(string environment, int expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ModuleSettings.Load(Path.Combine(HostDirectory, ModuleSettings.PermissionsDirectory))).Build();
        var authority = new AuthorityOptions();
        configuration.GetSection(AuthorityOptions.Section).Bind(authority);

        // -1 = every grant of every module file survives (the count grows with each module; it is not pinned here).
        var all = authority.Grants.Count;
        all.ShouldBeGreaterThanOrEqualTo(7);
        authority.Grants.ShouldAllBe(g => g.Illustrative);

        AuthorityOptions.DropIllustrativeIn(authority, new Env(environment));

        authority.Grants.Count.ShouldBe(expected < 0 ? all : expected);
    }

    [Fact]
    public void Dev_users_load_only_in_Development_and_permissions_in_every_environment()
    {
        var development = new ConfigurationManager();
        development.AddJsonFile(Path.Combine(HostDirectory, "appsettings.json"), optional: false);
        development.AddModuleSettings(new Env("Development"));
        var production = new ConfigurationManager();
        production.AddJsonFile(Path.Combine(HostDirectory, "appsettings.json"), optional: false);
        production.AddModuleSettings(new Env("Production"));

        development.GetSection("DevAuthentication:Users").GetChildren().Count().ShouldBeGreaterThanOrEqualTo(8);
        production.GetSection("DevAuthentication:Users").GetChildren().ShouldBeEmpty();
        development["Platform:Permissions:Grants:uw.Issue.decide:0"].ShouldBe("Staff.UnderwritingManager");
        production["Platform:Permissions:Grants:uw.Issue.decide:0"].ShouldBe("Staff.UnderwritingManager");
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_temp, name), json);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreIns.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("CoreIns.sln not found above the test output.");
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = HostDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(HostDirectory);
    }
}
