using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

namespace CoreIns.Host.Hosting;

/// <summary>Authorisation policy names used by the Host.</summary>
internal static class AuthPolicies
{
    /// <summary>Platform administrators (Hangfire dashboard, operational tools).</summary>
    public const string Admin = "Admin";

    /// <summary>Default Entra app role that grants <see cref="Admin"/>; override with <c>Authorization__AdminRole</c>.</summary>
    public const string DefaultAdminRole = "Platform.Admin";
}

/// <summary>Entra ID bearer-token validation (Microsoft.Identity.Web), the Development-only local sign-in, and authorisation policies.</summary>
internal static class SecurityExtensions
{
    /// <summary>Default scheme: routes a request to the dev scheme or to Entra ID by the token's issuer.</summary>
    public const string SelectorScheme = "CoreIns";

    /// <summary>
    /// Validates Entra ID tokens from the <c>AzureAd</c> section. Every endpoint requires an authenticated user
    /// unless it opts out explicitly (health probes, the static React app). Permission policies
    /// (<c>[Authorize(Policy = "pty.Party.create")]</c>) are resolved by the platform from <c>Platform:Permissions</c>.
    /// When <paramref name="devSignIn"/> is true (Development and <c>DevAuthentication:Enabled</c>, checked by
    /// <see cref="DevelopmentAuthentication.Guard"/>), tokens of the local dev issuer are accepted as well.
    /// </summary>
    public static IServiceCollection AddCoreInsSecurity(this IServiceCollection services, IConfiguration configuration, bool devSignIn = false)
    {
        var authentication = services.AddAuthentication(devSignIn ? SelectorScheme : JwtBearerDefaults.AuthenticationScheme);
        authentication.AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));
        if (devSignIn)
        {
            authentication.AddDevelopmentSignIn(services, configuration);
            authentication.AddPolicyScheme(SelectorScheme, "Entra ID or development sign-in", options =>
                options.ForwardDefaultSelector = context =>
                    DevelopmentAuthentication.IsDevToken(context) ? DevelopmentAuthentication.Scheme : JwtBearerDefaults.AuthenticationScheme);
        }

        var adminRole = configuration["Authorization:AdminRole"] is { Length: > 0 } role ? role : AuthPolicies.DefaultAdminRole;

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(adminRole))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
