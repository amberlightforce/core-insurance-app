using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>BO-FIN-03: rerun / markDuplicate / reject on the intake exception queue, and a correction that overtakes its delta.</summary>
public sealed class IntakeExceptionActionTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-02";
    private const string FinanceRole = "Staff.Finance";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private string? _artefactHash;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var (response, body) = await ProductApi.ImportAsync(_client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private Task SetPeriodAsync(string status) => database.ExecuteAsSuperuserAsync(
        $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '2026-11', '{status}', now()) ON CONFLICT (legal_entity_id, period_code) DO UPDATE SET status = '{status}'",
        TestContext.Current.CancellationToken);

    private async Task<Policy> BoundPolicyAsync()
    {
        var policy = Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash!);
        await _slice.PolicyBoundAsync(policy);
        await _slice.DrainAsync();
        return policy;
    }

    private static JsonObject Charge(Policy policy, Guid account, string side, string ledgerAccount, string amount, Guid chargeId)
    {
        var line = Line(ledgerAccount, side, amount, policy, account, "PREM-MTPL", "PREMIUM", "MTPL");
        line["dimensions"]!["chargeId"] = chargeId.ToString();
        return line;
    }

    /// <summary>An ordinary entry dated in a locked month: suspended as PERIOD_LOCKED.</summary>
    private async Task<Guid> SuspendedEntryAsync(Policy policy, Guid? charge = null)
    {
        var account = Guid.CreateVersion7();
        var chargeId = charge ?? Guid.CreateVersion7();
        var entry = await _slice.EntryAsync(account, "WRITTEN", Day,
            Charge(policy, account, "DEBIT", "LA-01", "25.00", chargeId), Charge(policy, account, "CREDIT", "LA-04", "25.00", chargeId));
        await _slice.DrainAsync();
        var id = await ScalarAsync<Guid>(_db, $"SELECT business_event_id FROM fin.business_event WHERE source_event_id = '{entry.EventId.Value}'");
        (await StatusAsync(id)).ShouldBe("SUSPENDED:PERIOD_LOCKED");
        return id;
    }

    private Task<string> StatusAsync(Guid id) =>
        ScalarAsync<string>(_db, $"SELECT status || ':' || coalesce(exception_reason, '') FROM fin.business_event WHERE business_event_id = '{id}'");

    private Task<long> JournalsAsync(Guid id) =>
        ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_entry j JOIN fin.business_event b ON b.source_event_id = ANY(j.source_event_ids) WHERE b.business_event_id = '{id}'");

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ActAsync(string action, object body, string roles = FinanceRole) =>
        PartyApi.SendAsync(_client, HttpMethod.Post, $"/api/fin/v1/intake-exceptions/{action}", body, roles: roles);

    [Fact]
    public async Task Rerun_after_the_cause_is_fixed_posts_once_and_a_second_rerun_is_a_no_op()
    {
        await SetPeriodAsync("LOCKED");
        var id = await SuspendedEntryAsync(await BoundPolicyAsync());

        // Still locked: the rerun reports the event as still suspended and posts nothing.
        var (stillResponse, still) = await ActAsync("rerun", new { itemIds = new[] { id } });
        stillResponse.StatusCode.ShouldBe(HttpStatusCode.OK, still?.ToJsonString());
        still!["outcomes"]![0]!["outcome"]!.GetValue<string>().ShouldBe("STILL_SUSPENDED");
        (await JournalsAsync(id)).ShouldBe(0);

        await SetPeriodAsync("OPEN"); // the fix
        var (response, body) = await ActAsync("rerun", new { itemIds = new[] { id }, reason = "period reopened" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["outcomes"]![0]!["outcome"]!.GetValue<string>().ShouldBe("POSTED");
        (await StatusAsync(id)).ShouldBe("POSTED:");
        (await JournalsAsync(id)).ShouldBe(1);

        var (again, noop) = await ActAsync("rerun", new { itemIds = new[] { id } });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, noop?.ToJsonString());
        noop!["outcomes"]![0]!["outcome"]!.GetValue<string>().ShouldBe("NO_OP");
        (await JournalsAsync(id)).ShouldBe(1);
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.IntakeException.rerun' AND outcome = 'Succeeded'")).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Mark_duplicate_and_reject_need_a_reason_are_audited_and_leave_the_queue()
    {
        await SetPeriodAsync("LOCKED");
        var policy = await BoundPolicyAsync();
        var duplicate = await SuspendedEntryAsync(policy);
        var rejected = await SuspendedEntryAsync(policy);

        (await ActAsync("mark-duplicate", new { itemIds = new[] { duplicate } })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ActAsync("reject", new { itemIds = new[] { rejected }, reason = "   " })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StatusAsync(duplicate)).ShouldBe("SUSPENDED:PERIOD_LOCKED");

        var (dupResponse, dup) = await ActAsync("mark-duplicate", new { itemIds = new[] { duplicate }, reason = "Same fact posted as JE-100" });
        dupResponse.StatusCode.ShouldBe(HttpStatusCode.OK, dup?.ToJsonString());
        dup!["outcomes"]![0]!["outcome"]!.GetValue<string>().ShouldBe("MARKED_DUPLICATE");
        var (rejResponse, rej) = await ActAsync("reject", new { itemIds = new[] { rejected }, reason = "Entered by mistake" });
        rejResponse.StatusCode.ShouldBe(HttpStatusCode.OK, rej?.ToJsonString());
        rej!["outcomes"]![0]!["outcome"]!.GetValue<string>().ShouldBe("REJECTED");

        (await StatusAsync(duplicate)).ShouldBe("DUPLICATE:");
        (await StatusAsync(rejected)).ShouldBe("REJECTED:");
        (await ScalarAsync<string>(_db, $"SELECT resolution_reason || '|' || resolved_by FROM fin.business_event WHERE business_event_id = '{rejected}'")).ShouldStartWith("Entered by mistake|");
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.IntakeException.markDuplicate' AND outcome = 'Succeeded' AND reason = 'Same fact posted as JE-100'")).ShouldBe(1);
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.IntakeException.reject' AND outcome = 'Succeeded' AND reason = 'Entered by mistake'")).ShouldBe(1);

        var (_, listed) = await PartyApi.SendAsync(_client, HttpMethod.Get, "/api/fin/v1/intake-exceptions?limit=200", roles: FinanceRole);
        listed!["items"]!.AsArray().Select(i => i!["eventId"]!.GetValue<string>()).ShouldNotContain(duplicate.ToString());

        // A closed event can neither be re-run nor closed again, and an unknown id is reported, not an error.
        var (_, closed) = await ActAsync("rerun", new { itemIds = new[] { duplicate, Guid.CreateVersion7() } });
        closed!["outcomes"]![0]!["outcome"]!.GetValue<string>().ShouldBe("NO_OP");
        closed["outcomes"]![1]!["outcome"]!.GetValue<string>().ShouldBe("NOT_FOUND");
        (await ActAsync("reject", new { itemIds = new[] { duplicate }, reason = "x" }, roles: PartyApi.Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_correction_that_overtakes_its_delta_posts_when_the_delta_arrives()
    {
        await SetPeriodAsync("LOCKED");
        var policy = await BoundPolicyAsync();
        var charge = Guid.CreateVersion7();
        var id = await SuspendedEntryAsync(policy, charge); // the entry arrived first: not recognisable as a correction yet

        var corrected = Guid.CreateVersion7();
        var delta = Sample("pol", "ChargeDeltaEmitted");
        delta["chargeId"] = charge.ToString();
        delta["termId"] = policy.TermId.ToString();
        delta["correctionOf"] = corrected.ToString();
        delta["oosSetId"] = Guid.CreateVersion7().ToString();
        await _slice.PublishAsync(ChargeDeltaEmittedV1.Descriptor, "Policy", policy.PolicyId.ToString(), delta,
            BusinessKeys.Empty.With("policyId", policy.PolicyId.ToString()).With("chargeId", charge.ToString())
                .With("policyTermId", policy.TermId.ToString()).With("transactionId", policy.TransactionId.ToString()),
            new EventSet(policy.TransactionId, 1, 1));
        await _slice.DrainAsync();

        (await StatusAsync(id)).ShouldBe("POSTED:");
        (await ScalarAsync<string>(_db, $"SELECT j.correction_of::text FROM fin.journal_entry j JOIN fin.business_event b ON b.source_event_id = ANY(j.source_event_ids) WHERE b.business_event_id = '{id}'")).ShouldBe(corrected.ToString());
    }
}
