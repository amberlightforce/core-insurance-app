using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-08: suspend, terminate cascade, register import and CPD status over HTTP.</summary>
public sealed class IntermediaryLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(string Id, string Code)> ActiveIntermediaryAsync(string afm, string registerNumber)
    {
        var (_, party) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new
        {
            partyType = "ORGANISATION",
            organisation = new { legalName = "Πράκτορες " + registerNumber, tradeName = "Agency " + registerNumber },
            identifiers = new[] { new { scheme = "AFM", value = afm } },
        });
        var (created, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries", new
        {
            partyId = party.Text("party.partyId"),
            intermediaryType = "INSURANCE_AGENT",
            register = new
            {
                registerName = "Register", chamber = "Chamber", registerNumber, registrationCategory = "AGENT", registrationDate = "2020-01-15",
                registerStatus = "ACTIVE",
            },
            authorities = new { collectPremium = true, issueCoverNotes = false, bindWithinAuthority = true, serviceOnly = false },
            validFrom = "2026-01-01",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var id = body.Text("intermediary.intermediaryId");
        var (activated, active) = await SendAsync(_client, HttpMethod.Patch, $"/api/pty/v1/intermediaries/{id}", new { status = "ACTIVE", expectedRecordVersion = 1 });
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, active?.ToJsonString());
        return (id, body.Text("intermediary.producerCode"));
    }

    private async Task<JsonNode?> ValidateAsync(string code)
    {
        var (_, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-codes/validate?validAt=2026-10-07",
            new { producerCode = code, product = "MOTOR_PRIVATE_CAR", transactionType = "NEW_BUSINESS" }, withKey: false);
        return body;
    }

    [Fact]
    public async Task Suspend_makes_validate_refuse_and_terminate_ends_the_producer_codes()
    {
        var (id, code) = await ActiveIntermediaryAsync("094014201", "LC-0001");
        (await ValidateAsync(code)).Text("valid").ShouldBe("true");

        var (suspended, suspendBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/suspend",
            new { intermediaryId = id, expectedRecordVersion = 2, reason = "Licence review" });
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK, suspendBody?.ToJsonString());
        suspendBody.Text("intermediary.status").ShouldBe("SUSPENDED");
        var refused = await ValidateAsync(code);
        refused.Text("valid").ShouldBe("false");
        refused.Text("reasons.0").ShouldBe("INTERMEDIARY_NOT_ACTIVE");

        var (staleTerminate, _) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/terminate",
            new { intermediaryId = id, expectedRecordVersion = 2, reason = "Stale" });
        staleTerminate.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (terminated, terminateBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/terminate",
            new { intermediaryId = id, expectedRecordVersion = 3, reason = "Left the market", effectiveDate = "2026-11-01" });
        terminated.StatusCode.ShouldBe(HttpStatusCode.OK, terminateBody?.ToJsonString());
        terminateBody.Text("intermediary.status").ShouldBe("TERMINATED");
        terminateBody.Text("intermediary.producerCodeStatus").ShouldBe("TERMINATED");
        terminateBody.Text("terminatedProducerCodes.0").ShouldBe(code);

        var (again, _) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/terminate",
            new { intermediaryId = id, expectedRecordVersion = 4, reason = "Again" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var events = dataSource.CreateCommand(
            $"SELECT string_agg(event_type, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_id = '{id}'");
        var published = (string)(await events.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        published.ShouldContain("IntermediaryLicenceChanged");
        published.Split(',').Count(e => e == "ProducerCodeChanged").ShouldBe(2);
    }

    [Fact]
    public async Task Register_import_is_idempotent_and_computes_cpd_status()
    {
        var (id, _) = await ActiveIntermediaryAsync("123456783", "LC-0002");
        var csv = "registerNumber,registerStatus,cpdYear,cpdHours\nLC-0002,ACTIVE,2026,8\nNOPE-1,ACTIVE,2026,30\n,ACTIVE,,\n";

        var (first, firstBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/import-register", new { format = "CSV", content = csv });
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstBody?.ToJsonString());
        firstBody.Text("updated").ShouldBe("1");
        firstBody.Text("unmatchedRegisterNumbers.0").ShouldBe("NOPE-1");
        firstBody.Text("rejectedRows.0").ShouldContain("registerNumber");

        var (second, secondBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/import-register", new { format = "CSV", content = csv });
        second.StatusCode.ShouldBe(HttpStatusCode.OK, secondBody?.ToJsonString());
        secondBody.Text("updated").ShouldBe("0");
        secondBody.Text("unchanged").ShouldBe("1");

        var json = """[{"registerNumber":"LC-0002","registerStatus":"SUSPENDED","cpdYear":2026,"cpdHours":25}]""";
        var (third, thirdBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/import-register", new { format = "JSON", content = json });
        third.StatusCode.ShouldBe(HttpStatusCode.OK, thirdBody?.ToJsonString());
        thirdBody.Text("updated").ShouldBe("1");

        var (suspended, suspendBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/suspend",
            new { intermediaryId = id, expectedRecordVersion = 4, reason = "check view" });
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK, suspendBody?.ToJsonString());
        suspendBody.Text("intermediary.cpdStatus").ShouldBe("COMPLIANT");
        suspendBody.Text("intermediary.registerStatus").ShouldBe("SUSPENDED");
    }
}
