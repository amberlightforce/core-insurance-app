using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.IntegrationTests;

/// <summary>
/// Boots the real Host in api mode against the given database. Authentication is replaced by
/// <see cref="TestAuthHandler"/> so tests can act as anonymous, non-admin or admin callers; the real authorisation
/// policies (fallback, Admin) stay in force.
/// </summary>
internal sealed class ApiHostFactory(string connectionString, string environment = "Testing", IReadOnlyDictionary<string, string?>? settings = null, bool testAuthentication = true)
    : WebApplicationFactory<Program>
{
    /// <summary>The stamp's legal entity in tests (synthetic).</summary>
    public const string LegalEntityId = "0192f0c4-0000-7000-8000-000000000001";

    /// <summary>A synthetic 32-byte local master key ("coreins-integration-test-key-32b"), never a real secret.</summary>
    public const string TestMasterKey = "Y29yZWlucy1pbnRlZ3JhdGlvbi10ZXN0LWtleS0zMmI=";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("APP_ROLE", "api");
        builder.UseSetting("ConnectionStrings:Core", connectionString);
        builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
        builder.UseSetting("Stamp:LegalEntity", "GR-TEST");
        builder.UseSetting("Stamp:Country", "GR");
        builder.UseSetting("DataProtection:LocalMasterKey", TestMasterKey);
        builder.UseSetting("Party:DefaultCallingCode", "30");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        if (!testAuthentication)
        {
            return;
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
    }
}

/// <summary>
/// Authenticates a request only when it carries <see cref="RolesHeader"/>; its value is a comma-separated role list
/// (no header = anonymous). Challenge = 401, forbid = 403.
/// </summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var user = Request.Headers.TryGetValue(UserHeader, out var name) ? name.ToString() : "test-user";
        identity.AddClaim(new Claim(ClaimTypes.Name, user));
        foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
