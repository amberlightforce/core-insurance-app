using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Claims;

namespace CoreIns.IntegrationTests.Migration;

/// <summary>
/// SL6-MIG-CLM-RI-IMPORT: an open claim imported through a migration batch keeps its original deadline (the running clock is
/// re-created from the original notified date), the treaty-year opening aggregate, the bureau handover date and the fiscal series
/// continuity are recorded, a double import leaves one claim and one tracker, and the claim cannot be reversed after a reserve
/// change, nor a batch holding RI or CMP objects (D-SL6-05). POL is the scripted double; amounts and the clock length are illustrative.
/// </summary>
public sealed class ClaimRiImportTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string MigRoles = "Platform.Admin,Staff.MigrationManager";
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task Open_claim_keeps_the_original_deadline_and_the_imports_are_idempotent_and_reverse_is_blocked_after_a_reserve_change()
    {
        var policy = _slice.Policy("MTPL");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var legacyClaim = $"LEG-CLM-{suffix}";
        var treaty = $"XOL-MTPL-{suffix}";
        var series = $"A-{suffix}";
        var plate = $"IKX{Random.Shared.Next(1000, 9999)}";
        var notifiedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));

        object[] items =
        [
            new { module = "CLM", objectType = "Claim", legacyId = legacyClaim, payload = ClaimPayload(policy, legacyClaim, notifiedOn) },
            new { module = "RI", objectType = "TreatyYear", legacyId = $"{treaty}/2026", payload = new { treatyId = treaty, contractYear = 2026, openingAggregate = 125000.50m } },
            new { module = "CMP", objectType = "BureauHandover", legacyId = $"HO-{plate}", payload = new { vehicleRef = $"{plate[..3]}-{plate[3..]}", handoverDate = "2026-03-01" } },
            new
            {
                module = "CMP", objectType = "FiscalContinuity", legacyId = series,
                payload = new { seriesId = series, lastLegacyNumber = 4120, marks = new[] { new { number = 4119, mark = $"MARK-{suffix}-4119" }, new { number = 4120, mark = $"MARK-{suffix}-4120" } } },
            },
        ];

        // Dry run writes nothing; the real run needs approval by another user.
        var batch = await CreateBatchAsync(items);
        Outcomes(await StartAsync(batch, dryRun: true)).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED", "IMPORTED"]);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.migration_import WHERE legacy_number = '{legacyClaim}'")).ShouldBe(0);
        (await StartAsync(batch, dryRun: false)).Text("batch.status").ShouldBe("PENDING_APPROVAL");
        var done = await ApproveAsync(batch);
        Outcomes(done).ShouldBe(["IMPORTED", "IMPORTED", "IMPORTED", "IMPORTED"]);
        var claimId = done["outcomes"]![0].Text("coreId");

        // The claim: origin MIGRATION, the original notified date, the reserve as given, open.
        (await _money.ScalarAsync<string>(
            $"SELECT m.origin || '/' || c.status || '/' || c.notice_on::text || '/' || m.opening_reserve::text FROM clm.migration_import m JOIN clm.claim c USING (claim_id) WHERE claim_id = '{claimId}'"))
            .ShouldBe($"MIGRATION/OPEN/{notifiedOn:yyyy-MM-dd}/18000.0000");

        // The running clock keeps its original start: the deadline is the notified date + 30 days (end of that Athens day), still running,
        // not a fresh 30 days from today.
        (await _money.ScalarAsync<string>(
            $"SELECT status || '/' || (deadline AT TIME ZONE 'Europe/Athens')::date::text || '/' || (started_at AT TIME ZONE 'Europe/Athens')::date::text FROM cmp.clock WHERE subject_id = '{claimId}'"))
            .ShouldBe($"RUNNING/{notifiedOn.AddDays(30):yyyy-MM-dd}/{notifiedOn:yyyy-MM-dd}");
        (await _money.ScalarAsync<bool>($"SELECT c.clock_id = k.clock_id FROM clm.migration_import c, cmp.clock k WHERE c.claim_id = '{claimId}' AND k.subject_id = '{claimId}'")).ShouldBeTrue();

        // RI, bureau and fiscal records.
        (await _money.ScalarAsync<decimal>($"SELECT opening_aggregate FROM ri.tracker WHERE treaty_id = '{treaty}' AND contract_year = 2026")).ShouldBe(125000.50m);
        (await _money.ScalarAsync<string>($"SELECT handover_date::text FROM cmp.bureau_handover WHERE vehicle_ref = '{plate}'")).ShouldBe("2026-03-01");
        (await _money.ScalarAsync<long>($"SELECT last_number FROM cmp.fiscal_series WHERE series_id = '{series}'")).ShouldBe(4120);
        (await _money.ScalarAsync<string>($"SELECT mark FROM cmp.legacy_fiscal_mark WHERE series_id = '{series}' AND legacy_number = 4120")).ShouldBe($"MARK-{suffix}-4120");

        // Import twice -> one claim, one tracker: the same legacy keys in a second batch are skipped by the xref.
        var again = await CreateBatchAsync(items[..2]);
        await StartAsync(again, dryRun: true);
        await StartAsync(again, dryRun: false);
        Outcomes(await ApproveAsync(again)).ShouldBe(["SKIPPED_EXISTING", "SKIPPED_EXISTING"]);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.migration_import WHERE legacy_number = '{legacyClaim}'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM ri.tracker WHERE treaty_id = '{treaty}'")).ShouldBe(1);

        // A batch that also holds RI and CMP objects has no reverser for them: NOT_REVERSIBLE, nothing removed (D-SL6-05).
        var (mixed, notReversible) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, MigRoles, "alice");
        mixed.StatusCode.ShouldBe(HttpStatusCode.OK, notReversible?.ToJsonString());
        notReversible.Text("batch.status").ShouldBe("NOT_REVERSIBLE");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{claimId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_reserve_change_since_the_import_blocks_the_reversal_of_the_claim()
    {
        var policy = _slice.Policy("MTPL");
        var legacyClaim = $"LEG-CLM-{Guid.NewGuid():N}";
        object[] items = [new { module = "CLM", objectType = "Claim", legacyId = legacyClaim, payload = ClaimPayload(policy, legacyClaim, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3))) }];
        var batch = await CreateBatchAsync(items);
        await StartAsync(batch, dryRun: true);
        await StartAsync(batch, dryRun: false);
        var claimId = (await ApproveAsync(batch))["outcomes"]![0].Text("coreId");

        var exposureId = await _money.ScalarAsync<string>($"SELECT exposure_id::text FROM clm.exposure WHERE claim_id = '{claimId}'");
        var moneyClaim = new MoneyClaim(claimId, "n/a", exposureId, policy.InsuredPartyId.ToString());
        var reserve = await _money.BuildAndSubmitAsync(moneyClaim, [ClaimsMoney.Reserve(moneyClaim, 500m)]);
        reserve.Text("status").ShouldBe("APPROVED");

        var (response, reversed) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, MigRoles, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        Outcomes(reversed).ShouldBe(["REVERSAL_BLOCKED"]);
        reversed!["outcomes"]![0].Text("errorCode").ShouldBe("MIG-ERR-REVERSAL-BLOCKED");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{claimId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task An_untouched_imported_claim_is_reversed_and_can_be_imported_again()
    {
        var policy = _slice.Policy("MTPL");
        var legacyClaim = $"LEG-CLM-{Guid.NewGuid():N}";
        object[] items = [new { module = "CLM", objectType = "Claim", legacyId = legacyClaim, payload = ClaimPayload(policy, legacyClaim, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3))) }];
        var batch = await CreateBatchAsync(items);
        await StartAsync(batch, dryRun: true);
        await StartAsync(batch, dryRun: false);
        var claimId = (await ApproveAsync(batch))["outcomes"]![0].Text("coreId");

        var (response, reversed) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, MigRoles, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        Outcomes(reversed).ShouldBe(["REVERSED"]);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{claimId}'")).ShouldBe(0);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.migration_import WHERE legacy_number = '{legacyClaim}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_batch_of_a_claim_and_a_treaty_year_reverses_removes_the_tracker_cancels_the_clock_and_can_be_imported_again()
    {
        var policy = _slice.Policy("MTPL");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var legacyClaim = $"LEG-CLM-{suffix}";
        var treaty = $"XOL-REV-{suffix}";
        object[] items =
        [
            new { module = "CLM", objectType = "Claim", legacyId = legacyClaim, payload = ClaimPayload(policy, legacyClaim, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3))) },
            new { module = "RI", objectType = "TreatyYear", legacyId = $"{treaty}/2026", payload = new { treatyId = treaty, contractYear = 2026, openingAggregate = 5000.00m } },
        ];
        var batch = await CreateBatchAsync(items);
        await StartAsync(batch, dryRun: true);
        await StartAsync(batch, dryRun: false);
        var claimId = (await ApproveAsync(batch))["outcomes"]![0].Text("coreId");
        (await _money.ScalarAsync<string>($"SELECT status FROM cmp.clock WHERE subject_id = '{claimId}'")).ShouldBe("RUNNING");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM ri.tracker WHERE treaty_id = '{treaty}' AND batch_id = '{batch}'")).ShouldBe(1);

        // Dry run reports both as reversible and removes nothing.
        var (dryResponse, dry) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = true }, MigRoles, "alice");
        dryResponse.StatusCode.ShouldBe(HttpStatusCode.OK, dry?.ToJsonString());
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM ri.tracker WHERE treaty_id = '{treaty}'")).ShouldBe(1);

        var (response, reversed) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/reverse", new { batchId = batch, dryRun = false }, MigRoles, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, reversed?.ToJsonString());
        Outcomes(reversed).ShouldBe(["REVERSED", "REVERSED"]);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim WHERE claim_id = '{claimId}'")).ShouldBe(0);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM ri.tracker WHERE treaty_id = '{treaty}'")).ShouldBe(0);
        (await _money.ScalarAsync<string>($"SELECT status FROM cmp.clock WHERE subject_id = '{claimId}'")).ShouldBe("CANCELLED");

        // The same treaty year imports again in a new batch (the tracker is gone, so the open-aggregate rule starts clean).
        var again = await CreateBatchAsync(items[1]);
        await StartAsync(again, dryRun: true);
        await StartAsync(again, dryRun: false);
        Outcomes(await ApproveAsync(again)).ShouldBe(["IMPORTED"]);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM ri.tracker WHERE treaty_id = '{treaty}' AND batch_id = '{again}'")).ShouldBe(1);
    }

    private static object ClaimPayload(ScriptedPolicy policy, string legacy, DateOnly notifiedOn) => new
    {
        legacyNumber = legacy, policyId = policy.PolicyId, lossAt = DateTimeOffset.UtcNow.AddDays(-12).ToString("yyyy-MM-ddTHH:mm:ssZ"), notifiedOn = notifiedOn.ToString("yyyy-MM-dd"),
        lossCause = "COLLISION", lossLocation = "Synthetic location", description = "Synthetic legacy bodily injury claim", coverageCode = "MTPL",
        reserve = new { amount = 18000.00m, currency = "EUR" },
    };

    private async Task<string> CreateBatchAsync(params object[] items)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches", new { name = "claims", items }, MigRoles, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("batch.batchId");
    }

    private async Task<JsonNode> StartAsync(string batchId, bool dryRun)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/start", new { batchId, dryRun }, MigRoles, "alice");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private async Task<JsonNode> ApproveAsync(string batchId)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/mig/v1/batches/approve", new { batchId, decision = "APPROVE" }, MigRoles, "bob");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private static string[] Outcomes(JsonNode? body) => [.. body!["outcomes"]!.AsArray().Select(o => o.Text("outcome"))];
}
