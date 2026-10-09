using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Money2;

/// <summary>Real Claims/Platform pipeline and database; no approval requests or decisions are fabricated.</summary>
public sealed class Money2ApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();
    private Task<MoneyClaim> ClaimAsync() => _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));

    private async Task<Guid> RecoveryAsync(MoneyClaim claim)
    {
        var id = Guid.NewGuid();
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand("""
            INSERT INTO clm.recovery(recovery_id,claim_id,exposure_id,type,counterparty_party_id,expected_amount,currency,status,milestones,
                allocation_rule,updated_at,legal_entity_id,jurisdiction,created_at,created_by,record_version)
            SELECT @id,c.claim_id,@exposure,'SALVAGE',c.insured_party_id,100000,'EUR','OPEN','[]'::jsonb,'PRO_RATA_PAID',c.created_at,
                c.legal_entity_id,c.jurisdiction,c.created_at,'USER:test-recovery',1 FROM clm.claim c WHERE c.claim_id=@claim
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("exposure", Guid.Parse(claim.ExposureId));
        command.Parameters.AddWithValue("claim", Guid.Parse(claim.ClaimId));
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        return id;
    }

    private static object RecoveryReserve(MoneyClaim claim, Guid recoveryId, decimal amount) => new
    {
        kind = "RECOVERY_RESERVE", recoveryId, exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR",
        amount = Money(amount), reason = "ASSESSMENT",
    };

    [Fact]
    public async Task D_SL4_08_A_manager_making_a_large_loss_still_needs_a_distinct_checker()
    {
        var claim = await ClaimAsync();
        var pending = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 60000m)], Manager, ManagerUser);
        pending.Text("status").ShouldBe("PENDING_APPROVAL");
        var requestId = pending.Text("approvalRequestId");
        (await _money.DecideAsync(requestId, roles: Manager, user: ManagerUser)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _money.DecideAsync(requestId, roles: Manager, user: "second-manager")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        (await _money.SetAsync(pending.Text("setId"))).Text("status").ShouldBe("APPROVED");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["reserved"]).ShouldBe(60000m);
    }

    [Fact]
    public async Task D_SL4_07_Recovery_reserve_split_is_aggregate_and_dry_run_matches_submit()
    {
        var claim = await ClaimAsync();
        var recovery = await RecoveryAsync(claim);
        var transactions = Enumerable.Range(0, 20).Select(_ => RecoveryReserve(claim, recovery, 300m)).ToArray();
        var (previewResponse, preview) = await _money.BuildAsync(claim, transactions, dryRun: true);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK, preview?.ToJsonString());
        preview.Text("authorityPreview.0.basis").ShouldBe("EXPOSURE_TOTAL_RECOVERY_RESERVE");
        preview.Text("authorityPreview.0.outcome").ShouldBe("REFER");
        Amount(preview!["authorityPreview"]![0]!["amount"]).ShouldBe(6000m);
        var submitted = await _money.BuildAndSubmitAsync(claim, transactions);
        submitted.Text("status").ShouldBe("PENDING_APPROVAL");
        submitted.Text("authorityChecks.0.basis").ShouldBe("EXPOSURE_TOTAL_RECOVERY_RESERVE");
        Amount(submitted["authorityChecks"]![0]!["amount"]).ShouldBe(6000m);
        (await _money.DecideAsync(submitted.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        var totals = (await _money.FinancialsAsync(claim))["totals"]!;
        Amount(totals["openRecoveryReserve"]).ShouldBe(6000m);
        Amount(totals["netIncurred"]).ShouldBe(-6000m);
    }

    [Fact]
    public async Task D_SL4_14_Rejection_withdraws_sibling_requests_through_real_Platform()
    {
        var claim = await ClaimAsync();
        var (account, _) = await _money.CaptureAsync(claim);
        var pending = await _money.BuildAndSubmitAsync(claim,
            [Reserve(claim, 40000m, "ASSESSOR_FEE", costType: "EXPENSE_ALLOCATED"), Payment(claim, 6000m, account, "PARTIAL")]);
        var ids = pending["set"]!["approvals"]!.AsArray().Select(a => a!.Text("approvalRequestId")).ToList();
        ids.Count.ShouldBe(3);
        (await _money.DecideAsync(ids[0], "Reject")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        (await _money.SetAsync(pending.Text("setId"))).Text("status").ShouldBe("REJECTED");
        foreach (var id in ids.Skip(1))
        {
            var (response, body) = await _money.SendAsync(HttpMethod.Get, $"/api/plt/v1/approval/{id}", roles: Manager, user: ManagerUser);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.Text("request.status").ShouldBe("Withdrawn");
        }
    }

    [Fact]
    public async Task REQ_CLM_073_Open_recovery_close_error_is_typed_with_exposure_ids()
    {
        var claim = await ClaimAsync();
        await RecoveryAsync(claim);
        var (_, view) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/claims/{claim.ClaimId}");
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/close", new
        {
            claimId = claim.ClaimId, expectedRecordVersion = view!["claim"]!["recordVersion"]!.GetValue<int>(), outcome = "COMPLETED",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("errors.0.code").ShouldBe("OPEN_RECOVERY");
        body.Text("errors.0.exposureIds.0").ShouldBe(claim.ExposureId);
        body!["closeGuardErrors"].ShouldBeNull();
    }

    [Fact]
    public async Task PITFALLS_07_Public_recovery_amount_cannot_record_cash()
    {
        var claim = await ClaimAsync();
        var recovery = await RecoveryAsync(claim);
        var (response, body) = await _money.BuildAsync(claim, [new
        {
            kind = "RECOVERY", recoveryId = recovery, exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = Money(1700m),
        }]);
        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented, body?.ToJsonString());
        Amount((await _money.FinancialsAsync(claim))["totals"]!["recoveries"]).ShouldBe(0m);
    }

    [Fact]
    public async Task D_SL4_14_Set_list_paginates_and_the_database_freezes_system_evidence()
    {
        var claim = await ClaimAsync();
        var (_, first) = await _money.BuildAsync(claim, [Reserve(claim, 100m)]);
        var (_, second) = await _money.BuildAsync(claim, [Reserve(claim, 200m)]);
        var (response, page) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/transaction-sets?claimId={claim.ClaimId}&limit=1");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page.Text("items.0.setId").ShouldBe(second.Text("setId"));
        var (_, next) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/transaction-sets?claimId={claim.ClaimId}&limit=1&cursor={page.Text("nextCursor")}");
        next.Text("items.0.setId").ShouldBe(first.Text("setId"));
        next!["nextCursor"].ShouldBeNull();
        var refused = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync(
            $"UPDATE clm.transaction_set SET evidence_kind='BIL_ALLOCATION',evidence_ref='forged' WHERE set_id='{first.Text("setId")}'", Ct));
        refused.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task PITFALLS_10_Missing_authoritative_evidence_fails_closed_without_a_system_set()
    {
        var claim = await ClaimAsync();
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.Actor = ActorRef.User("maker");
        context.Roles = [Manager];
        await scope.ServiceProvider.GetRequiredService<DbSession>().BeginTransactionAsync(Ct);
        var engine = scope.ServiceProvider.GetRequiredService<IClaimFinancialEngine>();
        var error = await Should.ThrowAsync<DomainException>(() => engine.SubmitSystemSetAsync(new ClaimId(Guid.Parse(claim.ClaimId)),
            new ClaimFinancialEvidence("BIL_ALLOCATION", Guid.NewGuid().ToString("D")), Ct));
        error.Error.Code.Value.ShouldBe("CLM-ERR-NOT-AVAILABLE");
        context.Actor.ShouldBe(ActorRef.User("maker"));
        var (_, page) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/transaction-sets?claimId={claim.ClaimId}");
        page!["items"]!.AsArray().Count.ShouldBe(0);
    }

    // A scripted owner port verifies engine mechanics; it does not claim live BIL/FS evidence-provider acceptance.
    private sealed class ScriptedEvidenceSource(ResolvedClaimFinancialEvidence resolved) : IClaimFinancialEvidenceSource
    {
        public Task<ResolvedClaimFinancialEvidence?> ResolveAsync(ClaimId claimId, ClaimFinancialEvidence evidence, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedClaimFinancialEvidence?>(resolved);
    }

    [Fact]
    public async Task D_SL4_12_CLEARING_system_payment_refers_and_after_real_approval_never_calls_Billing()
    {
        var claim = await ClaimAsync();
        var claimId = new ClaimId(Guid.Parse(claim.ClaimId));
        var insurer = PartyId.New();
        var resolved = new ResolvedClaimFinancialEvidence(new TransactionSetBuildRequest
        {
            ClaimId = claimId,
            Transactions = [new TransactionSetBuildRequest.TransactionItem
            {
                Kind = TransactionSetBuildRequest.TransactionItem.KindValue.Payment,
                ExposureId = new ExposureId(Guid.Parse(claim.ExposureId)), CostType = "INDEMNITY", CostCategory = "VEHICLE_REPAIR",
                Amount = new Money(1200m, Currency.EUR), PayeePartyId = insurer, PayeeAccountId = Guid.NewGuid(),
            }],
        }, "CLEARING", insurer);
        await using var scripted = new ClaimsSlice(database.AppConnectionString, services => services.AddSingleton<IClaimFinancialEvidenceSource>(new ScriptedEvidenceSource(resolved)));
        await using var scope = scripted.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST"); context.Jurisdiction = Jurisdiction.Parse("GR");
        context.Actor = ActorRef.User("system-caller"); context.Roles = [Manager];
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64)); context.IdempotencyKey = IdempotencyKey.New();
        var session = services.GetRequiredService<DbSession>();
        await session.BeginTransactionAsync(Ct);
        var engine = services.GetRequiredService<IClaimFinancialEngine>();
        var evidence = new ClaimFinancialEvidence("FS_NOTIFICATION", Guid.NewGuid().ToString("D"));
        var pending = await engine.SubmitSystemSetAsync(claimId, evidence, Ct);
        pending.Status.ShouldBe("PENDING_APPROVAL");
        var replay = await engine.SubmitSystemSetAsync(claimId, evidence, Ct);
        replay.SetId.ShouldBe(pending.SetId);
        await session.CommitAsync(Ct);
        context.Actor.ShouldBe(ActorRef.User("system-caller"));
        foreach (var approval in pending.Set!.Approvals!)
        {
            (await _money.DecideAsync(approval.ApprovalRequestId.Value.ToString("D"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await _money.DrainAsync();
        var payments = await _money.PaymentsAsync(claim);
        payments[0].Text("method").ShouldBe("CLEARING");
        payments[0].Text("status").ShouldBe("APPROVED");
        payments[0]!["disbursementId"].ShouldBeNull();
    }
}
