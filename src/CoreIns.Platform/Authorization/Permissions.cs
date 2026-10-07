using CoreIns.Platform.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace CoreIns.Platform.Authorization;

/// <summary>
/// The permission catalogue as configuration (subset of REQ-PLT-075/076/079): permission name (the operation's
/// <c>x-permission</c>, e.g. <c>pty.Party.create</c>) → the application roles that hold it. Section
/// <c>Platform:Permissions</c>. One evaluation path serves HTTP policies and in-module checks (REQ-PLT-079). The full
/// RBAC/ABAC model (effective-dated assignments, SoD, access reviews) is W1-PLT-02.
/// </summary>
public sealed class PermissionOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Platform:Permissions";

    /// <summary>Permission → roles. A permission that is not listed is held by nobody (deny by default).</summary>
    public Dictionary<string, string[]> Grants { get; } = new(StringComparer.Ordinal);
}

/// <summary>Answers whether a set of roles holds a permission.</summary>
public interface IPermissionEvaluator
{
    /// <summary>True when one of <paramref name="roles"/> is granted <paramref name="permission"/>.</summary>
    bool Has(IEnumerable<string> roles, string permission);

    /// <summary>True when the scope's actor holds <paramref name="permission"/>.</summary>
    bool Has(RequestContext context, string permission);
}

/// <summary>Configuration-backed <see cref="IPermissionEvaluator"/>.</summary>
internal sealed class ConfiguredPermissionEvaluator(IOptionsMonitor<PermissionOptions> options) : IPermissionEvaluator
{
    public bool Has(IEnumerable<string> roles, string permission)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return options.CurrentValue.Grants.TryGetValue(permission, out var granted)
               && roles.Any(role => granted.Contains(role, StringComparer.Ordinal));
    }

    public bool Has(RequestContext context, string permission)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Has(context.Roles, permission);
    }
}

/// <summary>The requirement behind a permission policy.</summary>
/// <param name="Permission">Permission name.</param>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Succeeds when the authenticated user's roles hold the permission.</summary>
internal sealed class PermissionHandler(IPermissionEvaluator evaluator) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var roles = context.User.Claims
                .Where(claim => claim.Type is System.Security.Claims.ClaimTypes.Role or "roles")
                .Select(claim => claim.Value);
            if (evaluator.Has(roles, requirement.Permission))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Resolves <c>[Authorize(Policy = "pty.Party.create")]</c>: any policy name of the form <c>&lt;mod&gt;.&lt;Resource&gt;.&lt;verb&gt;</c>
/// becomes "authenticated and holds that permission". Other names go to the default provider.
/// </summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var existing = await base.GetPolicyAsync(policyName).ConfigureAwait(false);
        if (existing is not null || !IsPermission(policyName))
        {
            return existing;
        }

        return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(policyName)).Build();
    }

    internal static bool IsPermission(string name) => name.Count(c => c == '.') == 2 && char.IsLower(name[0]);
}
