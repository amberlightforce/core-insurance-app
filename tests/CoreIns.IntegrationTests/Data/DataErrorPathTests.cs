using System.Net;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>NX-TST-07: Data module error paths, authorisation and idempotency (complements <see cref="DataCoreTests"/>).</summary>
public sealed class DataErrorPathTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Every_data_operation_needs_its_permission()
    {
        using var client = _factory.CreateClient();
        var subject = new { requestId = "r", subjectToken = "s" };
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "/api/dat/v1/subject/export", subject), (HttpMethod.Post, "/api/dat/v1/erasure/execute", subject),
                     (HttpMethod.Post, "/api/dat/v1/restriction/apply", subject), (HttpMethod.Post, "/api/dat/v1/models/register", new { featureKey = "AI-CLM-01", version = "1" }),
                     (HttpMethod.Post, "/api/dat/v1/bias-tests/run", new { metric = "M", ratio = "0.9" }), (HttpMethod.Get, $"/api/dat/v1/models/{Guid.NewGuid()}", null),
                 })
        {
            var (response, _) = await SendAsync(client, method, path, body, roles: "Viewer");
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task A_request_id_is_bound_to_one_subject()
    {
        using var client = _factory.CreateClient();
        var requestId = "req-" + Guid.NewGuid().ToString("N");
        var (first, _) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", new { requestId, subjectToken = "sub-a-" + requestId }, roles: Admin);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (other, body) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", new { requestId, subjectToken = "sub-b-" + requestId }, roles: Admin);
        other.StatusCode.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity);
        body.Text("code").ShouldBe("DAT-ERR-IDEMPOTENCY-MISMATCH");
    }

    [Fact]
    public async Task Malformed_requests_are_refused()
    {
        using var client = _factory.CreateClient();
        var (noSubject, _) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", new { requestId = "x" }, roles: Admin);
        ((int)noSubject.StatusCode).ShouldBeInRange(400, 422);

        var (noVersion, _) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/models/register", new { featureKey = "AI-CLM-01" }, roles: Admin);
        ((int)noVersion.StatusCode).ShouldBeInRange(400, 422);

        var (negative, _) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/bias-tests/run", new { feature = "AI-CLM-01", metric = "DISPARATE_IMPACT", ratio = "-1", sampleSize = 10 }, roles: Admin);
        ((int)negative.StatusCode).ShouldBeInRange(400, 422);

        var (neither, _) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/bias-tests/run", new { metric = "DISPARATE_IMPACT", ratio = "0.9", sampleSize = 10 }, roles: Admin);
        ((int)neither.StatusCode).ShouldBeInRange(400, 422);

        (await SendAsync(client, HttpMethod.Get, "/api/dat/v1/models/not-a-guid", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Registering_the_same_model_version_twice_returns_the_same_model()
    {
        using var client = _factory.CreateClient();
        var body = new { featureKey = "AI-CLM-" + Random.Shared.Next(10, 99), version = Guid.NewGuid().ToString("N")[..12] };
        var (_, one) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/models/register", body, roles: Admin);
        var (_, two) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/models/register", body, roles: Admin);
        two.Text("modelId").ShouldBe(one.Text("modelId"));
    }
}
