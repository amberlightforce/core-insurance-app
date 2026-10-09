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

    private static readonly string[] SuperRoles =
    [
        "Staff.Underwriter", "Staff.UnderwritingManager", "Staff.Billing", "Staff.BillingManager", "Staff.Finance", "Staff.ClaimsHandler", "Staff.ClaimsManager", "Platform.Admin", "Platform.ReleaseManager", "Platform.DesignAuthority", "Staff.RecoverySpecialist", "Staff.ReinsuranceAccountant", "Staff.ReinsuranceManager",
    ];

    private static readonly Dictionary<string, string?> Enabled = new() { ["DevAuthentication:Enabled"] = "true" };

    [Theory]
    [InlineData("recovery", "Dev Recovery Specialist (synthetic)", "Staff.RecoverySpecialist")]
    [InlineData("riacct", "Dev Reinsurance Accountant (synthetic)", "Staff.ReinsuranceAccountant")]
    [InlineData("rimgr", "Dev Reinsurance Manager (synthetic)", "Staff.ReinsuranceManager")]
    public async Task SL4_users_return_the_server_actor_key_matching_the_JWT(string id, string name, string role)
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Enabled, testAuthentication: false);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var users = (await client.GetFromJsonAsync<JsonNode>(new Uri("/api/plt/v1/dev/users", UriKind.Relative), ct))!["items"]!.AsArray();
        var user = users.Single(u => u!["id"]!.GetValue<string>() == id)!;
        user["name"]!.GetValue<string>().ShouldBe(name);
        user["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe([role]);
        var names = users.Select(u => u!["name"]!.GetValue<string>()).ToArray();
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        foreach (var other in names.Where(n => n != name))
        {
            name.Contains(other, StringComparison.Ordinal).ShouldBeFalse();
            other.Contains(name, StringComparison.Ordinal).ShouldBeFalse();
        }

        using var response = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = id }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonNode>(ct))!;
        var token = new JsonWebToken(body["accessToken"]!.GetValue<string>());
        var actorKey = "USER:" + token.GetClaim("oid").Value;
        user["actorKey"]!.GetValue<string>().ShouldBe(actorKey);
        body["user"]!["actorKey"]!.GetValue<string>().ShouldBe(actorKey);
        body["user"]!["id"]!.GetValue<string>().ShouldBe(id);
    }

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
        // D-PRG-21: the expected ids are read from dev-users.Development.json, so adding a dev user needs no test edit.
        var file = Path.Combine(RepositoryPaths.Root, "src", "CoreIns.Host", "dev-users.Development.json");
        var configured = JsonNode.Parse(
            await File.ReadAllTextAsync(file, ct),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!["DevAuthentication"]!["Users"]!
            .AsArray().Select(u => u!["Id"]!.GetValue<string>()).ToList();
        configured.ShouldContain("underwriter");
        configured.ShouldContain("billing");
        configured.ShouldContain("claims");
        configured.ShouldContain("superuser");
        users!["items"]!.AsArray().Select(u => u!["id"]!.GetValue<string>()).ShouldBe(configured);

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

        // SL2-PLT: the synthetic claims users see parties and their approvals inbox, but cannot create parties.
        foreach (var claimsUser in new[] { "claims", "claimsmgr" })
        {
            using var claimsSignIn = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = claimsUser }, ct);
            var claimsToken = (await claimsSignIn.Content.ReadFromJsonAsync<JsonNode>(ct))!["accessToken"]!.GetValue<string>();
            foreach (var (path, expected) in new[]
                     {
                         ("/api/pty/v1/parties/search?partyNumber=P000000001", HttpStatusCode.OK),
                         ("/api/plt/v1/approval", HttpStatusCode.OK),
                     })
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
                get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claimsToken);
                using var response = await client.SendAsync(get, ct);
                response.StatusCode.ShouldBe(expected, $"{claimsUser} GET {path}");
            }

            using var claimsCreate = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/pty/v1/parties", UriKind.Relative)) { Content = JsonContent.Create(new { }) };
            claimsCreate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claimsToken);
            claimsCreate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var claimsForbidden = await client.SendAsync(claimsCreate, ct);
            claimsForbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The all-roles super user: one token carries every staff role (a `roles` array) and every module's read surface is open.
        using var superSignIn = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = "superuser" }, ct);
        var superBody = (await superSignIn.Content.ReadFromJsonAsync<JsonNode>(ct))!;
        var superToken = superBody["accessToken"]!.GetValue<string>();
        superBody["user"]!["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(SuperRoles, ignoreOrder: true);
        var tokenRoles = new JsonWebTokenHandler().ReadJsonWebToken(superToken).Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        tokenRoles.ShouldBe(SuperRoles, ignoreOrder: true);
        foreach (var path in new[]
                 {
                     "/api/pty/v1/parties/search?partyNumber=P000000001",
                     "/api/plt/v1/approval",
                     "/api/fin/v1/journals/query",
                     "/api/bil/v1/invoices?policyId=0192f0c4-0000-7000-8000-0000000000aa",
                 })
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superToken);
            using var response = await client.SendAsync(get, ct);
            response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, $"superuser GET {path}");
            response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"superuser GET {path}");
        }

        // The synthetic senior underwriter (Staff.UnderwritingManager) may decide UW issues; the plain underwriter only lists them.
        foreach (var (uwUser, decideAllowed) in new[] { ("uwsenior", true), ("underwriter", false) })
        {
            using var uwSignIn = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = uwUser }, ct);
            var uwToken = (await uwSignIn.Content.ReadFromJsonAsync<JsonNode>(ct))!["accessToken"]!.GetValue<string>();
            using var list = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/uw/v1/issues?jobRef={Guid.CreateVersion7()}", UriKind.Relative));
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uwToken);
            using var listed = await client.SendAsync(list, ct);
            listed.StatusCode.ShouldBe(HttpStatusCode.OK, uwUser);

            using var decide = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/uw/v1/issues/decide", UriKind.Relative))
            {
                Content = JsonContent.Create(new { issueIds = new[] { Guid.CreateVersion7() }, decision = "APPROVE", reason = "sign-in test" }),
            };
            decide.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uwToken);
            decide.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var decided = await client.SendAsync(decide, ct);
            decided.StatusCode.ShouldBe(decideAllowed ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, uwUser); // allowed: the issue does not exist
        }

        // SL5-PLT-ROLES: the release manager and design authority dev users exist with exactly one role each (maker / checker for pack activation).
        var byId = JsonNode.Parse(
            await File.ReadAllTextAsync(file, ct),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!["DevAuthentication"]!["Users"]!.AsArray();
        foreach (var (id, name, role) in new[] { ("releasemgr", "Dev Release Manager (synthetic)", "Platform.ReleaseManager"), ("designauth", "Dev Design Authority (synthetic)", "Platform.DesignAuthority") })
        {
            var user = byId.Single(u => u!["Id"]!.GetValue<string>() == id)!;
            user["Name"]!.GetValue<string>().ShouldBe(name);
            user["Roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe([role]);
            using var roleSignIn = await client.PostAsJsonAsync(new Uri("/api/plt/v1/dev/sign-in", UriKind.Relative), new { userId = id }, ct);
            roleSignIn.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await roleSignIn.Content.ReadFromJsonAsync<JsonNode>(ct))!["user"]!["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe([role]);
        }

        var names = byId.Select(u => u!["Name"]!.GetValue<string>()).ToList();
        names.Distinct().Count().ShouldBe(names.Count);
        foreach (var name in names)
        {
            names.Where(other => other != name).ShouldAllBe(other => !other.Contains(name, StringComparison.Ordinal));
        }

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
