using CoreIns.Host.Hosting;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Host.Tests;

/// <summary>
/// NX-OT-01: every controller endpoint of the API carries an explicit permission policy. The security fallback policy
/// ("any authenticated user", <see cref="SecurityExtensions"/>) must never silently cover an endpoint; the only
/// exceptions are listed here, each with a reason. A new controller action without <c>[Authorize(Policy = ...)]</c>
/// fails this test until it gets a permission or an allow-list entry.
/// </summary>
public sealed class EndpointAuthorizationMatrixTests
{
    /// <summary>Endpoints that may be anonymous. Key: "VERB route".</summary>
    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // (none today: health, dev sign-in/clock/clearing, favicon and OpenAPI are minimal-API endpoints mapped in Program.cs)
    };

    /// <summary>
    /// Endpoints that are intentionally "any authenticated user" (no permission). Key: "VERB route".
    /// TODO(hardening): give each a permission, or confirm it is a pure self-service read.
    /// </summary>
    private static readonly HashSet<string> AuthenticatedOnlyAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // Checks the two-part permission "pol.change" in code (IPermissionEvaluator) and returns 403; policies must be <mod>.<Resource>.<verb>.
        // TODO(hardening): move to a policy once the platform policy provider accepts two-part permission names.
        "GET /api/pol/v1/policy-changes/{jobId}/preview",
    };

    [Fact]
    public void Every_controller_endpoint_has_an_explicit_permission_or_an_allow_list_entry()
    {
        var endpoints = ControllerEndpoints();
        endpoints.ShouldNotBeEmpty();

        var violations = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var key = Describe(endpoint);
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var authorizeOnly = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null && policies.Count == 0;

            if (anonymous)
            {
                if (!AnonymousAllowList.Contains(key))
                {
                    violations.Add($"{key}: anonymous but not on the allow-list");
                }
            }
            else if (policies.Count == 0)
            {
                if (!AuthenticatedOnlyAllowList.Contains(key))
                {
                    violations.Add($"{key}: {(authorizeOnly ? "[Authorize] without a policy" : "no [Authorize] (fallback policy only)")}");
                }
            }
        }

        violations.ShouldBeEmpty("Endpoints without an explicit permission:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Allow_list_entries_still_exist()
    {
        var keys = ControllerEndpoints().Select(Describe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = AnonymousAllowList.Concat(AuthenticatedOnlyAllowList).Where(k => !keys.Contains(k)).ToList();
        stale.ShouldBeEmpty("Stale allow-list entries: " + string.Join(", ", stale));
    }

    [Fact]
    public void Openapi_is_anonymous_only_in_development()
    {
        var source = File.ReadAllText(FindFile("src/CoreIns.Host/Program.cs"));
        source.ShouldContain("if (app.Environment.IsDevelopment())");
        var openApiIndex = source.IndexOf("app.MapOpenApi()", StringComparison.Ordinal);
        var anonymousIndex = source.IndexOf("openApi.AllowAnonymous()", StringComparison.Ordinal);
        var guardIndex = source.LastIndexOf("if (app.Environment.IsDevelopment())", anonymousIndex, StringComparison.Ordinal);
        openApiIndex.ShouldBeGreaterThan(-1);
        anonymousIndex.ShouldBeGreaterThan(openApiIndex);
        guardIndex.ShouldBeGreaterThan(openApiIndex);
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var verbs = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join(',', verbs)} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
    }

    private static List<RouteEndpoint> ControllerEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers().AddModuleControllers(ModuleCatalog.ApiAssemblies);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapControllers();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string FindFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}
