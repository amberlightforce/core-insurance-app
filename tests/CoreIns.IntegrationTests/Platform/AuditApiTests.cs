using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>BO-PLT-01: the audit query, export and verify HTTP API (plt.Audit.*) against the real Host.</summary>
public sealed class AuditApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Query = "/api/plt/v1/audit/query";
    private const string Export = "/api/plt/v1/audit/export";
    private const string Verify = "/api/plt/v1/audit/verify";

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body = null, string? roles = "Platform.Admin", string? correlation = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
            request.Headers.Add(TestAuthHandler.UserHeader, "audit-reader");
        }

        if (correlation is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlation);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Query_finds_a_record_by_correlation_id_and_pages_with_a_keyset_cursor()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var correlation = Guid.NewGuid().ToString("N");

        // Two audited calls under one correlation id leave two audit records to find.
        (await SendAsync(client, HttpMethod.Post, Verify, new { from = Today, to = Today }, correlation: correlation)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Post, Verify, new { from = Today, to = Today }, correlation: correlation)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var all = await SendAsync(client, HttpMethod.Get, $"{Query}?correlationId={correlation}&action=plt.Audit.verify&actor=audit-reader&limit=1");
        all.StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = await JsonAsync(all);
        var item = first["items"]!.AsArray().Single()!;
        item["operation"]!.GetValue<string>().ShouldBe("plt.Audit.verify");
        item["correlationId"]!.GetValue<string>().ShouldBe(correlation);
        var cursor = first["nextCursor"]!.GetValue<string>();

        using var next = await SendAsync(client, HttpMethod.Get, $"{Query}?correlationId={correlation}&limit=1&cursor={Uri.EscapeDataString(cursor)}");
        var second = await JsonAsync(next);
        var older = second["items"]!.AsArray().Single()!;
        older["auditId"]!.GetValue<Guid>().ShouldNotBe(item["auditId"]!.GetValue<Guid>());
        second["nextCursor"].ShouldBeNull();

        using var none = await SendAsync(client, HttpMethod.Get, $"{Query}?correlationId={Guid.NewGuid():N}");
        (await JsonAsync(none))["items"]!.AsArray().Count.ShouldBe(0);

        using var badCursor = await SendAsync(client, HttpMethod.Get, $"{Query}?cursor=not-a-cursor");
        badCursor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var wide = await SendAsync(client, HttpMethod.Get, $"{Query}?from=2020-01-01T00:00:00Z&to=2023-01-01T00:00:00Z");
        wide.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Verify_fails_for_a_day_whose_record_was_tampered_with()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var correlation = Guid.NewGuid().ToString("N");
        (await SendAsync(client, HttpMethod.Post, Verify, new { from = Today, to = Today }, correlation: correlation)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var where = $"correlation_id = '{correlation}' AND chain_date = '{Today}'";
        await Tamper($"UPDATE plt.audit_event SET reason = 'rewritten' WHERE {where}");
        try
        {
            using var verify = await SendAsync(client, HttpMethod.Post, Verify, new { from = Today, to = Today });
            verify.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await JsonAsync(verify);
            body["allValid"]!.GetValue<bool>().ShouldBeFalse();
            var day = body["days"]!.AsArray().Single()!;
            day["isValid"]!.GetValue<bool>().ShouldBeFalse();
            day["brokenAtSequence"]!.GetValue<int>().ShouldBeGreaterThan(0);
        }
        finally
        {
            await Tamper($"UPDATE plt.audit_event SET reason = NULL WHERE {where}");
        }

        using var wide = await SendAsync(client, HttpMethod.Post, Verify, new { from = "2026-01-01", to = "2026-03-01" });
        wide.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Export_returns_a_capped_csv_and_is_itself_audited()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var correlation = Guid.NewGuid().ToString("N");
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        (await SendAsync(client, HttpMethod.Post, Verify, new { from = Today, to = Today }, correlation: correlation)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var export = await SendAsync(client, HttpMethod.Post, Export, new { from, to, correlationId = correlation });
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(export);
        body["rowCount"]!.GetValue<int>().ShouldBe(1);
        body["truncated"]!.GetValue<bool>().ShouldBeFalse();
        var lines = body["csv"]!.GetValue<string>().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        lines[0].ShouldStartWith("auditId,chainDate,sequence");
        lines[1].ShouldContain("plt.Audit.verify");

        using var audited = await SendAsync(client, HttpMethod.Get, $"{Query}?action=plt.Audit.export&actor=audit-reader&limit=1");
        (await JsonAsync(audited))["items"]!.AsArray().Count.ShouldBe(1);

        using var tooWide = await SendAsync(client, HttpMethod.Post, Export, new { from = "2026-01-01T00:00:00Z", to = "2026-12-31T00:00:00Z" });
        tooWide.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("Staff.Billing")]
    [InlineData("Staff.Underwriter")]
    public async Task Other_roles_are_refused_and_anonymous_callers_are_unauthenticated(string role)
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var range = new { from = Today, to = Today };

        (await SendAsync(client, HttpMethod.Get, Query, roles: role)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, Verify, range, roles: role)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, Export, new { from = "2026-01-01T00:00:00Z", to = "2026-01-02T00:00:00Z" }, roles: role)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Get, Query, roles: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The compliance officer may read.
        (await SendAsync(client, HttpMethod.Get, Query, roles: "Staff.ComplianceOfficer")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Superuser with triggers disabled for the session: the attack the hash chain must reveal.</summary>
    private async Task Tamper(string sql) =>
        await database.ExecuteAsSuperuserAsync($"SET session_replication_role = replica; {sql}; SET session_replication_role = origin;", TestContext.Current.CancellationToken);
}
