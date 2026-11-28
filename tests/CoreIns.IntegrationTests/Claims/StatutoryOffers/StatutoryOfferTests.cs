using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.StatutoryOffers;

/// <summary>
/// BO-CLM-06: the statutory offer of a third-party MTPL exposure over HTTP on a real PostgreSQL: issue stops the offer clock and starts the payment
/// clock from the proven delivery, a delivery before the issue is refused, an accepted offer releases exactly one payment draft, repair in kind swaps
/// the payment clock for the repair clock, and Production refuses while the clock values are unverified (D-REG-04).
/// </summary>
public sealed class StatutoryOfferTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Platform:Time:Mode"] = "Shiftable" });
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Instant Now => ((ShiftableClock)_slice.Factory.Services.GetRequiredService<IClock>()).Now;

    private Task<(HttpResponseMessage Response, JsonNode? Body)> StepAsync(string step, object body, string roles = Handler, Guid? key = null) =>
        _money.SendAsync(HttpMethod.Post, $"/api/clm/v1/statutory-offers/{step}", body, roles, HandlerUser, key);

    private async Task<(MoneyClaim Claim, string ExposureId, string Claimant)> ThirdPartyAsync(ClaimsMoney? money = null, ClaimsSlice? slice = null)
    {
        money ??= _money;
        slice ??= _slice;
        var claim = await money.OpenClaimAsync(ClaimsSlice.Fnol(slice.Policy()));
        var version = int.Parse((await money.ClaimAsync(claim)).Text("summary.recordVersion"), CultureInfo.InvariantCulture);
        var claimant = Guid.CreateVersion7().ToString();
        var (response, body) = await money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures", new
        {
            claimId = claim.ClaimId, expectedRecordVersion = version, kind = "MTPL_BI", coverageCode = "MTPL", claimantPartyId = claimant,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (claim, body.Text("exposure.exposureId"), claimant);
    }

    private object Offer(string exposureId, decimal amount = 1500m, Instant? deliveredAt = null, Instant? issuedAt = null) => new
    {
        exposureId, kind = "REASONED_OFFER", amount = Money(amount), content = "Reasoned offer for the third-party damage.",
        deliveredAt = (deliveredAt ?? Now).ToString(), issuedAt = issuedAt?.ToString(), deliveryEvidenceId = Guid.CreateVersion7(), language = "en",
    };

    private async Task<JsonArray> ClocksAsync(MoneyClaim claim, string exposureId) =>
        (await _money.ClaimAsync(claim))["exposures"]!.AsArray().Single(e => e!.Text("exposureId") == exposureId)!["statutoryClocks"]!.AsArray();

    [Fact]
    public async Task Issue_stops_the_offer_clock_and_starts_the_payment_clock_from_the_delivery_once()
    {
        var (claim, exposureId, _) = await ThirdPartyAsync();
        var delivered = Now;
        var key = Guid.NewGuid();

        var request = Offer(exposureId, 1500m, delivered);
        var (response, body) = await StepAsync("issue", request, key: key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("offer.status").ShouldBe("ISSUED");
        body.Text("offer.clockLegalStatus").ShouldBe("UNVERIFIED");
        body.Text("offer.documentId").ShouldNotBeNullOrEmpty();

        var clocks = await ClocksAsync(claim, exposureId);
        clocks.Select(c => c!.Text("clockCode")).ShouldBe(["CLM_MTPL_OFFER", "CLM_MTPL_ASSESSMENT", "CLM_MTPL_PAYMENT"]);
        clocks.Single(c => c!.Text("clockCode") == "CLM_MTPL_OFFER")!.Text("status").ShouldBe("MET");
        var payment = clocks.Single(c => c!.Text("clockCode") == "CLM_MTPL_PAYMENT")!;
        payment.Text("status").ShouldBe("RUNNING");
        payment.Text("instanceId").ShouldBe(body.Text("offer.paymentClockId"));
        var athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
        var deadline = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(payment.Text("deadline"), CultureInfo.InvariantCulture), athens);
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(delivered.ToString(), CultureInfo.InvariantCulture), athens);
        DateOnly.FromDateTime(deadline.DateTime).ShouldBe(DateOnly.FromDateTime(start.DateTime).AddDays(10)); // 10 days from proven delivery (UNVERIFIED, OQ-008)

        // The same request replayed returns the same offer; a second offer on the exposure is refused. One event, one row.
        var (replay, replayBody) = await StepAsync("issue", request, key: key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, replayBody?.ToJsonString());
        (await StepAsync("issue", Offer(exposureId))).Body.Text("code").ShouldBe("CLM-ERR-OFFER-STATE");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.statutory_offer WHERE exposure_id = '{exposureId}'")).ShouldBe(1);
        await _money.DrainAsync();
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'StatutoryOfferIssued' AND aggregate_id = '{claim.ClaimId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_reply_stops_the_offer_clock_without_starting_payment_and_bad_input_is_refused()
    {
        var (claim, exposureId, _) = await ThirdPartyAsync();
        var reply = new { exposureId, kind = "REASONED_REPLY", content = "Liability is disputed.", deliveredAt = Now.ToString(), deliveryEvidenceId = Guid.CreateVersion7() };
        var (response, body) = await StepAsync("issue", reply);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("offer.kind").ShouldBe("REASONED_REPLY");
        (await ClocksAsync(claim, exposureId)).Select(c => c!.Text("clockCode")).ShouldBe(["CLM_MTPL_OFFER", "CLM_MTPL_ASSESSMENT"]);

        // A reply with an amount, an offer without one, a zero amount.
        (await StepAsync("issue", new { exposureId, kind = "REASONED_REPLY", amount = Money(10m), content = "x", deliveredAt = Now.ToString(), deliveryEvidenceId = Guid.CreateVersion7() }))
            .Body.Text("code").ShouldBe("CLM-ERR-OFFER-CONTENT");
        (await StepAsync("issue", new { exposureId, kind = "REASONED_OFFER", content = "x", deliveredAt = Now.ToString(), deliveryEvidenceId = Guid.CreateVersion7() }))
            .Body.Text("code").ShouldBe("CLM-ERR-OFFER-CONTENT");
        (await StepAsync("issue", Offer(exposureId, 0m))).Body.Text("code").ShouldBe("CLM-ERR-OFFER-CONTENT");

        // The own-damage exposure of the insured is not a statutory-clock case; an unknown exposure is not found; no permission is 403.
        (await StepAsync("issue", Offer(claim.ExposureId))).Body.Text("code").ShouldBe("CLM-ERR-OFFER-STATE");
        (await StepAsync("issue", Offer(Guid.NewGuid().ToString()))).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StepAsync("issue", Offer(exposureId), "Staff.Nobody")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_delivery_earlier_than_the_issue_or_in_the_future_is_refused_and_leaves_the_clocks_alone()
    {
        var (claim, exposureId, _) = await ThirdPartyAsync();
        var now = Now;

        var (early, earlyBody) = await StepAsync("issue", Offer(exposureId, 1500m, deliveredAt: now.Minus(TimeSpan.FromHours(3)), issuedAt: now.Minus(TimeSpan.FromHours(1))));
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, earlyBody?.ToJsonString());
        earlyBody.Text("code").ShouldBe("CLM-ERR-OFFER-DELIVERY");

        var (future, futureBody) = await StepAsync("issue", Offer(exposureId, 1500m, deliveredAt: now.Plus(TimeSpan.FromDays(1))));
        future.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, futureBody?.ToJsonString());
        futureBody.Text("code").ShouldBe("CLM-ERR-OFFER-DELIVERY");

        (await ClocksAsync(claim, exposureId)).ShouldAllBe(c => c!.Text("status") == "RUNNING");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.statutory_offer WHERE exposure_id = '{exposureId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task An_accepted_offer_creates_exactly_one_payment_draft()
    {
        var (claim, exposureId, claimant) = await ThirdPartyAsync();
        var (_, issued) = await StepAsync("issue", Offer(exposureId, 1500m));
        var offerId = issued.Text("offer.offerId");
        var (accountId, _) = await _money.CaptureAsync(claim, claimant);

        // Not accepted yet: no payment. Accepting before the delivery is refused.
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim_payment WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(0);
        var (tooEarly, tooEarlyBody) = await StepAsync("record-acceptance", new { offerId, acceptedAt = Now.Minus(TimeSpan.FromDays(1)).ToString(), payeePartyId = claimant, payeeAccountId = accountId });
        tooEarly.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, tooEarlyBody?.ToJsonString());
        tooEarlyBody.Text("code").ShouldBe("CLM-ERR-OFFER-DELIVERY");

        var (response, body) = await StepAsync("record-acceptance", new { offerId, payeePartyId = claimant, payeeAccountId = accountId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("offer.status").ShouldBe("ACCEPTED");
        var setId = body.Text("offer.paymentSetId");
        (await _money.SetAsync(setId)).Text("status").ShouldBe("DRAFT");

        // A repeat returns the same set; there is exactly one draft payment of the offered amount.
        var (again, againBody) = await StepAsync("record-acceptance", new { offerId, payeePartyId = claimant, payeeAccountId = accountId });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("offer.paymentSetId").ShouldBe(setId);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.transaction_set WHERE claim_id = '{claim.ClaimId}' AND status = 'DRAFT'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim_payment WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(1);
        (await _money.ScalarAsync<decimal>($"SELECT amount FROM clm.claim_payment WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(1500m);

        // An accepted offer takes no repair agreement; an unknown offer is not found.
        (await StepAsync("record-repair-agreement", new { offerId })).Body.Text("code").ShouldBe("CLM-ERR-OFFER-STATE");
        (await StepAsync("record-acceptance", new { offerId = Guid.NewGuid(), payeePartyId = claimant, payeeAccountId = accountId })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_payee_account_that_is_not_captured_refuses_the_acceptance_and_nothing_changes()
    {
        var (claim, exposureId, claimant) = await ThirdPartyAsync();
        var offerId = (await StepAsync("issue", Offer(exposureId))).Body.Text("offer.offerId");

        var (response, body) = await StepAsync("record-acceptance", new { offerId, payeePartyId = claimant, payeeAccountId = Guid.NewGuid() });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        (await _money.ScalarAsync<string>($"SELECT status FROM clm.statutory_offer WHERE offer_id = '{offerId}'")).ShouldBe("ISSUED");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim_payment WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Repair_in_kind_cancels_the_payment_clock_and_starts_the_repair_clock()
    {
        var (claim, exposureId, _) = await ThirdPartyAsync();
        var offerId = (await StepAsync("issue", Offer(exposureId))).Body.Text("offer.offerId");

        var (response, body) = await StepAsync("record-repair-agreement", new { offerId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("offer.status").ShouldBe("REPAIR_AGREED");
        var clocks = await ClocksAsync(claim, exposureId);
        clocks.Single(c => c!.Text("clockCode") == "CLM_MTPL_PAYMENT")!.Text("status").ShouldBe("CANCELLED");
        var repair = clocks.Single(c => c!.Text("clockCode") == "CLM_MTPL_REPAIR")!;
        repair.Text("status").ShouldBe("RUNNING");
        repair.Text("instanceId").ShouldBe(body.Text("offer.repairClockId"));

        // Repeat is idempotent; accepting for payment afterwards is refused.
        (await StepAsync("record-repair-agreement", new { offerId })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StepAsync("record-acceptance", new { offerId, payeePartyId = Guid.NewGuid(), payeeAccountId = Guid.NewGuid() })).Body.Text("code").ShouldBe("CLM-ERR-OFFER-STATE");
        (await ClocksAsync(claim, exposureId)).Count(c => c!.Text("clockCode") == "CLM_MTPL_REPAIR").ShouldBe(1);
    }

    [Fact]
    public async Task Production_refuses_an_offer_while_the_clock_values_are_unverified()
    {
        await using var production = new ClaimsSlice(database.AppConnectionString, services =>
            services.AddSingleton<IHostEnvironment>(sp => new ProductionEnvironment("Production")));
        var money = new ClaimsMoney(production.Factory, production.Client, database.SuperuserConnectionString);

        // Production refuses to open a clocked exposure while the values are unverified (BO-CLM-05), so the exposure is opened through the
        // non-Production slice on the same database; the Production slice then refuses the offer.
        var (claim, exposureId, _) = await ThirdPartyAsync();
        var (refused, body) = await money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/statutory-offers/issue",
            new
            {
                exposureId, kind = "REASONED_OFFER", amount = Money(900m), content = "Offer.", deliveredAt = Now.ToString(), deliveryEvidenceId = Guid.CreateVersion7(),
            },
            Handler, HandlerUser);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CLM-ERR-MTPL-CLOCKS-REQUIRED");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.statutory_offer WHERE exposure_id = '{exposureId}'")).ShouldBe(0);
        (await ClocksAsync(claim, exposureId)).ShouldAllBe(c => c!.Text("status") == "RUNNING");
    }

    private sealed class ProductionEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
