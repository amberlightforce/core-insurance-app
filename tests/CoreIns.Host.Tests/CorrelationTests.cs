using CoreIns.Host.Hosting;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Host.Tests;

/// <summary>NX-OT-06: X-Correlation-Id is accepted when valid, created otherwise, and echoed (also on errors).</summary>
public sealed class CorrelationTests
{
    private const string Valid = "0123456789abcdef0123456789abcdef";

    private static async Task<HttpClient> ClientAsync()
    {
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.Configure(app =>
            {
                app.UseCoreInsCorrelation();
                app.Run(context =>
                {
                    if (context.Request.Path == "/boom")
                    {
                        throw new InvalidOperationException("boom");
                    }

                    return context.Response.WriteAsync(context.Items[PlatformHeaders.CorrelationItemKey]!.ToString()!);
                });
            });
        }).StartAsync(TestContext.Current.CancellationToken);
        return host.GetTestClient();
    }

    [Fact]
    public async Task A_valid_incoming_id_is_kept_and_echoed()
    {
        using var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-Id", Valid.ToUpperInvariant());
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Correlation-Id").ShouldBe([Valid]);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(Valid);
    }

    [Theory]
    [InlineData("not-a-trace-id")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("<script>alert(1)</script>")]
    public async Task An_invalid_incoming_id_is_replaced_by_a_new_one(string incoming)
    {
        using var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", incoming);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        id.Length.ShouldBe(32);
        id.ShouldNotBe(incoming);
    }

    [Fact]
    public async Task A_request_without_an_id_gets_one()
    {
        using var client = await ClientAsync();
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        response.Headers.GetValues("X-Correlation-Id").Single().Length.ShouldBe(32);
    }
}
