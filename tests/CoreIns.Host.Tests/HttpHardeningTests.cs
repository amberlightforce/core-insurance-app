using CoreIns.Host.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Host.Tests;

/// <summary>NX-OT-05: security headers, CORS, rate limits and defaults per environment.</summary>
public sealed class HttpHardeningTests
{
    private static async Task<HttpClient> ClientAsync(string environment, Dictionary<string, string?>? settings = null)
    {
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer().UseEnvironment(environment);
            web.ConfigureAppConfiguration(c => c.AddInMemoryCollection(settings ?? []));
            web.ConfigureServices((context, services) => services.AddCoreInsHttpHardening(context.Configuration, context.HostingEnvironment));
            web.Configure(app =>
            {
                var options = new HttpHardeningOptions();
                app.ApplicationServices.GetRequiredService<IConfiguration>().GetSection(HttpHardeningOptions.Section).Bind(options);
                app.UseCoreInsSecurityHeaders(app.ApplicationServices.GetRequiredService<IHostEnvironment>(), options);
                app.UseCors(HttpHardeningExtensions.CorsPolicy);
                app.UseRateLimiter();
                app.Run(context => context.Response.WriteAsync("ok"));
            });
        }).StartAsync(TestContext.Current.CancellationToken);
        return host.GetTestClient();
    }

    [Fact]
    public async Task Responses_carry_csp_nosniff_and_frame_headers_and_production_adds_hsts()
    {
        using var dev = await ClientAsync("Development");
        var response = await dev.GetAsync("/", TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldContain("script-src 'self'");
        csp.ShouldContain("connect-src 'self'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldNotContain("unsafe-eval");
        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();

        using var prod = await ClientAsync("Production");
        (await prod.GetAsync("/", TestContext.Current.CancellationToken)).Headers.GetValues("Strict-Transport-Security").Single().ShouldStartWith("max-age=");
    }

    [Fact]
    public async Task Hangfire_dashboard_skips_the_csp()
    {
        using var client = await ClientAsync("Development");
        (await client.GetAsync("/hangfire", TestContext.Current.CancellationToken)).Headers.Contains("Content-Security-Policy").ShouldBeFalse();
    }

    [Fact]
    public async Task Cors_is_same_origin_only_unless_an_origin_is_configured()
    {
        using var closed = await ClientAsync("Development");
        var denied = await closed.SendAsync(Origin("https://evil.example"), TestContext.Current.CancellationToken);
        denied.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        using var open = await ClientAsync("Development", new() { ["HttpHardening:AllowedOrigins:0"] = "https://portal.example" });
        (await open.SendAsync(Origin("https://portal.example"), TestContext.Current.CancellationToken)).Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://portal.example"]);
        (await open.SendAsync(Origin("https://evil.example"), TestContext.Current.CancellationToken)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Sign_in_is_rate_limited_per_ip_and_reads_are_not()
    {
        using var client = await ClientAsync("Development", new() { ["HttpHardening:SignInPermitsPerMinute"] = "2" });
        (await client.PostAsync(HttpHardeningExtensions.DevSignInPath, null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await client.PostAsync(HttpHardeningExtensions.DevSignInPath, null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var limited = await client.PostAsync(HttpHardeningExtensions.DevSignInPath, null, TestContext.Current.CancellationToken);
        limited.StatusCode.ShouldBe(System.Net.HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Retry-After").ShouldBeTrue();
        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/api/plt/v1/dev/users", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Write_requests_are_limited_when_configured()
    {
        using var client = await ClientAsync("Development", new() { ["HttpHardening:WritePermitsPerMinute"] = "1" });
        (await client.PostAsync("/api/x", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await client.PutAsync("/api/x", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Non_production_defaults_are_generous_so_e2e_suites_do_not_trip_them()
    {
        using var client = await ClientAsync("Development");
        for (var i = 0; i < 300; i++)
        {
            (await client.PostAsync(HttpHardeningExtensions.DevSignInPath, null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Production_defaults_limit_sign_in()
    {
        using var client = await ClientAsync("Production");
        var codes = new List<System.Net.HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            codes.Add((await client.PostAsync(HttpHardeningExtensions.DevSignInPath, null, TestContext.Current.CancellationToken)).StatusCode);
        }

        codes.Count(c => c == System.Net.HttpStatusCode.TooManyRequests).ShouldBe(2);
    }

    private static HttpRequestMessage Origin(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", origin);
        return request;
    }
}
