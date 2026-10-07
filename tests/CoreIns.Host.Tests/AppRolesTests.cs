using CoreIns.Host.Database;
using CoreIns.Host.Hosting;

namespace CoreIns.Host.Tests;

public sealed class AppRolesTests
{
    [Theory]
    [InlineData("api", "api")]
    [InlineData("API", "api")]
    [InlineData(" worker ", "worker")]
    [InlineData("Worker", "worker")]
    [InlineData("migrate", "migrate")]
    [InlineData(null, "api")]
    [InlineData("", "api")]
    public void Known_values_select_the_mode(string? value, string expected) =>
        AppRoles.Parse(value).ToConfigValue().ShouldBe(expected);

    [Theory]
    [InlineData("web")]
    [InlineData("apis")]
    [InlineData("migration")]
    [InlineData("bootstrap")]
    public void Unknown_values_fail_fast(string value)
    {
        var error = Should.Throw<InvalidOperationException>(() => AppRoles.Parse(value));

        error.Message.ShouldContain($"'{value}'");
        error.Message.ShouldContain("api, worker, migrate");
    }

    [Fact]
    public void Every_role_round_trips_through_its_config_value()
    {
        foreach (var role in Enum.GetValues<AppRole>())
        {
            AppRoles.Parse(role.ToConfigValue()).ShouldBe(role);
        }
    }

    [Fact]
    public void Bootstrap_script_is_embedded_and_creates_roles_and_extensions()
    {
        var script = DatabaseBootstrapper.ReadScript();

        script.ShouldContain("CREATE ROLE migrator");
        script.ShouldContain("CREATE ROLE app");
        foreach (var extension in DatabaseMigrator.RequiredExtensions)
        {
            script.ShouldContain($"CREATE EXTENSION IF NOT EXISTS {extension};");
        }
    }
}
