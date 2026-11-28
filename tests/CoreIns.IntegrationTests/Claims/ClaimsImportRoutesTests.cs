using System.Net;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// BO-CLM-14: the <c>clm.Import.*</c> and <c>clm.Dsar.*</c> routes. Import twice answers the first claim; the financial history is
/// posted once at the legacy rate; the reverse route removes only its own batch and is refused whole when a claim moved on; the DSAR
/// routes return what the participant returns. POL is the scripted double; amounts are illustrative.
/// </summary>
public sealed class ClaimsImportRoutesTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string MigRoles = "Platform.Admin,Staff.MigrationManager";
    private const string DpoRoles = "Staff.ComplianceOfficer";
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static object Legacy(ScriptedPolicy policy, string legacy, Guid batch) => new
    {
        legacyNumber = legacy, batchId = batch, policyId = policy.PolicyId, lossAt = DateTimeOffset.UtcNow.AddDays(-12).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        notifiedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5)).ToString("yyyy-MM-dd"), lossCause = "COLLISION", lossLocation = "Synthetic location",
        description = "Synthetic legacy claim", coverageCode = "MTPL", reserve = new { amount = 1000.00m, currency = "EUR" },
    };

    private async Task<JsonNode> ImportAsync(object legacyClaim)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/claim", new { legacyClaim }, MigRoles, "migration");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["claim"]!;
    }

    [Fact]
    public async Task Import_twice_is_idempotent_and_needs_the_migration_role()
    {
        var policy = _slice.Policy("MTPL");
        var legacy = $"LEG-{Guid.NewGuid():N}";
        var batch = Guid.NewGuid();
        var payload = Legacy(policy, legacy, batch);

        var (denied, _) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/claim", new { legacyClaim = payload });
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var first = await ImportAsync(payload);
        first.Text("imported").ShouldBe("true");
        var second = await ImportAsync(payload);
        second.Text("imported").ShouldBe("false");
        second.Text("claimId").ShouldBe(first.Text("claimId"));
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.migration_import WHERE legacy_number = '{legacy}'")).ShouldBe(1);

        var (bad, problem) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/claim", new { legacyClaim = new { legacyNumber = "X" } }, MigRoles, "migration");
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
    }

    [Fact]
    public async Task Financial_history_posts_reserve_lines_once_at_the_legacy_rate_and_blocks_the_reversal()
    {
        var policy = _slice.Policy("MTPL");
        var legacy = $"LEG-{Guid.NewGuid():N}";
        var batch = Guid.NewGuid();
        var claim = await ImportAsync(Legacy(policy, legacy, batch));
        var claimId = claim.Text("claimId");

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var history = new
        {
            legacyNumber = legacy,
            lines = new object[]
            {
                new { costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = 1000.00m, currency = "USD", legacyRate = 0.9m, postedOn = today },
                new { costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = -250.00m, currency = "USD", legacyRate = 0.8m, postedOn = today },
            },
        };
        // A reserve below zero is refused (nothing is posted).
        var overdrawn = new { legacyNumber = legacy, lines = new object[] { new { costType = "INDEMNITY", costCategory = "TOTAL_LOSS", amount = -5.00m, currency = "EUR", postedOn = today } } };
        (await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/financial-history", new { legacyClaim = overdrawn }, MigRoles, "migration")).Response.StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/financial-history", new { legacyClaim = history }, MigRoles, "migration");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["claim"]!.Text("posted").ShouldBe("2");
        (await _money.ScalarAsync<string>(
            $"SELECT string_agg(amount::text || '/' || functional_amount::text, ',' ORDER BY sequence) FROM clm.financial_transaction WHERE claim_id = '{claimId}'"))
            .ShouldBe("1000.0000/900.0000,-250.0000/-200.0000");
        (await _money.ScalarAsync<string>($"SELECT status FROM clm.transaction_set WHERE claim_id = '{claimId}'")).ShouldBe("APPROVED");

        // Posting twice adds nothing.
        var (again, repeated) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/financial-history", new { legacyClaim = history }, MigRoles, "migration");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, repeated?.ToJsonString());
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.financial_transaction WHERE claim_id = '{claimId}'")).ShouldBe(2);

        // The ledger is append-only: the claim with history is no longer reversible.
        var (reverse, blocked) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/reverse", new { batchId = batch }, MigRoles, "migration");
        reverse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blocked?.ToJsonString());
        blocked.Text("code").ShouldContain("REVERSE-LIVE-ACTIVITY");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{claimId}'")).ShouldBe(1);

        // The currency changeover plan previews the open USD reserve (750) at the plan rate and posts nothing.
        var (convert, plan) = await _money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/import/convert-currency", new { plan = new { fromCurrency = "USD", toCurrency = "EUR", rate = 0.5m } }, MigRoles, "migration");
        convert.StatusCode.ShouldBe(HttpStatusCode.OK, plan?.ToJsonString());
        plan!["conversionResult"]!.Text("applied").ShouldBe("false");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.financial_transaction WHERE claim_id = '{claimId}'")).ShouldBe(2);
    }

    [Fact]
    public async Task Reverse_removes_the_batch_only_and_a_second_call_removes_nothing()
    {
        var policy = _slice.Policy("MTPL");
        var batch = Guid.NewGuid();
        var other = Guid.NewGuid();
        var a = await ImportAsync(Legacy(policy, $"LEG-{Guid.NewGuid():N}", batch));
        var kept = await ImportAsync(Legacy(policy, $"LEG-{Guid.NewGuid():N}", other));

        // Dry run removes nothing.
        var (dry, dryBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/reverse?dryRun=true", new { batchId = batch }, MigRoles, "migration");
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{a.Text("claimId")}'")).ShouldBe(1);

        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/reverse", new { batchId = batch }, MigRoles, "migration");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["reversalResult"]!.Text("reversed").ShouldBe("1");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{a.Text("claimId")}'")).ShouldBe(0);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{kept.Text("claimId")}'")).ShouldBe(1);

        var (again, none) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/import/reverse", new { batchId = batch }, MigRoles, "migration");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, none?.ToJsonString());
        none!["reversalResult"]!.Text("reversed").ShouldBe("0");
    }

    [Fact]
    public async Task Dsar_routes_give_the_result_of_the_participant()
    {
        var policy = _slice.Policy();
        var (submitted, claim) = await _slice.SubmitAsync(ClaimsSlice.Fnol(policy));
        submitted.IsSuccessStatusCode.ShouldBeTrue(claim?.ToJsonString());
        var party = new PartyId(policy.InsuredPartyId);
        var participant = _slice.Factory.Services.GetServices<IDsarParticipant>().Single(p => p.Module == "CLM");

        var engine = await participant.ExportAsync(SubjectRef.ForParty(party), new DsarContext(Guid.NewGuid(), "dpo", null), TestContext.Current.CancellationToken);
        var (export, exported) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/dsar/export", new { party = new { partyId = party.Value } }, DpoRoles, "dpo");
        export.StatusCode.ShouldBe(HttpStatusCode.OK, exported?.ToJsonString());
        var package = exported!["claimDataPackage"]!;
        package["outcomes"]!.AsArray().Count.ShouldBe(engine.Outcomes.Count);
        package["outcomes"]![0]!.Text("recordCount").ShouldBe(engine.Outcomes[0].RecordCount.ToString());
        package.Text("exportPayload").ShouldContain(claim.Text("claimNumber"));

        var purged = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/dsar/purge", new { party = party.Value, scope = new { } }, DpoRoles, "dpo");
        purged.Response.StatusCode.ShouldBe(HttpStatusCode.OK, purged.Body?.ToJsonString());
        purged.Body!["result"]!["outcomes"]![0]!.Text("kind").ShouldBe("Restricted");
        purged.Body["result"]!["outcomes"]![0]!.Text("retentionEnd").ShouldBe(DsarRetention.UnsetF404);

        var restricted = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/dsar/restrict", new { party = party.Value, scope = new { reason = "OBJECTION" } }, DpoRoles, "dpo");
        restricted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, restricted.Body?.ToJsonString());
        var rectified = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/dsar/rectify", new { party = party.Value, corrections = new { name = "x" } }, DpoRoles, "dpo");
        rectified.Response.StatusCode.ShouldBe(HttpStatusCode.OK, rectified.Body?.ToJsonString());

        // One restriction row per subject and class, however many calls.
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.dsar_restriction WHERE subject_party_id = '{party.Value}'")).ShouldBe(1);

        var (denied, _) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/dsar/export", new { party = party.Value }, "Staff.ClaimsHandler", "alice");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (invalid, _) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/dsar/export", new { party = "nope" }, DpoRoles, "dpo");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
