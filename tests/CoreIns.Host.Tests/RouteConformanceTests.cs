using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;

namespace CoreIns.Host.Tests;

/// <summary>
/// NX-TST-04: the routes the Host actually serves under /api must match the operations in contracts/openapi/*.yaml.
/// Known gaps live in RouteConformanceAllowList.txt (the HD-08 backlog); any NEW drift, and any allow-list entry that
/// has since been fixed, fails the test. No database is touched: the Host is only built, never queried.
/// </summary>
public sealed class RouteConformanceTests
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch"];

    [Fact]
    public void Served_routes_match_the_openapi_contracts_except_for_the_allow_list()
    {
        var served = ServedRoutes();
        var contracted = ContractedRoutes();
        served.Count.ShouldBeGreaterThan(50, "route enumeration found suspiciously few endpoints");
        contracted.Count.ShouldBeGreaterThan(50, "contract parsing found suspiciously few operations");

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var route in served.Except(contracted))
        {
            actual.Add("SERVED_NOT_IN_CONTRACT " + route);
        }

        foreach (var route in contracted.Except(served))
        {
            actual.Add("CONTRACT_NOT_SERVED " + route);
        }

        var allowFile = Path.Combine(AppContext.BaseDirectory, "RouteConformanceAllowList.txt");
        var allowed = File.ReadAllLines(allowFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        if (Environment.GetEnvironmentVariable("ROUTE_CONFORMANCE_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllLines(dump, actual);
        }

        var newDrift = actual.Where(a => !allowed.Contains(a)).ToList();
        var stale = allowed.Where(a => !actual.Contains(a)).ToList();
        newDrift.ShouldBeEmpty("route/contract drift not in RouteConformanceAllowList.txt:\n" + string.Join('\n', newDrift));
        stale.ShouldBeEmpty("allow-list entries that no longer drift (remove them):\n" + string.Join('\n', stale));
    }

    private static SortedSet<string> ServedRoutes()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("APP_ROLE", "api");
            builder.UseSetting("ConnectionStrings:Core", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
            builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
            builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
            builder.UseSetting("Stamp:LegalEntity", "GR-TEST");
            builder.UseSetting("Stamp:Country", "GR");
            builder.UseSetting("DataProtection:LocalMasterKey", "Y29yZWlucy1pbnRlZ3JhdGlvbi10ZXN0LWtleS0zMmI=");
            builder.UseSetting("Party:DefaultCallingCode", "30");
        });

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in factory.Services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
                if (!pattern.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase) || pattern.Contains("{**", StringComparison.Ordinal))
                {
                    continue; // static files, health, openapi, hangfire, and the /api/{**path} 404 fallback
                }

                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                foreach (var method in methods)
                {
                    result.Add(Key(method, pattern));
                }
            }
        }

        return result;
    }

    private static SortedSet<string> ContractedRoutes()
    {
        var dir = Path.Combine(FindRoot(), "contracts", "openapi");
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*.yaml"))
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(file))
            {
                yaml.Load(reader);
            }

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            if (!root.Children.TryGetValue("paths", out var pathsNode) || pathsNode is not YamlMappingNode paths)
            {
                continue;
            }

            foreach (var (pathKey, pathValue) in paths.Children)
            {
                if (pathValue is not YamlMappingNode operations)
                {
                    continue;
                }

                foreach (var method in Methods.Where(m => operations.Children.ContainsKey(m)))
                {
                    result.Add(Key(method, ((YamlScalarNode)pathKey).Value!));
                }
            }
        }

        return result;
    }

    // Normalise: upper-case method, lower-case path, parameter names and constraints dropped ({id:guid} -> {}), no trailing slash.
    private static string Key(string method, string path)
    {
        var segments = path.Trim('/').Split('/').Select(s => s.StartsWith('{') ? "{}" : s.ToLowerInvariant());
        return method.ToUpperInvariant() + " /" + string.Join('/', segments);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoreIns.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
