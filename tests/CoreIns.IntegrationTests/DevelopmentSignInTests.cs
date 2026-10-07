using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CoreIns.IntegrationTests;

/// <summary>The Development-only local sign-in (D-SLC-03): refused outside Development, usable end to end inside it.</summary>
public sealed class DevelopmentSignInTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly string[] AdminRoles = ["Platform.Admin"];

    private static readonly Dictionary<string, string?> Enabled = new() { ["DevAuthentication:Enabled"] = "true" };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void The_host_refuses_to_start_when_the_flag_is_set_outside_Development(string environment)
    {
        using var factory = new ApiHostFactory(database.AppConnectionString, environment, Enabled, testAuthentication: false);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        error.Message.ShouldContain("DevAuthentication:Enabled");
        error.Message.ShouldContain("refuses to start");
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    [InlineData("Development", true)]
    public void The_guard_reports_dev_sign_in_only_for_Development_with_the_flag(string environment, bool flag)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DevAuthentication:Enabled"] = flag ? "true" : "false" }).Build();
        DevelopmentAuthentication.Guard(configuration, new Environment(environment)).ShouldBe(environment == "Development" && flag);
    }

    [Fact]
    public async Task Without_the_flag_the_dev_endpoints_do_not_exist()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", testAuthentication: false);
        using var client = factory.CreateClient();

        using var users = await client.GetAsync(new Uri("/api/plt/v1/dev/users", UriKind.Relative), TestContext.Current.CancellationToken);

        users.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task In_Development_a_configured_user_signs_in_and_calls_the_API_with_its_roles()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Enabled, testAuthentication: false);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var users = await client.GetFromJsonAsync<JsonNode>(new Uri("/api/plt/v1/dev/users", UriKind.Relative), ct);
        users!["items"]!.AsArray().Select(u => u!["id"]!.GetValue<string>()).ShouldBe(["underwriter", "billing", "finance", "admin"]);

        using var unknown = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = "nobody" }, ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var signIn = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = "underwriter" }, ct);
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await signIn.Content.ReadFromJsonAsync<JsonNode>(ct))!["accessToken"]!.GetValue<string>();

        using var anonymous = await client.GetAsync(new Uri("/api/pty/v1/parties/search?partyNumber=P000000001", UriKind.Relative), ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var search = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/pty/v1/parties/search?partyNumber=P000000001", UriKind.Relative));
        search.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var allowed = await client.SendAsync(search, ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The billing user lacks pty.Intermediary.create: authenticated but forbidden.
        using var billingSignIn = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = "billing" }, ct);
        var billingToken = (await billingSignIn.Content.ReadFromJsonAsync<JsonNode>(ct))!["accessToken"]!.GetValue<string>();
        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/pty/v1/intermediaries", UriKind.Relative)) { Content = JsonContent.Create(new { }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", billingToken);
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var forbidden = await client.SendAsync(create, ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A token with the dev issuer but signed with another key is rejected.
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevelopmentAuthentication.Issuer,
            Audience = DevelopmentAuthentication.Audience,
            Claims = new Dictionary<string, object> { ["sub"] = "dev:mallory", ["roles"] = AdminRoles },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(new byte[32]), SecurityAlgorithms.HmacSha256),
        });
        using var forgedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/pty/v1/parties/search?partyNumber=P000000001", UriKind.Relative));
        forgedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged);
        using var rejected = await client.SendAsync(forgedRequest, ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
