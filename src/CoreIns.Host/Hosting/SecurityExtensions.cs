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

/// <summary>Entra ID bearer-token validation (Microsoft.Identity.Web) and authorisation policies.</summary>
internal static class SecurityExtensions
{
    /// <summary>
    /// Validates Entra ID tokens from the <c>AzureAd</c> section. Every endpoint requires an authenticated user
    /// unless it opts out explicitly (health probes, the static React app).
    /// </summary>
    public static IServiceCollection AddCoreInsSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));

        var adminRole = configuration["Authorization:AdminRole"] is { Length: > 0 } role ? role : AuthPolicies.DefaultAdminRole;

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(adminRole))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
