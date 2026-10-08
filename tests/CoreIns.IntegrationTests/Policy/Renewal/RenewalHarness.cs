using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Product;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Renewal;

/// <summary>
/// A bound one-year policy on a host with a shiftable clock, and the calls of the renewal journey (E2E-04 steps 1-7):
/// "Renew now", offer, explicit acceptance. Direct SQL (as the superuser) only reads, or simulates what the CHANGE and CANCEL
/// work packages will write (a later head transaction, a cancelled term version) in the shape those commands will use.
/// </summary>
internal sealed class RenewalHarness(PolicySlice slice, string superuserConnectionString)
{
    public const string Manager = "Staff.UnderwritingManager";

    public PolicySlice Slice { get; } = slice;

    public ShiftableClock Clock { get; } = (ShiftableClock)slice.Factory.Services.GetRequiredService<IClock>();

    public string PolicyId { get; private set; } = string.Empty;

    public string TermId { get; private set; } = string.Empty;

    public string PolicyNumber { get; private set; } = string.Empty;

    public string PartyId { get; private set; } = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Quotes and binds a new policy (term 1 starts in two days) with the first 1.0 version of the product.</summary>
    public async Task BindPolicyAsync(string? birthDate = null)
    {
        PartyId = birthDate is null ? await Slice.CreatePartyAsync() : await Slice.CreatePartyAsync(birthDate);
        var (jobId, _, _) = await Slice.DraftAsync(PartyId, Clock.Now.ToDateTimeOffset().AddDays(2));
        var (quoted, quote) = await Slice.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await Slice.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");
        PolicyId = bind.Text("policyId");
        TermId = bind.Text("termId");
        PolicyNumber = bind.Text("policyNumber");
    }

    /// <summary>
    /// Imports the MOTOR-GR 1.1 seed for this host's product code: it closes 1.0's windows at 2026-10-01 (D-SL3-15), so a term
    /// that starts after that date (a renewal) resolves to it while term 1, bound before, keeps 1.0.
    /// </summary>
    public async Task<(HttpResponseMessage Response, JsonNode? Body)> ImportVersion11Async()
    {
        var definition = JsonNode.Parse(ProductSeeds.MotorPrivateCar11Json())!.AsObject();
        definition["product"]!["code"] = Slice.Product;
        return await ProductApi.ImportAsync(Slice.Client, definition);
    }

    /// <summary>Moves the clock to <paramref name="daysBeforeExpiry"/> days before term 1 ends.</summary>
    public async Task GoToAsync(double daysBeforeExpiry)
    {
        var expiry = await ScalarAsync<DateTime>($"SELECT valid_to FROM pol.policy_term WHERE term_id = '{TermId}' AND recorded_to IS NULL");
        var target = new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)).AddDays(-daysBeforeExpiry);
        Clock.Advance(target - Clock.Now.ToDateTimeOffset());
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> CreateAsync(string? termId = null, bool dryRun = false, Guid? key = null, string roles = Underwriter, string? user = null) =>
        PostAsync("/api/pol/v1/renewals" + (dryRun ? "?dryRun=true" : string.Empty), new { termId = termId ?? TermId }, key, roles, user);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> OfferAsync(string? termId = null, string roles = Underwriter, string? user = null) =>
        PostAsync("/api/pol/v1/renewals/offer", new { termId = termId ?? TermId }, null, roles, user);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> AcceptAsync(string? termId = null, bool dryRun = false, Guid? key = null, string roles = Underwriter, string? user = null, object? evidence = null) =>
        PostAsync("/api/pol/v1/renewals/accept" + (dryRun ? "?dryRun=true" : string.Empty), new { termId = termId ?? TermId, acceptanceEvidence = evidence }, key, roles, user);

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, Guid? key = null, string roles = Underwriter, string? user = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        request.Content = JsonContent.Create(body);
        var response = await Slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> GetAsync(string path, string roles = Underwriter)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        var response = await Slice.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(superuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(superuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public Task<long> CountEventsAsync(string eventType) =>
        ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = '{eventType}' AND aggregate_id = '{PolicyId}'");

    public Task<long> TermVersionsAsync() => ScalarAsync<long>($"SELECT count(DISTINCT term_id) FROM pol.policy_term WHERE policy_id = '{PolicyId}'");

    /// <summary>
    /// Simulates a bound change or cancellation on term 1 the way the CHANGE / CANCEL commands will write it: a later head
    /// transaction at a fresh watermark, and a new term version that closes the old one at that watermark.
    /// </summary>
    public async Task SimulateLaterHeadAsync(string transactionKind, string? termState = null)
    {
        var transaction = Guid.CreateVersion7();
        var state = termState is null ? "NULL" : $"'{termState}'";
        var sql = """
            UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 second', record_version = record_version + 1 WHERE policy_id = '@P';
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, rating_artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            SELECT '@TX', policy_id, term_id, gen_random_uuid(), legal_entity_id, '@KIND',
                   (SELECT max(sequence) FROM pol.policy_transaction WHERE policy_id = '@P') + 1, effective_at,
                   (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '@P'), configuration_hash, artefact_hash, rating_artefact_hash,
                   resolution_hash, '{}'::jsonb, 0, 0, 0, currency, 'simulated', 'simulated', origin
              FROM pol.policy_transaction WHERE transaction_id = (SELECT head_transaction_id FROM pol.policy_term WHERE term_id = '@T' AND recorded_to IS NULL);
            CREATE TEMP TABLE old_term AS SELECT * FROM pol.policy_term WHERE term_id = '@T' AND recorded_to IS NULL;
            UPDATE pol.policy_term SET recorded_to = (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '@P')
             WHERE term_id = '@T' AND recorded_to IS NULL;
            UPDATE old_term SET term_version_id = gen_random_uuid(), recorded_from = (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '@P'),
                   recorded_to = NULL, head_transaction_id = '@TX', state = COALESCE(@STATE, state);
            INSERT INTO pol.policy_term SELECT * FROM old_term;
            """;
        await ExecuteAsync(sql.Replace("@P", PolicyId, StringComparison.Ordinal).Replace("@TX", transaction.ToString(), StringComparison.Ordinal)
            .Replace("@KIND", transactionKind, StringComparison.Ordinal).Replace("@T", TermId, StringComparison.Ordinal).Replace("@STATE", state, StringComparison.Ordinal));
    }

    /// <summary>Asserts the status and error code of a failed call.</summary>
    public static void ShouldFailWith(HttpResponseMessage response, JsonNode? body, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status, body?.ToJsonString());
        body.Text("code").ShouldBe(code);
    }
}
