using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Change;

/// <summary>
/// SL3-POL-CHANGE on PostgreSQL 17: an in-sequence mid-term change from start to bind, with RAT scripted (rates move with the
/// vehicle's engine capacity and value), MKT rounding, PFC and PTY real, a manual clock and the scripted tax port. The names carry
/// the requirement ids (REQ → test traceability); every amount is ILLUSTRATIVE TEST DATA.
/// </summary>
public sealed class PolicyChangeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ChangeHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = new ChangeHarness(database);
        await _h.InitializeAsync();
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static JsonNode Line(JsonNode? body, string array, string chargeType) =>
        body![array]!.AsArray().Single(l => l!["chargeType"]!.GetValue<string>() == chargeType)!;

    [Fact]
    public async Task REQ_POL_190_192_193_005_119_122_129_197_change_at_day_200_preview_equals_bind_and_deltas_sum_to_written()
    {
        var policy = await _h.IssueAsync();
        var issuancePremium = await _h.ScalarAsync<decimal>($"SELECT premium FROM pol.policy_transaction WHERE transaction_id = '{policy.IssuanceTransactionId}'");
        issuancePremium.ShouldBe(ChangeHarness.MtplRate(1400) + ChangeHarness.OwnDamageRate(15000m));
        var at = _h.SetDay(policy, 200);
        var days = ChangeHarness.Days(at.ToUtcDateTime(), policy.End);

        // REQ-POL-190: start a change (effective date defaults to now); the job is pinned to the term and its head.
        var jobId = await _h.NewChangeAsync(policy);
        (await _h.ScalarAsync<string>($"SELECT job_type || '/' || state FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("POLICY_CHANGE/DRAFT");
        (await _h.ScalarAsync<string>($"SELECT base_transaction_id::text FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe(policy.IssuanceTransactionId);
        (await _h.ScalarAsync<string>($"SELECT target_term_id::text FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe(policy.TermId);
        (await _h.ScalarAsync<string>($"SELECT rating_artefact_hash FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe(PolicySlice.RatingArtefactHash);

        // REQ-POL-191: the edit (engine capacity 1400 → 1600).
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);

        // REQ-POL-192: the dry-run quote prices the change and leaves nothing behind.
        var (dryQuoted, dryQuote) = await _h.QuoteAsync(jobId, dryRun: true);
        dryQuoted.StatusCode.ShouldBe(HttpStatusCode.OK, dryQuote?.ToJsonString());
        (await _h.ScalarAsync<string>($"SELECT state FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("DRAFT");
        (await _h.ScalarAsync<string>($"SELECT state FROM pol.quote_version WHERE job_id = '{jobId}' AND version_no = 1")).ShouldBe("DRAFT");

        var (quoted, quote) = await _h.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("state").ShouldBe("QUOTED");
        var mtplDelta = ChangeHarness.Prorated(ChangeHarness.MtplRate(1600) - ChangeHarness.MtplRate(1400), days);
        var tax = decimal.Round(mtplDelta * 0.15m, 2, MidpointRounding.AwayFromZero);
        quote.Text("premium.amount").ShouldBe(mtplDelta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        quote.Text("taxes.amount").ShouldBe(tax.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Line(quote, "charges", "PREM-MTPL").Text("annualRate").ShouldBe(ChangeHarness.MtplRate(1600).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Line(quote, "charges", "PREM-OD").Text("amount.amount").ShouldBe("0");
        quote!["charges"]!.AsArray().Count.ShouldBe(3);

        // RAT: ENDORSEMENT mode under the term's pinned rating artefact (REQ-POL-093).
        var rated = (Modules.Rating.Contracts.Api.RateRateRequest)_h.Slice.Rating.CallsTo("rat.Rate.rate").Last().Arguments[0]!;
        rated.Envelope.Mode.ShouldBe(Modules.Rating.Contracts.Api.RateRateRequest.EnvelopeDetail.ModeValue.Endorsement);
        rated.Envelope.RatingArtefactHash!.Value.Value.ShouldBe(PolicySlice.RatingArtefactHash);

        // REQ-POL-193: the diff, grouped by section, and the servicing preview (before/after annual, prorated change, tax treatment).
        var (previewed, preview) = await _h.PreviewAsync(jobId);
        previewed.StatusCode.ShouldBe(HttpStatusCode.OK, preview?.ToJsonString());
        preview.Text("sections.0.section").ShouldBe("vehicle");
        preview.Text("diff.0.field").ShouldBe("engineCapacityCc");
        preview.Text("diff.0.before").ShouldBe("1400");
        preview.Text("diff.0.after").ShouldBe("1600");
        preview.Text("servicingPreview.totalChange.amount").ShouldBe((mtplDelta + tax).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var previewMtpl = preview!["servicingPreview"]!["lines"]!.AsArray().Single(l => l!["chargeType"]!.GetValue<string>() == "PREM-MTPL")!;
        previewMtpl.Text("beforeAnnual").ShouldBe(ChangeHarness.MtplRate(1400).ToString(System.Globalization.CultureInfo.InvariantCulture));
        previewMtpl.Text("afterAnnual").ShouldBe(ChangeHarness.MtplRate(1600).ToString(System.Globalization.CultureInfo.InvariantCulture));
        previewMtpl.Text("days").ShouldBe(days.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // REQ-POL-129: a dry-run bind returns the deltas and writes nothing.
        var transactionsBefore = await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'");
        var linesBefore = await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}'");
        var watermarkBefore = await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        var (dryBound, dryBind) = await _h.BindAsync(jobId, dryRun: true);
        dryBound.StatusCode.ShouldBe(HttpStatusCode.OK, dryBind?.ToJsonString());
        dryBind["chargeDeltas"]!.AsArray().Count.ShouldBe(2);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(transactionsBefore);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(linesBefore);
        (await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(watermarkBefore);
        (await _h.ScalarAsync<string>($"SELECT state FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("QUOTED");
        (await _h.ScalarAsync<long>("SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyChanged'")).ShouldBe(0);

        // REQ-POL-005, -119, -122: the bind. Preview = bind amounts; one delta per element × charge type.
        var (bound, bind) = await _h.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND");
        bind["chargeDeltas"]!.AsArray().Count.ShouldBe(2);
        Line(bind, "chargeDeltas", "PREM-MTPL").Text("amount.amount").ShouldBe(quote.Text("premium.amount"));
        Line(bind, "chargeDeltas", "GR-IPT").Text("amount.amount").ShouldBe(quote.Text("taxes.amount"));
        var transactionId = bind.Text("transactionId");
        (await _h.ScalarAsync<string>($"SELECT kind FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe("CHANGE");
        (await _h.ScalarAsync<int>($"SELECT sequence FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(2);
        (await _h.ScalarAsync<decimal>($"SELECT premium FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(mtplDelta);
        (await _h.ScalarAsync<string>($"SELECT head_transaction_id::text FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL")).ShouldBe(transactionId);

        // Σ deltas per term = cumulative written (P7): the issuance premium plus the change.
        (await _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}' AND charge_category = 'PREMIUM'")).ShouldBe(issuancePremium + mtplDelta);

        // Charge lines: transaction kind, treatment rule id/version, legal status and the provisional flag are always set.
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND transaction_kind = 'ENDORSEMENT_DEBIT'")).ShouldBe(2);
        (await _h.ScalarAsync<string>(
            $"SELECT tax_treatment_ref || '/' || treatment_rule_id || '/' || treatment_rule_version || '/' || legal_status || '/' || provisional FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_category = 'TAX'"))
            .ShouldBe("APPLY/TEST-IPT-ENDORSE-DEBIT/1/Unverified/true");

        // Everything is stamped with the one record time t = the policy watermark; the old versions closed at exactly t.
        var watermark = await _h.ScalarAsync<DateTime>($"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        (await _h.ScalarAsync<DateTime>($"SELECT recorded_at FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(watermark);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND recorded_at <> '{ChangeHarness.Iso(watermark)}'")).ShouldBe(0);
        (await _h.ScalarAsync<DateTime>($"SELECT recorded_from FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL")).ShouldBe(watermark);
        (await _h.ScalarAsync<DateTime>($"SELECT recorded_to FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NOT NULL")).ShouldBe(watermark);
        watermark.ShouldBeGreaterThanOrEqualTo(at.ToUtcDateTime());

        // The old segment is kept in history and readable at the old knownAt; the current ones are the elapsed part and the new risk.
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NOT NULL")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL")).ShouldBe(2);
        (await _h.ScalarAsync<int>(
            $"SELECT (snapshot->'vehicles'->0->>'engineCapacityCc')::int FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND valid_to = '{ChangeHarness.Iso(policy.End)}'"))
            .ShouldBe(1600);
        (await _h.ScalarAsync<int>(
            $"SELECT (snapshot->'vehicles'->0->>'engineCapacityCc')::int FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND valid_from = '{ChangeHarness.Iso(policy.Start)}'"))
            .ShouldBe(1400);

        // Events: ChargeDeltaEmitted (one complete set) and PolicyChanged, no plate or personal data.
        (await _h.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transactionId}' AND set_size = 2 AND business_keys->>'transactionId' = '{transactionId}'"))
            .ShouldBe(2);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyChanged' AND business_keys->>'transactionId' = '{transactionId}'")).ShouldBe(1);
        var changed = await _h.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'PolicyChanged' AND business_keys->>'transactionId' = '{transactionId}'");
        var payload = JsonNode.Parse(changed)!;
        payload.Text("kind").ShouldBe("CHANGE");
        payload.Text("changedElements.0.locator").ShouldBe(policy.Locator);
        payload.Text("effectiveDate").ShouldBe(TimeZoneInfo.ConvertTimeFromUtc(at.ToUtcDateTime(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens")).ToString("yyyy-MM-dd"));
        payload["vehicleCoverFacts"]!["added"]!.AsArray().ShouldBeEmpty();
        payload["vehicleCoverFacts"]!["removed"]!.AsArray().ShouldBeEmpty();
        var allEvents = await _h.ScalarAsync<string>(
            $"SELECT string_agg(payload::text, ' ') FROM plt.outbox_message WHERE aggregate_id = '{policy.PolicyId}' AND business_keys->>'transactionId' = '{transactionId}'");
        allEvents.ShouldNotContain("ikx-1234");
        allEvents.ShouldNotContain("Παπαδοπούλου");

        // The job is Bound; the same bind again is not a second transaction.
        (await _h.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(2);
    }

    [Fact]
    public async Task REQ_POL_007_078_080_snapshot_at_an_earlier_loss_date_is_not_superseded_a_later_one_is()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 200);
        var early = Instant.FromUtcDateTime(DateTime.SpecifyKind(policy.Start.AddDays(100), DateTimeKind.Utc));
        var late = Instant.FromUtcDateTime(DateTime.SpecifyKind(policy.Start.AddDays(250), DateTimeKind.Utc));
        var (earlyRef, lateRef) = (await RefAsync(policy, early), await RefAsync(policy, late));
        var oldContent = await ContentAsync(lateRef);

        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);
        (await _h.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.BindAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The reference keeps answering with the same bytes; only the live supersession metadata moves.
        (await ContentAsync(lateRef)).ShouldBe(oldContent);
        (await SupersessionAsync(earlyRef)).Superseded.ShouldBeFalse();
        var superseded = await SupersessionAsync(lateRef);
        superseded.Superseded.ShouldBeTrue();
        superseded.SuccessorRef.ShouldNotBeNull();
        (await ContentAsync(superseded.SuccessorRef!)).ShouldContain("1600");
    }

    [Fact]
    public async Task REQ_POL_093_115_119_123_debit_applies_ipt_and_a_credit_keeps_it_not_reduced_provisional()
    {
        var policy = await _h.IssueAsync();
        var issuancePremium = await _h.ScalarAsync<decimal>($"SELECT premium FROM pol.policy_transaction WHERE transaction_id = '{policy.IssuanceTransactionId}'");

        // Day 100: debit (1400 → 1600).
        var first = _h.SetDay(policy, 100);
        var firstJob = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(firstJob, policy, capacity: 1600);
        (await _h.QuoteAsync(firstJob)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (firstBound, firstBind) = await _h.BindAsync(firstJob);
        firstBound.StatusCode.ShouldBe(HttpStatusCode.OK, firstBind?.ToJsonString());
        var debit = ChangeHarness.Prorated(ChangeHarness.MtplRate(1600) - ChangeHarness.MtplRate(1400), ChangeHarness.Days(first.ToUtcDateTime(), policy.End));

        // Day 150: credit (1600 → 1300), in sequence after the first change; the engine builds on the replayed history.
        var second = _h.SetDay(policy, 150);
        var secondJob = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(secondJob, policy, capacity: 1300);
        var (quoted, quote) = await _h.QuoteAsync(secondJob);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await _h.BindAsync(secondJob);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var days = ChangeHarness.Days(second.ToUtcDateTime(), policy.End);
        var credit = ChangeHarness.Prorated(ChangeHarness.MtplRate(1300) - ChangeHarness.MtplRate(1600), days);
        credit.ShouldBeLessThan(0m);
        var transactionId = bind.Text("transactionId");

        // The credit is the difference over the remaining days; the IPT line is 0.00, KEEP_NOT_REDUCED, provisional (D-SL3-05).
        (await _h.ScalarAsync<decimal>($"SELECT amount FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'PREM-MTPL'")).ShouldBe(credit);
        (await _h.ScalarAsync<decimal>($"SELECT amount FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'GR-IPT'")).ShouldBe(0m);
        (await _h.ScalarAsync<string>(
            $"SELECT transaction_kind || '/' || tax_treatment_ref || '/' || treatment_rule_id || '/' || legal_status || '/' || provisional FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'GR-IPT'"))
            .ShouldBe("ENDORSEMENT_CREDIT/KEEP_NOT_REDUCED/TEST-IPT-ENDORSE-CREDIT/PendingOpinion/true");
        (await _h.ScalarAsync<string>($"SELECT transaction_kind FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'PREM-MTPL'")).ShouldBe("ENDORSEMENT_CREDIT");
        Line(bind, "chargeDeltas", "GR-IPT").Text("provisional").ShouldBe("true");

        // Σ deltas per term = cumulative written across three transactions.
        (await _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}' AND charge_category = 'PREMIUM'")).ShouldBe(issuancePremium + debit + credit);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL")).ShouldBe(3);

        // The cumulative result is what a replay of the history gives: a third change that restores 1400 credits exactly the unearned remainder.
        var third = _h.SetDay(policy, 170);
        var thirdJob = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(thirdJob, policy, capacity: 1400);
        (await _h.QuoteAsync(thirdJob)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.BindAsync(thirdJob)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}' AND kind = 'CHANGE'")).ShouldBe(3);
        third.ShouldBeGreaterThan(second);
    }

    [Fact]
    public async Task REQ_POL_195_197_replacing_the_vehicle_is_one_change_with_vehicle_facts()
    {
        var policy = await _h.IssueAsync();
        var at = _h.SetDay(policy, 120);
        var days = ChangeHarness.Days(at.ToUtcDateTime(), policy.End);
        var jobId = await _h.NewChangeAsync(policy);
        var (edited, draft) = await _h.EditAsync(jobId,
        [
            new { op = "SET_VEHICLE", vehicle = new { plate = "yzx-9876", make = "Honda", model = "Civic", firstRegistrationYear = 2023, engineCapacityCc = 1800, use = "PRIVATE", value = new { amount = "20000.00", currency = "EUR" } } },
            new { op = "REMOVE_VEHICLE", locator = policy.Locator },
        ]);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        var newLocator = draft.Text("riskTree.vehicles.0.locator");
        newLocator.ShouldNotBe(policy.Locator);
        var driverLocator = draft.Text("riskTree.drivers.0.locator");
        var driverPartyId = policy.PartyId;
        var (reassigned, reassign) = await _h.EditAsync(jobId,
        [
            new { op = "SET_DRIVER", driver = new { locator = driverLocator, partyId = driverPartyId, driverType = "MAIN", yearFirstLicensed = 2010, vehicleLocator = newLocator, usagePercent = 100, claimsLast5Years = 0 } },
            new
            {
                op = "SET_COVERAGES",
                coverages = new object[]
                {
                    new { coverageCode = "MTPL", elementLocator = newLocator, selected = true },
                    new { coverageCode = "OWN-DAMAGE", elementLocator = newLocator, selected = true },
                },
            },
        ], draftVersion: 1);
        reassigned.StatusCode.ShouldBe(HttpStatusCode.OK, reassign?.ToJsonString());

        var (quoted, quote) = await _h.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await _h.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var transactionId = bind.Text("transactionId");

        // The old vehicle's elements are credited (rate 0 from the effective date), the new vehicle's are debited, in one transaction.
        var oldMtpl = ChangeHarness.MtplRate(1400);
        var newMtpl = ChangeHarness.MtplRate(1800);
        (await _h.ScalarAsync<decimal>($"SELECT amount FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'PREM-MTPL' AND element_locator = '{policy.Locator}'"))
            .ShouldBe(-ChangeHarness.Prorated(oldMtpl, days));
        (await _h.ScalarAsync<decimal>($"SELECT amount FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'PREM-MTPL' AND element_locator = '{newLocator}'"))
            .ShouldBe(ChangeHarness.Prorated(newMtpl, days));
        (await _h.ScalarAsync<string>($"SELECT transaction_kind FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'PREM-OD' AND element_locator = '{policy.Locator}'"))
            .ShouldBe("ENDORSEMENT_CREDIT");
        (await _h.ScalarAsync<string>($"SELECT transaction_kind FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND charge_type = 'PREM-OD' AND element_locator = '{newLocator}'"))
            .ShouldBe("ENDORSEMENT_DEBIT");
        (await _h.ScalarAsync<string>($"SELECT snapshot->'vehicles'->0->>'locator' FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND valid_to = '{ChangeHarness.Iso(policy.End)}'"))
            .ShouldBe(newLocator);

        var payload = JsonNode.Parse(await _h.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'PolicyChanged' AND business_keys->>'transactionId' = '{transactionId}'"))!;
        payload["vehicleCoverFacts"]!["added"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe([newLocator]);
        payload["vehicleCoverFacts"]!["removed"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe([policy.Locator]);
        payload.ToJsonString().ShouldNotContain("yzx-9876");
        payload.ToJsonString().ShouldNotContain("ikx-1234");
    }

    [Fact]
    public async Task REQ_POL_202_a_no_premium_change_emits_policy_changed_and_zero_deltas()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 90);
        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, use: "BUSINESS");
        var (quoted, quote) = await _h.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        quote.Text("total.amount").ShouldBe("0");
        var (bound, bind) = await _h.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var transactionId = bind.Text("transactionId");
        bind["chargeDeltas"]!.AsArray().ShouldBeEmpty();
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transactionId}'")).ShouldBe(0);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND business_keys->>'transactionId' = '{transactionId}'")).ShouldBe(0);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyChanged' AND business_keys->>'transactionId' = '{transactionId}'")).ShouldBe(1);
        (await _h.ScalarAsync<decimal>($"SELECT total FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(0m);
        (await _h.ScalarAsync<string>($"SELECT snapshot->'vehicles'->0->>'use' FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND valid_to = '{ChangeHarness.Iso(policy.End)}'"))
            .ShouldBe("BUSINESS");
    }

    [Fact]
    public async Task REQ_POL_008_135_136_effective_date_limits_csr_zero_days_underwriter_thirty()
    {
        var policy = await _h.IssueAsync();
        var now = _h.SetDay(policy, 60);

        // A CSR may not backdate at all: yesterday is refused, with the permitted range in the problem.
        var yesterday = ChangeHarness.Iso(now.ToUtcDateTime().AddDays(-1));
        var (csrRefused, csrBody) = await _h.StartChangeAsync(policy, yesterday, ChangeHarness.Csr);
        csrRefused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, csrBody?.ToJsonString());
        csrBody.Text("code").ShouldBe("POL-ERR-EFFDATE-LIMIT");
        csrBody.Text("earliest").ShouldNotBe("null");
        csrBody.Text("latest").ShouldNotBe("null");
        (await _h.StartChangeAsync(policy, null, ChangeHarness.Csr, dryRun: true)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The underwriter may go back 30 days, not 31.
        var tooFar = ChangeHarness.Iso(now.ToUtcDateTime().AddDays(-31));
        (await _h.StartChangeAsync(policy, tooFar)).Body.Text("code").ShouldBe("POL-ERR-EFFDATE-LIMIT");
        (await _h.StartChangeAsync(policy, ChangeHarness.Iso(now.ToUtcDateTime().AddDays(-29)), dryRun: true)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Not beyond the end of the term.
        var after = ChangeHarness.Iso(policy.End.AddDays(1));
        (await _h.StartChangeAsync(policy, after)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        // Nothing was created by the dry runs and refusals.
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'POLICY_CHANGE'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_POL_104_G1_nothing_at_or_after_a_bound_cancellation()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 40);
        var open = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(open, policy, capacity: 1600);
        (await _h.QuoteAsync(open)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A cancellation is bound meanwhile (written the way POL-CANCEL's command does).
        await _h.NewTermVersionAsync(policy, "CANCELLED", cancelledAtSql: "now()", headSql: $"'{Guid.CreateVersion7()}'::uuid");

        var (bindRefused, bind) = await _h.BindAsync(open);
        bindRefused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, bind?.ToJsonString());
        bind.Text("code").ShouldBe("POL-ERR-AFTER-CANCELLATION");
        var (startRefused, start) = await _h.StartChangeAsync(policy);
        startRefused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, start?.ToJsonString());
        start.Text("code").ShouldBe("POL-ERR-AFTER-CANCELLATION");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_POL_072_190_a_term_that_is_not_in_force_or_scheduled_cannot_be_changed_and_one_open_change_per_term()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 10);
        var first = await _h.NewChangeAsync(policy);

        // D-SL3-11: a second open change on the term is a conflict, not a server error.
        var (second, body) = await _h.StartChangeAsync(policy);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("POL-ERR-JOB-CONFLICT");

        // Once the first is withdrawn (Draft → Withdrawn), the term takes a new one.
        await _h.ExecuteAsync($"UPDATE pol.job SET state = 'WITHDRAWN' WHERE job_id = '{first}'");
        (await _h.StartChangeAsync(policy)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await _h.ExecuteAsync($"UPDATE pol.job SET state = 'WITHDRAWN' WHERE job_type = 'POLICY_CHANGE' AND policy_id = '{policy.PolicyId}'");

        await _h.NewTermVersionAsync(policy, "EXPIRED");
        var (expired, expiredBody) = await _h.StartChangeAsync(policy);
        expired.StatusCode.ShouldBe(HttpStatusCode.Conflict, expiredBody?.ToJsonString());
        expiredBody.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        // An unknown policy is not found.
        (await _h.SendAsync(HttpMethod.Post, "/api/pol/v1/policy-changes", new { policyId = Guid.NewGuid(), effectiveAt = ChangeHarness.Iso(DateTime.UtcNow) })).Body.Text("code").ShouldBe("POL-ERR-NOT-FOUND");
    }

    [Fact]
    public async Task REQ_POL_104_D_SL3_02_out_of_sequence_is_refused_with_the_typed_error()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 200);
        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);
        (await _h.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.BindAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Two days later an underwriter may still start a change effective day 199 (inside 30 days), but day 200 is bound.
        _h.SetDay(policy, 202);
        var earlier = ChangeHarness.Iso(DateTime.SpecifyKind(policy.Start.AddDays(199), DateTimeKind.Utc));
        var (refused, body) = await _h.StartChangeAsync(policy, earlier);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("POL-ERR-OUT-OF-SEQUENCE");

        // At the bound change's own effective time it is in sequence (not earlier).
        var same = ChangeHarness.Iso(DateTime.SpecifyKind(policy.Start.AddDays(200), DateTimeKind.Utc));
        (await _h.StartChangeAsync(policy, same, dryRun: true)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task REQ_POL_056_134_racing_binds_one_wins_the_other_conflicts_never_500_and_a_moved_head_is_preempted()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 100);
        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);
        (await _h.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _h.BindAsync(jobId)));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        results.Where(r => r.Response.StatusCode != HttpStatusCode.OK).ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.Conflict);
        results.Where(r => r.Response.StatusCode != HttpStatusCode.OK).Select(r => r.Body.Text("code"))
            .ShouldAllBe(code => code == "POL-ERR-STALE" || code == "POL-ERR-ILLEGAL-TRANSITION");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}' AND kind = 'CHANGE'")).ShouldBe(1);

        // Another transaction takes the term's head after a change was started: the bind is PREEMPTED (409), nothing is written.
        _h.SetDay(policy, 130);
        var preempted = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(preempted, policy, capacity: 1300);
        (await _h.QuoteAsync(preempted)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _h.NewTermVersionAsync(policy, "IN_FORCE", headSql: $"'{Guid.CreateVersion7()}'::uuid");
        var (response, body) = await _h.BindAsync(preempted);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("POL-ERR-PREEMPTED");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(2);
    }

    [Fact]
    public async Task REQ_POL_190_D_SL3_11_concurrent_creates_on_one_policy_one_wins_the_loser_is_409_never_500()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 20);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _h.StartChangeAsync(policy)));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        results.Where(r => r.Response.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.Conflict);
        results.Where(r => r.Response.StatusCode != HttpStatusCode.Created).Select(r => r.Body.Text("code"))
            .ShouldAllBe(code => code == "POL-ERR-STALE" || code == "POL-ERR-JOB-CONFLICT");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'POLICY_CHANGE'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_POL_191_only_vehicle_rating_fields_and_the_vehicle_may_change()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 50);
        var jobId = await _h.NewChangeAsync(policy);

        // The plate is identification, not a rating field.
        await _h.EditVehicleAsync(jobId, policy, plate: "zzz-0001");
        var (response, body) = await _h.QuoteAsync(jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("POL-ERR-VALIDATION");
        body!["errors"]!.AsArray().Select(e => e!["code"]!.GetValue<string>()).ShouldContain("CHANGE_NOT_ALLOWED");

        // Nor the cover.
        var (edited, edit) = await _h.EditAsync(jobId, [new { op = "SET_COVERAGES", coverages = new object[] { new { coverageCode = "MTPL", elementLocator = policy.Locator, selected = true } } }], draftVersion: 1);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, edit?.ToJsonString());
        (await _h.QuoteAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-VALIDATION");
        (await _h.ScalarAsync<string>($"SELECT state FROM pol.job WHERE job_id = '{jobId}'")).ShouldBe("DRAFT");
    }

    [Fact]
    public async Task REQ_POL_181_a_change_needs_explicit_confirmation_and_a_quote()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 30);
        var jobId = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(jobId, policy, capacity: 1600);
        (await _h.BindAsync(jobId)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        (await _h.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _h.BindAsync(jobId, confirmation: false)).Body.Text("code").ShouldBe("POL-ERR-HUMAN-CONFIRMATION-REQUIRED");
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
    }

    private async Task<string> RefAsync(Issued policy, Instant validAt)
    {
        var (response, body) = await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/snapshots/get?policyId={policy.PolicyId}&validAt={Uri.EscapeDataString(validAt.ToString())}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("snapshotRef");
    }

    private async Task<string> ContentAsync(string snapshotRef)
    {
        var (response, body) = await _h.SendAsync(HttpMethod.Get, $"/api/pol/v1/snapshots/get?snapshotRef={Uri.EscapeDataString(snapshotRef)}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["content"]!.ToJsonString();
    }

    private async Task<SnapshotSupersession> SupersessionAsync(string snapshotRef)
    {
        await using var scope = _h.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Roles = [Underwriter];
        var result = await scope.ServiceProvider.GetRequiredService<PolicySnapshots>()
            .GetDetailedAsync(new SnapshotQuery(null, null, snapshotRef, null, null), Ct);
        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        return result.Value.Supersession;
    }
}
