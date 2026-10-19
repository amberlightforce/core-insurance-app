using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Recoveries;

/// <summary>SL4-CLM-RECOVERY-OPS over the real Claims/Platform pipeline and database; BIL receivable registration is a scripted double.</summary>
public sealed class RecoveryOpsTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string SpecialistRole = "Staff.RecoverySpecialist";
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;
    private readonly FakeReceivables _receivables = new();

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString, services => services.AddSingleton<IBillingReceivableService>(_receivables));
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<MoneyClaim> ClaimAsync() => _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));

    private async Task<JsonNode> CreateAsync(MoneyClaim claim, string type, decimal expected)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/recoveries", new
        {
            claimId = claim.ClaimId, exposureId = claim.ExposureId, type, counterpartyPartyId = Guid.NewGuid(), expectedAmount = Money(expected),
            reason = "AT_FAULT_THIRD_PARTY",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!["recovery"]!;
    }

    private static object RecoveryReserve(MoneyClaim claim, string recoveryId, decimal amount) => new
    {
        kind = "RECOVERY_RESERVE", recoveryId, exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR",
        amount = Money(amount), reason = "ASSESSMENT",
    };

    private async Task<JsonNode> GetAsync(string recoveryId)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/recoveries/{recoveryId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["recovery"]!;
    }

    private async Task<JsonNode> DemandAsync(JsonNode recovery, decimal amount)
    {
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/recoveries/demand", new
        {
            recoveryId = recovery.Text("recoveryId"), expectedRecordVersion = recovery["recordVersion"]!.GetValue<int>(), amount = Money(amount), dueDate = "2026-12-31",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    /// <summary>The BIL consumer path: <c>CashAllocated</c> for a receivable, handled in a scope like the worker would.</summary>
    private async Task CashAllocatedAsync(MoneyClaim claim, string recoveryId, Guid receivableId, decimal amount, PaymentId? receipt = null)
    {
        #pragma warning disable CA2000 // the scope is left to the host: disposing it after a manual commit trips DbSession's own teardown order
        var scope = _slice.Factory.Services.CreateAsyncScope();
#pragma warning restore CA2000
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.Actor = ActorRef.Service("bil-test");
        context.Roles = [];
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        _ = services.GetRequiredService<CoreIns.Modules.Claims.Persistence.ClaimsDbContext>(); // created first, so the scope disposes it after the session
        var session = services.GetRequiredService<DbSession>();
        await session.BeginTransactionAsync(Ct);
        var handler = services.GetRequiredService<CoreIns.Modules.Claims.Events.CashAllocatedHandler>();
        await handler.HandleAsync(null!, new CashAllocatedV1
        {
            ReceiptId = receipt ?? PaymentId.New(), Allocations = [], ArrearsCleared = [], DownPayment = false,
            ReceivableId = receivableId, ReceivableAmount = new Money(amount, Currency.EUR), ClaimId = new ClaimId(Guid.Parse(claim.ClaimId)),
            RecoveryId = Guid.Parse(recoveryId), SourceType = "CLM_CLAIM_PAYMENT",
        }, Ct);
        await session.CommitAsync(Ct);
    }

    [Fact]
    public async Task REQ_CLM_143_Create_get_list_and_milestones_follow_the_state_model()
    {
        var claim = await ClaimAsync();
        var recovery = await CreateAsync(claim, "SUBROGATION", 200000m);
        recovery.Text("state").ShouldBe("OPEN");
        recovery.Text("type").ShouldBe("SUBROGATION");
        (await GetAsync(recovery.Text("recoveryId"))).Text("milestones.0.milestone").ShouldBe("CASE_OPENED");

        var (response, page) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/recoveries?claimId={claim.ClaimId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().Count.ShouldBe(1);

        async Task<(HttpResponseMessage Response, JsonNode? Body)> MilestoneAsync(string milestone, int version) => await _money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/recoveries/record-milestone",
            new { recoveryId = recovery.Text("recoveryId"), expectedRecordVersion = version, milestone, occurredOn = "2026-10-19" });

        var (bad, badBody) = await MilestoneAsync("ARBITRATION_STARTED", 1);
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, badBody?.ToJsonString());
        badBody.Text("code").ShouldContain("RECOVERY-STATE");

        var (stale, _) = await MilestoneAsync("COUNTERPARTY_ACKNOWLEDGED", 7);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var current = await DemandAsync(recovery, 200000m);
        current["recovery"]!.Text("state").ShouldBe("DEMANDED");
        var version = current["recovery"]!["recordVersion"]!.GetValue<int>();
        var (ok, disputed) = await MilestoneAsync("DISPUTED", version);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, disputed?.ToJsonString());
        disputed!["recovery"]!.Text("state").ShouldBe("DISPUTED");
        var (arb, arbitration) = await MilestoneAsync("ARBITRATION_STARTED", disputed["recovery"]!["recordVersion"]!.GetValue<int>());
        arb.StatusCode.ShouldBe(HttpStatusCode.OK, arbitration?.ToJsonString());
        arbitration!["recovery"]!.Text("state").ShouldBe("IN_ARBITRATION");
    }

    [Fact]
    public async Task E2E_02_Salvage_reserve_1500_sold_1700_recorded_from_BIL_cash_leaves_no_open_reserve()
    {
        var claim = await ClaimAsync();
        var recovery = await CreateAsync(claim, "SALVAGE", 1500m);
        var recoveryId = recovery.Text("recoveryId");
        (await _money.BuildAndSubmitAsync(claim, [RecoveryReserve(claim, recoveryId, 1500m)])).Text("status").ShouldBe("APPROVED");

        var (sold, soldBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/recoveries/record-milestone", new
        {
            recoveryId, expectedRecordVersion = 1, milestone = "SALVAGE_SOLD", occurredOn = "2026-10-19",
            salvage = new { buyerPartyId = Guid.NewGuid(), salePrice = Money(1700m) },
        });
        sold.StatusCode.ShouldBe(HttpStatusCode.OK, soldBody?.ToJsonString());
        soldBody!["recovery"]!.Text("state").ShouldBe("AGREED");

        var demand = await DemandAsync(soldBody["recovery"]!, 1700m);
        var receivableId = Guid.Parse(demand.Text("receivableId"));
        demand.Text("paymentReference").ShouldBe(FakeReceivables.Reference);
        _receivables.Registered.Count.ShouldBe(1);
        _receivables.Registered[0].Purpose.ShouldBe(ReceivablePurpose.Salvage);
        _receivables.Registered[0].RecoveryId.ShouldBe(Guid.Parse(recoveryId));

        var receipt = PaymentId.New();
        await CashAllocatedAsync(claim, recoveryId, receivableId, 1700m, receipt);
        await CashAllocatedAsync(claim, recoveryId, receivableId, 1700m, receipt); // replay: one set

        var totals = (await _money.FinancialsAsync(claim))["totals"]!;
        Amount(totals["recoveries"]).ShouldBe(1700m);
        Amount(totals["openRecoveryReserve"]).ShouldBe(0m);
        Amount(totals["netIncurred"]).ShouldBe(-1700m);
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.transaction_set WHERE claim_id='{claim.ClaimId}' AND evidence_kind='BIL_ALLOCATION'")).ShouldBe(1);
        var closed = await GetAsync(recoveryId);
        closed.Text("state").ShouldBe("CLOSED");
        Amount(closed["recoveredAmount"]).ShouldBe(1700m);
    }

    [Fact]
    public async Task REQ_CLM_151_Partial_cash_keeps_the_case_open_and_a_client_cannot_post_recovery_cash()
    {
        var claim = await ClaimAsync();
        var recovery = await CreateAsync(claim, "SUBROGATION", 200000m);
        var recoveryId = recovery.Text("recoveryId");
        var demand = await DemandAsync(recovery, 200000m);
        await CashAllocatedAsync(claim, recoveryId, Guid.Parse(demand.Text("receivableId")), 150000m);
        var current = await GetAsync(recoveryId);
        current.Text("state").ShouldBe("DEMANDED");
        Amount(current["recoveredAmount"]).ShouldBe(150000m);

        var (response, _) = await _money.BuildAsync(claim, [new
        {
            kind = "RECOVERY", recoveryId, exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = Money(50000m),
        }]);
        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task REQ_CLM_150_Write_off_within_authority_and_referred_above_it()
    {
        var claim = await ClaimAsync();
        var small = await CreateAsync(claim, "SUBROGATION", 4000m);
        (await _money.BuildAndSubmitAsync(claim, [RecoveryReserve(claim, small.Text("recoveryId"), 4000m)])).Text("status").ShouldBe("APPROVED");
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/recoveries/write-off", new
        {
            recoveryId = small.Text("recoveryId"), expectedRecordVersion = 1, amount = Money(4000m), reason = "UNCOLLECTABLE",
        }, SpecialistRole, "specialist-eleni");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("outcome").ShouldBe("WRITTEN_OFF");
        (await GetAsync(small.Text("recoveryId"))).Text("state").ShouldBe("WRITTEN_OFF");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openRecoveryReserve"]).ShouldBe(0m);

        var large = await CreateAsync(claim, "SUBROGATION", 8000m);
        (await _money.BuildAndSubmitAsync(claim, [RecoveryReserve(claim, large.Text("recoveryId"), 8000m)], Manager, ManagerUser)).Text("status").ShouldBe("APPROVED");
        var (referred, pending) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/recoveries/write-off", new
        {
            recoveryId = large.Text("recoveryId"), expectedRecordVersion = 1, amount = Money(8000m), reason = "UNCOLLECTABLE",
        }, SpecialistRole, "specialist-eleni");
        referred.StatusCode.ShouldBe(HttpStatusCode.OK, pending?.ToJsonString());
        pending.Text("outcome").ShouldBe("PENDING_APPROVAL");
        (await GetAsync(large.Text("recoveryId"))).Text("state").ShouldBe("OPEN");
        (await _money.DecideAsync(pending.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        (await GetAsync(large.Text("recoveryId"))).Text("state").ShouldBe("WRITTEN_OFF");
    }

    [Fact]
    public async Task REQ_CLM_145_Specified_allocation_must_sum_to_the_demand()
    {
        var claim = await ClaimAsync();
        var recovery = await CreateAsync(claim, "SUBROGATION", 1000m);
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/recoveries/demand", new
        {
            recoveryId = recovery.Text("recoveryId"), expectedRecordVersion = 1, amount = Money(1000m), dueDate = "2026-12-31", allocationRule = "SPECIFIED",
            allocation = new[] { new { exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = Money(900m) } },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldContain("ALLOCATION");
        _receivables.Registered.ShouldBeEmpty();
    }

    [Fact]
    public async Task D_SL4_17_Liability_facts_are_stored_audited_and_returned_on_the_claim()
    {
        var claim = await ClaimAsync();
        var version = await database.ScalarAsync<int>($"SELECT record_version FROM clm.claim WHERE claim_id = '{claim.ClaimId}'");
        var insurer = Guid.NewGuid();
        var (response, body) = await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/claims/{claim.ClaimId}", new
        {
            ids = new[] { claim.ClaimId }, expectedRecordVersion = version, reason = "LIABILITY_FACTS",
            changes = new { liabilityFacts = new { insuredFaultPct = "0", faultSource = "JOINT_REPORT", counterpartyInsurerPartyId = insurer, vehicleCount = 2, accidentInGreece = true } },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("liabilityFacts.faultSource").ShouldBe("JOINT_REPORT");
        var stored = await _money.ClaimAsync(claim);
        stored.Text("liabilityFacts.counterpartyInsurerPartyId").ShouldBe(insurer.ToString());
        stored["liabilityFacts"]!["vehicleCount"]!.GetValue<int>().ShouldBe(2);

        var (stale, _) = await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/claims/{claim.ClaimId}", new
        {
            ids = new[] { claim.ClaimId }, expectedRecordVersion = version, changes = new { liabilityFacts = new { insuredFaultPct = "100" } },
        });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (invalid, _) = await _money.SendAsync(HttpMethod.Patch, $"/api/clm/v1/claims/{claim.ClaimId}", new
        {
            ids = new[] { claim.ClaimId }, expectedRecordVersion = version + 1, changes = new { liabilityFacts = new { insuredFaultPct = "101" } },
        });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed class FakeReceivables : IBillingReceivableService
    {
        public static string Reference => "RF0000000000000001";

        public List<ReceivableRegisterRequest> Registered { get; } = [];

        public Task<ReceivableRegisterResponse> RegisterAsync(ReceivableRegisterRequest request, CommandOptions options, CancellationToken cancellationToken = default)
        {
            Registered.Add(request);
            return Task.FromResult(new ReceivableRegisterResponse { ReceivableId = Guid.NewGuid(), BillingAccountId = BillingAccountId.New(), PaymentReference = Reference });
        }

        public Task<ReceivableGetResponse> GetAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReceivableListPage> ListAsync(BillingAccountId? billingAccountId = null, PartyId? counterpartyPartyId = null, ClaimId? claimId = null, Guid? recoveryId = null,
            string? statementRef = null, string? paymentReference = null, ReceivableSourceType? sourceType = null, ReceivableStatus? status = null, string? cursor = null,
            int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
