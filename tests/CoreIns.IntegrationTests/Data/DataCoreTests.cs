using System.Net;
using CoreIns.Modules.Data.Commands;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>SL6-DAT-CORE main paths: crypto-shred erasure (idempotent), export, restriction, bias test suspension.</summary>
public sealed class DataCoreTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task SeedAsync(string subject)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SubjectFactStore>();
        (await store.RecordAsync(subject, "PARTY", "email", "a@example.org", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await store.RecordAsync(subject, "CLAIM", "note", "whiplash", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Erasure_destroys_the_key_once_blanks_facts_and_is_idempotent_on_the_request_id()
    {
        var subject = "sub-" + Guid.NewGuid().ToString("N");
        await SeedAsync(subject);
        using var client = _factory.CreateClient();

        var (exported, before) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/subject/export", new { requestId = "x", subjectToken = subject }, roles: Admin);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, before?.ToJsonString());
        before.Text("factCount").ShouldBe("2");
        before.Text("facts.0.category").ShouldBe("PARTY");

        var requestId = "req-" + Guid.NewGuid().ToString("N");
        var body = new { requestId, subjectToken = subject, dsarRef = Guid.CreateVersion7() };
        var (first, one) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", body, roles: Admin);
        var (second, two) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", body, roles: Admin);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        second.StatusCode.ShouldBe(HttpStatusCode.OK, two?.ToJsonString());
        one.Text("status").ShouldBe("ERASED");
        one.Text("factCount").ShouldBe("2");
        two!.ToJsonString().ShouldBe(one!.ToJsonString());

        (await CountAsync($"SELECT count(*) FROM dat.subject_key WHERE subject_token = '{subject}' AND key_material IS NULL AND destroyed_at IS NOT NULL")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM dat.subject_fact WHERE subject_token = '{subject}' AND cipher IS NULL AND blanked")).ShouldBe(2);
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'LakehouseErasureCompleted' AND aggregate_id = '{requestId}'")).ShouldBe(1);

        var (_, after) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/subject/export", new { requestId = "x", subjectToken = subject }, roles: Admin);
        after.Text("status").ShouldBe("ERASED");
        after.Text("factCount").ShouldBe("0");

        // A new request id for an already-erased subject destroys nothing more.
        var (third, again) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", new { requestId = requestId + "-2", subjectToken = subject }, roles: Admin);
        third.StatusCode.ShouldBe(HttpStatusCode.OK, again?.ToJsonString());
        again.Text("factCount").ShouldBe("0");

        (await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", body, roles: "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Restriction_keeps_facts_exportable_and_blocks_erasure()
    {
        var subject = "sub-" + Guid.NewGuid().ToString("N");
        await SeedAsync(subject);
        using var client = _factory.CreateClient();

        var (restricted, applied) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/restriction/apply", new { requestId = "r", subjectToken = subject }, roles: Admin);
        restricted.StatusCode.ShouldBe(HttpStatusCode.OK, applied?.ToJsonString());
        applied.Text("status").ShouldBe("RESTRICTED");

        var (_, exported) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/subject/export", new { requestId = "x", subjectToken = subject }, roles: Admin);
        exported.Text("status").ShouldBe("RESTRICTED");
        exported.Text("factCount").ShouldBe("2");

        var (blocked, problem) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/erasure/execute", new { requestId = "e-" + subject, subjectToken = subject }, roles: Admin);
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, problem?.ToJsonString());
        problem.Text("code").ShouldBe("DAT-ERR-060");
    }

    [Fact]
    public async Task Bias_ratio_outside_the_band_suspends_the_model_and_emits_one_breach_event_inside_emits_none()
    {
        using var client = _factory.CreateClient();
        var feature = "AI-CLM-" + Random.Shared.Next(10, 99).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var (registered, model) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/models/register", new { featureKey = feature, version = "1.0" }, roles: Admin);
        registered.StatusCode.ShouldBe(HttpStatusCode.OK, model?.ToJsonString());
        model.Text("status").ShouldBe("ACTIVE");

        var (okResponse, ok) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/bias-tests/run", new { feature, metric = "DISPARATE_IMPACT", ratio = "0.90", sampleSize = 500 }, roles: Admin);
        okResponse.StatusCode.ShouldBe(HttpStatusCode.OK, ok?.ToJsonString());
        ok.Text("breached").ShouldBe("false");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'BiasThresholdBreached' AND payload::text LIKE '%{feature}%'")).ShouldBe(0);

        var (badResponse, bad) = await SendAsync(client, HttpMethod.Post, "/api/dat/v1/bias-tests/run", new { feature, metric = "DISPARATE_IMPACT", ratio = "0.74", sampleSize = 500 }, roles: Admin);
        badResponse.StatusCode.ShouldBe(HttpStatusCode.OK, bad?.ToJsonString());
        bad.Text("breached").ShouldBe("true");
        bad.Text("modelStatus").ShouldBe("SUSPENDED");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'BiasThresholdBreached' AND payload::text LIKE '%{feature}%'")).ShouldBe(1);

        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/dat/v1/models/{model.Text("modelId")}", roles: Admin);
        got.Text("status").ShouldBe("SUSPENDED");
        (await SendAsync(client, HttpMethod.Get, $"/api/dat/v1/models/{Guid.CreateVersion7()}", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
