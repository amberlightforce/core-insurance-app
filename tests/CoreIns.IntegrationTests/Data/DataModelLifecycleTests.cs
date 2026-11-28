using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Data;

/// <summary>BO-DAT-06: model registry lifecycle (SoD), drift metrics and the bias confidence interval.</summary>
public sealed class DataModelLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static readonly object Card = new { purpose = "Claims triage", trainingData = "2024 claims", limitations = "No marine" };

    private async Task<JsonNode?> SendAsync(string path, object? body, string user, HttpStatusCode expected, HttpMethod? method = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Admin);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (request.Method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = text.Length == 0 ? null : JsonNode.Parse(text);
        response.StatusCode.ShouldBe(expected, text);
        return json;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static string NewFeature() => "AI-CLM-" + Random.Shared.Next(100, 99999).ToString(CultureInfo.InvariantCulture);

    private async Task<string> RegisterDraftAsync(string feature, string dev, object? card = null)
    {
        var model = await SendAsync("/api/dat/v1/models/register", new { featureKey = feature, version = "1.0", modelCard = card ?? Card, draft = true }, dev, HttpStatusCode.OK);
        model.Text("status").ShouldBe("DRAFT");
        return model.Text("modelId");
    }

    [Fact]
    public async Task Model_walks_draft_to_active_with_a_second_person_and_the_timeline_records_it()
    {
        var feature = NewFeature();
        var id = await RegisterDraftAsync(feature, "dev");

        // Cannot validate or approve a draft.
        await SendAsync("/api/dat/v1/models/record-validation", new { modelId = id, outcome = "PASSED" }, "val", HttpStatusCode.UnprocessableEntity);
        await SendAsync("/api/dat/v1/models/approve", new { modelId = id }, "boss", HttpStatusCode.UnprocessableEntity);

        (await SendAsync("/api/dat/v1/models/submit-validation", new { modelId = id }, "dev", HttpStatusCode.OK)).Text("status").ShouldBe("IN_VALIDATION");
        await SendAsync("/api/dat/v1/models/submit-validation", new { modelId = id }, "dev", HttpStatusCode.UnprocessableEntity); // not a draft any more

        // The developer / submitter cannot validate their own model (DAT-ERR-030).
        var self = await SendAsync("/api/dat/v1/models/record-validation", new { modelId = id, outcome = "PASSED" }, "dev", HttpStatusCode.UnprocessableEntity);
        self.Text("code").ShouldBe("DAT-ERR-030");
        // Approval needs a passed validation first.
        await SendAsync("/api/dat/v1/models/approve", new { modelId = id }, "boss", HttpStatusCode.UnprocessableEntity);

        (await SendAsync("/api/dat/v1/models/record-validation", new { modelId = id, outcome = "PASSED", findings = "ok" }, "val", HttpStatusCode.OK)).Text("outcome").ShouldBe("PASSED");

        // SoD on approval: developer and validator are refused, a third person approves.
        (await SendAsync("/api/dat/v1/models/approve", new { modelId = id }, "dev", HttpStatusCode.UnprocessableEntity)).Text("code").ShouldBe("DAT-ERR-030");
        (await SendAsync("/api/dat/v1/models/approve", new { modelId = id }, "val", HttpStatusCode.UnprocessableEntity)).Text("code").ShouldBe("DAT-ERR-030");
        (await SendAsync("/api/dat/v1/models/approve", new { modelId = id }, "boss", HttpStatusCode.OK)).Text("status").ShouldBe("ACTIVE");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ModelVersionApproved' AND payload::text LIKE '%{id}%'")).ShouldBe(1);

        var got = await SendAsync($"/api/dat/v1/models/{id}", null, "boss", HttpStatusCode.OK, HttpMethod.Get);
        got.Text("status").ShouldBe("ACTIVE");
        got!["transitions"]!.AsArray().Select(t => t!["status"]!.ToString()).ToArray().ShouldBe(["DRAFT", "IN_VALIDATION", "IN_VALIDATION", "ACTIVE"]);

        (await SendAsync("/api/dat/v1/models/retire", new { modelId = id, reason = "Replaced by 2.0" }, "boss", HttpStatusCode.OK)).Text("status").ShouldBe("RETIRED");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ModelVersionRetired' AND payload::text LIKE '%{id}%'")).ShouldBe(1);
        await SendAsync("/api/dat/v1/models/retire", new { modelId = id, reason = "again" }, "boss", HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Failed_validation_returns_to_draft_and_incomplete_card_cannot_be_submitted_and_future_approval_stays_approved()
    {
        var feature = NewFeature();
        var thin = await RegisterDraftAsync(feature, "dev", new { purpose = "x" });
        (await SendAsync("/api/dat/v1/models/submit-validation", new { modelId = thin }, "dev", HttpStatusCode.UnprocessableEntity)).Text("code").ShouldBe("DAT-ERR-031");
        (await SendAsync("/api/dat/v1/models/submit-validation", new { modelId = thin, modelCard = Card }, "dev", HttpStatusCode.OK)).Text("status").ShouldBe("IN_VALIDATION");
        (await SendAsync("/api/dat/v1/models/record-validation", new { modelId = thin, outcome = "FAILED", findings = "bias" }, "val", HttpStatusCode.OK)).Text("status").ShouldBe("DRAFT");

        // Resubmit, pass, approve with a future effective date: stays APPROVED.
        await SendAsync("/api/dat/v1/models/submit-validation", new { modelId = thin }, "dev", HttpStatusCode.OK);
        await SendAsync("/api/dat/v1/models/record-validation", new { modelId = thin, outcome = "PASSED" }, "val", HttpStatusCode.OK);
        var approved = await SendAsync("/api/dat/v1/models/approve", new { modelId = thin, effectiveDate = "2099-01-01" }, "boss", HttpStatusCode.OK);
        approved.Text("status").ShouldBe("APPROVED");
        await SendAsync("/api/dat/v1/models/approve", new { modelId = Guid.NewGuid() }, "boss", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Drift_breach_publishes_the_event_flags_the_model_and_a_passing_metric_clears_it()
    {
        var feature = NewFeature();
        var id = await RegisterDraftAsync(feature, "dev");
        await SendAsync("/api/dat/v1/models/submit-validation", new { modelId = id }, "dev", HttpStatusCode.OK);
        await SendAsync("/api/dat/v1/models/record-validation", new { modelId = id, outcome = "PASSED" }, "val", HttpStatusCode.OK);
        await SendAsync("/api/dat/v1/models/approve", new { modelId = id }, "boss", HttpStatusCode.OK);

        var ok = await SendAsync("/api/dat/v1/monitor/metrics", new { feature, version = "1.0", metricRecords = new[] { new { metric = "PSI", value = "0.08", sampleSize = 1000 } } }, "ops", HttpStatusCode.OK);
        ok.Text("breached").ShouldBe("0");
        ok.Text("driftFlagged").ShouldBe("false");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ModelDriftDetected' AND payload::text LIKE '%{feature}%'")).ShouldBe(0);

        var bad = await SendAsync("/api/dat/v1/monitor/metrics", new { feature, version = "1.0", metricRecords = new object[] { new { metric = "PSI", value = "0.40", sampleSize = 1000 }, new { metric = "AUC", value = "0.7" } } }, "ops", HttpStatusCode.OK);
        bad.Text("accepted").ShouldBe("2");
        bad.Text("breached").ShouldBe("1");
        bad.Text("driftFlagged").ShouldBe("true");
        (await CountAsync($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ModelDriftDetected' AND payload::text LIKE '%{feature}%'")).ShouldBe(1);

        var flagged = await SendAsync($"/api/dat/v1/models/{id}", null, "ops", HttpStatusCode.OK, HttpMethod.Get);
        flagged.Text("driftFlagged").ShouldBe("true");
        flagged!["metrics"]!.AsArray().Count.ShouldBe(3);

        await SendAsync("/api/dat/v1/monitor/metrics", new { feature, version = "1.0", metricRecords = new[] { new { metric = "PSI", value = "0.05" } } }, "ops", HttpStatusCode.OK);
        (await SendAsync($"/api/dat/v1/models/{id}", null, "ops", HttpStatusCode.OK, HttpMethod.Get)).Text("driftFlagged").ShouldBe("false");

        // Unknown feature/version is DAT-ERR-032.
        (await SendAsync("/api/dat/v1/monitor/metrics", new { feature = NewFeature(), version = "9", metricRecords = new[] { new { metric = "PSI", value = "0.5" } } }, "ops", HttpStatusCode.UnprocessableEntity)).Text("code").ShouldBe("DAT-ERR-032");
    }

    [Fact]
    public async Task Bias_breach_confidence_interval_uses_the_sample_size()
    {
        var feature = NewFeature();
        await SendAsync("/api/dat/v1/models/register", new { featureKey = feature, version = "1.0" }, "dev", HttpStatusCode.OK);
        await SendAsync("/api/dat/v1/bias-tests/run", new { feature, metric = "DISPARATE_IMPACT", ratio = "0.74", sampleSize = 400 }, "dev", HttpStatusCode.OK);

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'BiasThresholdBreached' AND payload::text LIKE '%{feature}%'", connection);
        var payload = JsonNode.Parse((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!)!;
        var ci = payload["payload"]?["confidenceInterval"] ?? payload["confidenceInterval"];
        var lower = decimal.Parse(ci!["lower"]!.ToString(), CultureInfo.InvariantCulture);
        var upper = decimal.Parse(ci["upper"]!.ToString(), CultureInfo.InvariantCulture);
        lower.ShouldBeLessThan(0.74m);
        upper.ShouldBeGreaterThan(0.74m);
        (upper - lower).ShouldBeLessThan(0.2m);
    }
}
