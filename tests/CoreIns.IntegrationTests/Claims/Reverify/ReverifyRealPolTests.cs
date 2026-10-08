using System.Net;
using CoreIns.IntegrationTests.Finance;
using CoreIns.IntegrationTests.Policy;
using CoreIns.IntegrationTests.Policy.Temporal;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Reverify;

/// <summary>
/// SL3-CLM-REVERIFY against the REAL POL (merged SL3-POL-TEMPORAL): POL itself computes <c>supersession</c> from its record
/// history. A real bound policy and a real FNOL; then POL records a content change under its write protocol (lock, stamp, re-cut
/// segment, as a servicing command will), so the claim's stored ref reports itself superseded with POL's own successor ref.
/// The change event is published synthetically (its producer, SL3-POL-CHANGE, is not merged yet): everything CLM does with it
/// — the supersession read by stored ref, the demand, and the adoption against POL's successor — runs for real.
/// </summary>
public sealed class ReverifyRealPolTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _policy = null!;
    private ClaimsMoney _money = null!;

    public async ValueTask InitializeAsync()
    {
        _policy = new PolicySlice(
            database.AppConnectionString, realRatingAndUnderwriting: true, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        await _policy.SeedAsync();
        _money = new ClaimsMoney(_policy.Factory, _policy.Client, database.SuperuserConnectionString);
    }

    public async ValueTask DisposeAsync() => await _policy.DisposeAsync();

    [Fact]
    public async Task REQ_CLM_057_058_POL_supersession_on_a_real_policy_raises_the_demand_and_adoption_follows_POLs_successor_ref()
    {
        // A real bound policy; the clock moves ten days into the term; a real FNOL with an own-damage exposure.
        var party = await _policy.CreatePartyAsync();
        var (jobId, _, _) = await _policy.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));
        (await _policy.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (bound, bind) = await _policy.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var clock = (ShiftableClock)_policy.Factory.Services.GetRequiredService<IClock>();
        clock.Advance(TimeSpan.FromDays(10));
        await _money.DrainAsync();

        var policyId = Guid.Parse(bind.Text("policyId"));
        var termId = Guid.Parse(bind.Text("termId"));
        var scripted = new ScriptedPolicy(policyId, bind.Text("policyNumber"), Guid.Parse(party), "MOTOR-GR", clock.Now, clock.Now, []);
        var (reported, fnol) = await _money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/fnol/submit", ClaimsSlice.Fnol(scripted, lossAt: clock.Now.Plus(TimeSpan.FromDays(-1)), exposure: false));
        reported.StatusCode.ShouldBe(HttpStatusCode.OK, fnol?.ToJsonString());
        var (created, exposure) = await _money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/exposures", new { claimId = fnol.Text("claimId"), expectedRecordVersion = 1, kind = "OWN_DAMAGE", coverageCode = "OWN-DAMAGE" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, exposure?.ToJsonString());
        exposure.Text("exposure.coverageIndication").ShouldBe("COVERED");
        var claim = new MoneyClaim(fnol.Text("claimId"), fnol.Text("claimNumber"), exposure.Text("exposure.exposureId"), party);
        var oldRef = (await _money.ClaimAsync(claim)).Text("summary.snapshotRef");

        // Before POL records anything, an event about the policy raises nothing: POL reports the ref current.
        var lossDate = BusinessDate.Parse((await _money.ClaimAsync(claim)).Text("summary.lossDate"));
        await PublishChangedAsync(policyId, lossDate.AddDays(-1));
        await _money.DrainAsync();
        (await RaisedAsync(claim)).ShouldBeEmpty();

        // POL records a content change (the cover is dropped) under its write protocol.
        await using (var scope = _policy.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PolicyDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ClaimsSlice.Ct);
            var locked = await PolicyWriteLock.AcquireAsync(
                db, clock, TimeSpan.FromSeconds(10), new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId)), new PolicyId(policyId), ClaimsSlice.Ct);
            locked.IsSuccess.ShouldBeTrue();
            await TemporalTestBase.RecutSegmentAsync(db, termId, locked.Value.ToDateTimeOffset().UtcDateTime, split: false, changeContent: true, sequence: 2);
            await transaction.CommitAsync(ClaimsSlice.Ct);
        }

        var cause = await PublishChangedAsync(policyId, lossDate.AddDays(-1));
        await _money.DrainAsync();

        var raised = await RaisedAsync(claim);
        raised.Count.ShouldBe(1);
        var newRef = raised[0]["newSnapshotRef"]!.GetValue<string>();
        newRef.ShouldNotBe(oldRef);
        raised[0]["oldSnapshotRef"]!.GetValue<string>().ShouldBe(oldRef);
        raised[0]["causeEventId"]!.GetValue<string>().ShouldBe(cause.EventId.Value.ToString());

        // REQ-CLM-002: the claim is flagged but not changed.
        var flagged = await _money.ClaimAsync(claim);
        flagged.Text("summary.snapshotStatus").ShouldBe("REVERIFICATION_REQUIRED");
        flagged.Text("summary.snapshotRef").ShouldBe(oldRef);
        flagged.Text("exposures.0.coverageIndication").ShouldBe("COVERED");
        flagged.Text("pendingReverification.newSnapshotRef").ShouldBe(newRef);

        // A stale expectation is refused; adopting POL's own successor ref re-runs the cover check against POL's new content.
        var (stale, staleBody) = await ReverifyAsync(claim, oldRef);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("CLM-ERR-SNAPSHOT-MISMATCH");

        var (adopted, adoptBody) = await ReverifyAsync(claim, newRef);
        adopted.StatusCode.ShouldBe(HttpStatusCode.OK, adoptBody?.ToJsonString());
        adoptBody.Text("snapshotRef").ShouldBe(newRef);
        adoptBody.Text("previousSnapshotRef").ShouldBe(oldRef);
        adoptBody.Text("coverageInQuestion").ShouldBe("true");

        var after = await _money.ClaimAsync(claim);
        after.Text("summary.snapshotRef").ShouldBe(newRef);
        after.Text("summary.snapshotStatus").ShouldBe("VERIFIED");
        after.Text("summary.coverageInQuestion").ShouldBe("true");
        after.Text("exposures.0.coverageIndication").ShouldBe("IN_QUESTION");
        var (payment, paymentBody) = await _money.BuildAsync(claim, [Payment(claim, 100m, Guid.NewGuid().ToString())]);
        payment.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, paymentBody?.ToJsonString());
        paymentBody.Text("code").ShouldBe("CLM-ERR-COVERAGE-IN-QUESTION");
    }

    private Task<EventEnvelope> PublishChangedAsync(Guid policyId, BusinessDate effective)
    {
        var payload = FinanceSlice.Sample("pol", "PolicyChanged");
        payload["effectiveDate"] = effective.ToString();
        return PolicyEventPublisher.PublishAsync(
            _policy.Factory.Services, PolicyChangedV1.Descriptor, policyId, payload,
            BusinessKeys.Empty.With("policyId", policyId.ToString()).With("transactionId", payload["transactionId"]!.GetValue<string>()));
    }

    private async Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> RaisedAsync(MoneyClaim claim)
    {
        var json = await _money.ScalarAsync<string?>(
            $"SELECT json_agg(payload ORDER BY aggregate_sequence)::text FROM plt.outbox_message WHERE event_type = 'ReverificationRequired' AND aggregate_id = '{claim.ClaimId}'");
        return json is null ? [] : [.. System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray().Select(n => n!.AsObject())];
    }

    private Task<(HttpResponseMessage Response, System.Text.Json.Nodes.JsonNode? Body)> ReverifyAsync(MoneyClaim claim, string expectedNewRef) =>
        _money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/coverage/reverify",
            new { claimId = claim.ClaimId, decision = "ADOPT", reasonCode = "CHANGE_APPLIES", expectedNewSnapshotRef = expectedNewRef });
}
