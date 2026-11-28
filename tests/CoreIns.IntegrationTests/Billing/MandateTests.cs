using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Platform.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-06a: payer instruments (IBAN P2: encrypted, blind-indexed, masked everywhere) and SEPA mandates (DRAFT, sign with consent
/// evidence, amend back to FRST, cancel; one live mandate per account), plus the rule that a DIRECT_DEBIT plan method needs an active mandate.
/// </summary>
public sealed class MandateTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    /// <summary>A valid synthetic Greek IBAN (ISO 13616 check digits computed), unique per call.</summary>
    private static string NewIban()
    {
        var bban = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (b % 10).ToString(CultureInfo.InvariantCulture))).PadRight(23, '7')[..23];
        var remainder = 0;
        foreach (var c in bban + "GR00")
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value >= 10 ? ((remainder * 100) + value) % 97 : ((remainder * 10) + value) % 97;
        }

        return "GR" + (98 - remainder).ToString("00", CultureInfo.InvariantCulture) + bban;
    }

    /// <summary>A bound policy whose payer has a billing account, and one ACTIVE instrument of that payer.</summary>
    private async Task<(BoundPolicy Policy, string Account, string Instrument, string Iban)> PayerAsync()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var account = await _slice.ScalarAsync<string>($"SELECT billing_account_id::text FROM bil.billing_account WHERE payer_party_id = '{policy.PartyId}'");
        var (instrument, iban) = await AddInstrumentAsync(policy.PartyId);
        return (policy, account, instrument, iban);
    }

    private async Task<(string Instrument, string Iban)> AddInstrumentAsync(string party)
    {
        var iban = NewIban();
        var (response, body) = await _slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = party, iban, holder = "Ελένη Παπαδοπούλου" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return (body.Text("instrument.instrumentId"), iban);
    }

    private async Task<JsonNode?> CreateAsync(string account, string instrument)
    {
        var (response, body) = await _slice.PostAsync("/api/bil/v1/mandates", new { billingAccountId = account, instrumentId = instrument });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return body!["mandate"];
    }

    private async Task<JsonNode?> SignAsync(string mandate, string evidence = "EVD-1")
    {
        var (response, body) = await _slice.PostAsync("/api/bil/v1/mandates/sign", new { mandateId = mandate, signatureEvidenceId = evidence });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["mandate"];
    }

    private async Task<long> ExposuresAsync(string secret) => await _slice.ScalarAsync<long>($"""
        SELECT (SELECT count(*) FROM bil.payment_instrument t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM bil.mandate t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.outbox_message t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.audit_event t WHERE t::text LIKE '%{secret}%')
             + (SELECT count(*) FROM plt.idempotency_record t WHERE t::text LIKE '%{secret}%')
        """);

    private async Task<HttpStatusCode> CodeAsync(string path, object body, string code)
    {
        var (response, problem) = await _slice.PostAsync(path, body);
        if (code.Length > 0)
        {
            problem.Text("code").ShouldBe(code, problem?.ToJsonString());
        }

        return response.StatusCode;
    }

    [Fact]
    public async Task BO_BIL_06a_an_instrument_is_encrypted_at_rest_masked_in_responses_and_never_in_clear()
    {
        var policy = await _slice.BindAsync();
        var iban = NewIban();
        var printed = string.Join(' ', Enumerable.Range(0, 7).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - (i * 4))))).ToLowerInvariant();

        var (response, body) = await _slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = policy.PartyId, iban = printed, holder = "Ελένη Παπαδοπούλου" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("instrument.maskedIban").ShouldBe("****" + iban[^4..]);
        body.Text("instrument.status").ShouldBe("ACTIVE");
        body.Text("instrument.kind").ShouldBe("BANK_ACCOUNT");
        body.Text("vopResult").ShouldBe("MATCH");
        body!.ToJsonString().ShouldNotContain(iban);
        var id = Guid.Parse(body.Text("instrument.instrumentId"));

        var envelope = await _slice.ScalarAsync<byte[]>($"SELECT iban_encrypted FROM bil.payment_instrument WHERE instrument_id = '{id}'");
        Convert.ToHexStringLower(envelope).ShouldNotContain(System.Text.Encoding.ASCII.GetBytes(iban).Aggregate(string.Empty, (s, b) => s + b.ToString("x2", CultureInfo.InvariantCulture)));
        var encryptor = _slice.Policy.Factory.Services.GetRequiredService<FieldEncryptor>();
        (await encryptor.DecryptAsync(envelope, PayeeProtection.InstrumentIbanField, id.ToString("N"), Ct)).ShouldBe(iban);
        (await _slice.ScalarAsync<string>($"SELECT iban_last4 FROM bil.payment_instrument WHERE instrument_id = '{id}'")).ShouldBe(iban[^4..]);

        // The same IBAN again, in another format, is the same instrument.
        var (_, same) = await _slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = policy.PartyId, iban, holder = "Ελένη Παπαδοπούλου" });
        same.Text("instrument.instrumentId").ShouldBe(id.ToString());

        // List: masked, with the VoP result, no IBAN anywhere in the system.
        var (listed, page) = await _slice.GetAsync($"/api/bil/v1/payment-instruments?partyId={policy.PartyId}");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().Count.ShouldBe(1);
        page.Text("items.0.instrument.maskedIban").ShouldBe("****" + iban[^4..]);
        page.ToJsonString().ShouldNotContain(iban);
        (await ExposuresAsync(iban)).ShouldBe(0);
        (await ExposuresAsync(iban.ToLowerInvariant())).ShouldBe(0);
        (await ExposuresAsync(iban[4..])).ShouldBe(0, "not even the BBAN");

        (await _slice.GetAsync($"/api/bil/v1/payment-instruments?partyId={policy.PartyId}", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.GetAsync("/api/bil/v1/payment-instruments")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("GR1601101250000000012300694")] // wrong check digits
    [InlineData("XX1601101250000000012300695")] // not a registry country
    public async Task BO_BIL_06a_a_bad_IBAN_or_a_card_token_is_refused_without_echoing_the_IBAN(string iban)
    {
        var party = Guid.NewGuid().ToString();
        var (response, problem) = await _slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = party, iban, holder = "Test Payer" });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("BIL-ERR-IBAN-INVALID");
        problem!.ToJsonString().ShouldNotContain(iban);
        (await ExposuresAsync(iban)).ShouldBe(0);

        var (card, _) = await _slice.PostAsync("/api/bil/v1/payment-instruments/add", new { partyId = party, holder = "Test Payer", orCardToken = "tok_123" });
        card.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task BO_BIL_06a_a_mandate_runs_draft_sign_amend_cancel_and_each_illegal_step_is_refused()
    {
        var (policy, account, instrument, iban) = await PayerAsync();

        var draft = await CreateAsync(account, instrument);
        var id = draft.Text("mandateId");
        draft.Text("status").ShouldBe("DRAFT");
        draft.Text("sequenceType").ShouldBe("FRST");
        draft.Text("scheme").ShouldBe("SEPA_CORE");
        draft.Text("origin").ShouldBe("NATIVE");
        draft.Text("maskedIban").ShouldBe("****" + iban[^4..]);
        var reference = draft.Text("mandateReference");
        reference.Length.ShouldBeInRange(1, 35);

        // One live mandate per account; a draft cannot be amended; signing needs consent evidence.
        (await CodeAsync("/api/bil/v1/mandates", new { billingAccountId = account, instrumentId = instrument }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (other, _) = await AddInstrumentAsync(policy.PartyId);
        (await CodeAsync("/api/bil/v1/mandates/amend", new { mandateId = id, instrumentId = other, signatureEvidenceId = "EVD-2" }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _slice.PostAsync("/api/bil/v1/mandates/sign", new { mandateId = id })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _slice.PostAsync("/api/bil/v1/mandates/sign", new { mandateId = id, signatureEvidenceId = "  " })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (future, futureBody) = await _slice.PostAsync("/api/bil/v1/mandates/sign", new { mandateId = id, signatureEvidenceId = "EVD-1", signatureDate = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        future.StatusCode.ShouldBe(HttpStatusCode.BadRequest, futureBody?.ToJsonString());

        // The instrument in use cannot be removed.
        (await CodeAsync("/api/bil/v1/payment-instruments/remove", new { instrumentId = instrument }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);

        var signed = await SignAsync(id);
        signed.Text("status").ShouldBe("ACTIVE");
        signed.Text("sequenceType").ShouldBe("FRST");
        signed.Text("signatureEvidenceId").ShouldBe("EVD-1");
        signed!["signatureDate"].ShouldNotBeNull();
        signed.Text("mandateReference").ShouldBe(reference);
        (await CodeAsync("/api/bil/v1/mandates/sign", new { mandateId = id, signatureEvidenceId = "EVD-1" }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);

        // A collection has been made under it (BO-BIL-06b will set this): the amendment starts a new first collection.
        await _slice.ScalarAsync<int>($"WITH u AS (UPDATE bil.mandate SET sequence_type = 'RCUR' WHERE mandate_id = '{id}' RETURNING 1) SELECT count(*)::int FROM u");
        (await CodeAsync("/api/bil/v1/mandates/amend", new { mandateId = id, instrumentId = instrument, signatureEvidenceId = "EVD-2" }, "BIL-ERR-NOT-ELIGIBLE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (amended, amendBody) = await _slice.PostAsync("/api/bil/v1/mandates/amend", new { mandateId = id, instrumentId = other, signatureEvidenceId = "EVD-2" });
        amended.StatusCode.ShouldBe(HttpStatusCode.OK, amendBody?.ToJsonString());
        amendBody.Text("mandate.sequenceType").ShouldBe("FRST");
        amendBody.Text("mandate.amendmentCount").ShouldBe("1");
        amendBody.Text("mandate.signatureEvidenceId").ShouldBe("EVD-2");
        amendBody.Text("mandate.instrumentId").ShouldBe(other);
        amendBody.Text("mandate.mandateReference").ShouldBe(reference);

        // The old instrument is free again, the new one is in use; another payer's instrument is not usable.
        (await _slice.PostAsync("/api/bil/v1/payment-instruments/remove", new { instrumentId = instrument })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CodeAsync("/api/bil/v1/payment-instruments/remove", new { instrumentId = other }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        var (stranger, _) = await AddInstrumentAsync(await _slice.Policy.CreatePartyAsync());
        (await CodeAsync("/api/bil/v1/mandates/amend", new { mandateId = id, instrumentId = stranger, signatureEvidenceId = "EVD-3" }, "BIL-ERR-NOT-ELIGIBLE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync("/api/bil/v1/mandates/amend", new { mandateId = id, instrumentId = instrument, signatureEvidenceId = "EVD-3" }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Get shows the same, masked; then cancel is final.
        var (got, view) = await _slice.GetAsync($"/api/bil/v1/mandates/{id}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, view?.ToJsonString());
        view.Text("mandate.status").ShouldBe("ACTIVE");
        view.Text("mandate.maskedIban").ShouldStartWith("****");
        var (cancelled, cancelBody) = await _slice.PostAsync("/api/bil/v1/mandates/cancel", new { mandateId = id, reason = "Customer request" });
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, cancelBody?.ToJsonString());
        cancelBody.Text("mandate.status").ShouldBe("CANCELLED");
        (await CodeAsync("/api/bil/v1/mandates/cancel", new { mandateId = id }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync("/api/bil/v1/mandates/sign", new { mandateId = id, signatureEvidenceId = "EVD-9" }, "BIL-ERR-MANDATE-STATE")).ShouldBe(HttpStatusCode.UnprocessableEntity);

        // With the previous mandate cancelled, the account can have a new one (new reference).
        var next = await CreateAsync(account, other);
        next.Text("mandateReference").ShouldNotBe(reference);

        // Nothing carries an IBAN: not the mandate rows, the audit trail, the events nor the responses.
        view!.ToJsonString().ShouldNotContain(iban);
        (await ExposuresAsync(iban)).ShouldBe(0);
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM plt.audit_event WHERE operation IN ('bil.Mandate.create', 'bil.Mandate.sign', 'bil.Mandate.amend', 'bil.Mandate.cancel') AND outcome = 'Succeeded'"))
            .ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task BO_BIL_06a_mandates_are_billing_staff_only_and_unknown_ids_are_not_found()
    {
        var (_, account, instrument, _) = await PayerAsync();
        (await _slice.PostAsync("/api/bil/v1/mandates", new { billingAccountId = account, instrumentId = instrument }, Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.GetAsync($"/api/bil/v1/mandates/{Guid.NewGuid()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.GetAsync("/api/bil/v1/mandates/not-a-guid")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.PostAsync("/api/bil/v1/mandates", new { billingAccountId = Guid.NewGuid(), instrumentId = instrument })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.PostAsync("/api/bil/v1/mandates", new { billingAccountId = account, instrumentId = Guid.NewGuid() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.PostAsync("/api/bil/v1/mandates/sign", new { mandateId = Guid.NewGuid(), signatureEvidenceId = "E" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BO_BIL_06a_direct_debit_as_a_plan_method_needs_an_active_mandate()
    {
        var (policy, account, instrument, _) = await PayerAsync();
        var change = new { termId = policy.TermId, planCode = "ANNUAL", method = "DIRECT_DEBIT" };

        // No mandate, then a draft one: refused.
        (await CodeAsync("/api/bil/v1/payment-plans/change", change, "BIL-ERR-NOT-ELIGIBLE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        var mandate = (await CreateAsync(account, instrument)).Text("mandateId");
        (await CodeAsync("/api/bil/v1/payment-plans/change", change, "BIL-ERR-NOT-ELIGIBLE")).ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Signed: accepted and persisted. Cancelled: refused again.
        await SignAsync(mandate);
        (await _slice.PostAsync("/api/bil/v1/payment-plans/change", change)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.ScalarAsync<string>($"SELECT method FROM bil.plan_instance WHERE term_id = '{policy.TermId}'")).ShouldBe("DIRECT_DEBIT");
        (await _slice.PostAsync("/api/bil/v1/mandates/cancel", new { mandateId = mandate })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CodeAsync("/api/bil/v1/payment-plans/change", change, "BIL-ERR-NOT-ELIGIBLE")).ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task BO_BIL_06a_a_migrated_mandate_is_readable_as_a_mandate_and_counts_for_direct_debit()
    {
        var (policy, account, _, _) = await PayerAsync();
        var id = Guid.NewGuid();
        var reference = "LEGACY-" + id.ToString("N")[..8];
        await _slice.ScalarAsync<int>($"""
            WITH m AS (INSERT INTO bil.mandate (mandate_id, legal_entity_id, billing_account_id, payer_party_id, mandate_reference, scheme, sequence_type, status,
                                                 iban_masked, signature_date, amendment_count, origin, created_at, created_by, record_version)
                       SELECT '{id}', legal_entity_id, billing_account_id, payer_party_id, '{reference}', 'SEPA_CORE', 'RCUR', 'ACTIVE',
                              '****1234', DATE '2020-01-15', 0, 'MIGRATION', now(), 'test', 1
                       FROM bil.billing_account WHERE billing_account_id = '{account}' RETURNING 1)
            SELECT count(*)::int FROM m
            """);
        var (got, view) = await _slice.GetAsync($"/api/bil/v1/mandates/{id}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, view?.ToJsonString());
        view.Text("mandate.origin").ShouldBe("MIGRATION");
        view.Text("mandate.maskedIban").ShouldBe("****1234");
        view.Text("mandate.sequenceType").ShouldBe("RCUR");
        view!["mandate"]!["instrumentId"].ShouldBeNull();

        // A migrated mandate counts as an active mandate for DIRECT_DEBIT and does not block a native one for the account.
        (await _slice.PostAsync("/api/bil/v1/payment-plans/change", new { termId = policy.TermId, planCode = "ANNUAL", method = "DIRECT_DEBIT" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
