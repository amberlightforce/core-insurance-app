using System.Globalization;
using System.Net;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.DataProtection;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// SL2-BIL-DISB: payee accounts (IBAN P2) and the disbursement service for CLM claim payments, end to end on the real
/// database: field encryption and masking, the gates (approval hash, payee account, VoP, cooling-off, duplicates,
/// sanctions, all fail closed), the stub bank channel, the sealed sub-ledger entries and the events in one transaction.
/// </summary>
public sealed class DisbursementTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private DisbursementSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_343_345_the_IBAN_is_encrypted_at_rest_masked_in_responses_and_never_stored_in_clear()
    {
        var party = PartyId.New();
        var iban = DisbursementSlice.NewIban();
        var printed = string.Join(' ', Enumerable.Range(0, 7).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - (i * 4))))).ToLowerInvariant();
        var key = Guid.NewGuid();

        var (created, account) = await _slice.PostAsync("/api/bil/v1/payee-accounts",
            new { partyId = party.Value, purpose = "CLAIM_PAYMENT", iban = printed, holderName = "Ελένη Παπαδοπούλου", evidenceRef = "DOC-1" }, key: key);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, account?.ToJsonString());
        account.Text("maskedIban").ShouldBe("****" + iban[^4..]);
        account.Text("verificationStatus").ShouldBe("VoPMatched");
        account.Text("change").ShouldBe("false");
        var id = Guid.Parse(account.Text("payeeAccountId"));
        account!.ToJsonString().ShouldNotContain(iban);

        // At rest: ciphertext + blind index + last four only; the envelope decrypts (row-bound) to the normalised IBAN.
        var envelope = await _slice.ScalarAsync<byte[]>($"SELECT iban_encrypted FROM bil.payee_account WHERE payee_account_id = '{id}'");
        Convert.ToHexStringLower(envelope).ShouldNotContain(DisbursementSlice.Hex(iban));
        var encryptor = _slice.Factory.Services.GetRequiredService<FieldEncryptor>();
        (await encryptor.DecryptAsync(envelope, PayeeProtection.IbanField, id.ToString("N"), Ct)).ShouldBe(iban);
        (await _slice.ScalarAsync<string>($"SELECT iban_last4 FROM bil.payee_account WHERE payee_account_id = '{id}'")).ShouldBe(iban[^4..]);
        (await _slice.ScalarAsync<string>($"SELECT cooling_off_until::text FROM bil.payee_account WHERE payee_account_id = '{id}'"))
            .ShouldBe(BusinessDate.Parse(await _slice.ScalarAsync<string>($"SELECT valid_from::text FROM bil.payee_account WHERE payee_account_id = '{id}'")).AddDays(30).ToString());

        // The same IBAN again (another format, another key) is the same account; a replay of the key too.
        var (again, same) = await _slice.PostAsync("/api/bil/v1/payee-accounts", new { partyId = party.Value, purpose = "CLAIM_PAYMENT", iban, holderName = "Ελένη Παπαδοπούλου" });
        again.IsSuccessStatusCode.ShouldBeTrue(same?.ToJsonString());
        same.Text("payeeAccountId").ShouldBe(id.ToString());
        var (replay, replayed) = await _slice.PostAsync("/api/bil/v1/payee-accounts",
            new { partyId = party.Value, purpose = "CLAIM_PAYMENT", iban = printed, holderName = "Ελένη Παπαδοπούλου", evidenceRef = "DOC-1" }, key: key);
        replay.IsSuccessStatusCode.ShouldBeTrue(replayed?.ToJsonString());
        replayed.Text("payeeAccountId").ShouldBe(id.ToString());

        // bil.PayeeAccount.get: masked; no IBAN in any response.
        var (got, view) = await _slice.GetAsync($"/api/bil/v1/payee-accounts/{id}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, view?.ToJsonString());
        view.Text("maskedIban").ShouldBe("****" + iban[^4..]);
        view.Text("status").ShouldBe("Active");
        view.Text("purpose").ShouldBe("CLAIM_PAYMENT");
        view!.ToJsonString().ShouldNotContain(iban);
        (await _slice.GetAsync($"/api/bil/v1/payee-accounts/{id}?purpose=REFUND")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _slice.GetAsync($"/api/bil/v1/payee-accounts/{id}", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A disbursement to it, then: the IBAN is nowhere in clear — payee account row, disbursement, ledger, outbox,
        // audit, idempotency store (request and response) — in any format.
        await _slice.RequestAsync(DisbursementSlice.ClaimPayment(party, id, 120m));
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.idempotency_record WHERE encode(response_body, 'escape') LIKE '%{iban[^4..]}%'")).ShouldBeGreaterThan(0);
        (await _slice.ExposuresAsync(iban)).ShouldBe(0);
        (await _slice.ExposuresAsync(iban.ToLowerInvariant())).ShouldBe(0);
        (await _slice.ExposuresAsync(iban[4..])).ShouldBe(0, "not even the BBAN");
    }

    [Theory]
    [InlineData("GR1601101250000000012300694", "IBAN-INVALID")] // check digits wrong
    [InlineData("GR160110125000000001230069", "IBAN-INVALID")] // one character short for GR
    [InlineData("XX1601101250000000012300695", "IBAN-INVALID")] // not a registry country
    [InlineData("GR16-0110-1250-0000-0001-2300-695", "IBAN-INVALID")] // not alphanumeric
    public async Task REQ_BIL_343_a_bad_IBAN_is_refused_without_echoing_it(string iban, string code)
    {
        var (response, problem) = await _slice.PostAsync("/api/bil/v1/payee-accounts",
            new { partyId = PartyId.New().Value, purpose = "CLAIM_PAYMENT", iban, holderName = "Test Payee" });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("BIL-ERR-" + code);
        problem!.ToJsonString().ShouldNotContain(iban);
        (await _slice.ExposuresAsync(iban)).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_BIL_343_a_holder_name_with_control_characters_is_refused()
    {
        var (response, problem) = await _slice.PostAsync("/api/bil/v1/payee-accounts",
            new { partyId = PartyId.New().Value, purpose = "CLAIM_PAYMENT", iban = DisbursementSlice.NewIban(), holderName = "Bad\u0007Name" });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("BIL-ERR-CHARSET");
    }

    [Fact]
    public async Task REQ_BIL_009_207_211_a_claim_payment_is_released_issued_and_cleared_with_sealed_entries_and_events_in_one_transaction()
    {
        var party = PartyId.New();
        var claim = ClaimId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var request = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 2400m, claim: claim);

        var paid = await _slice.RequestAsync(request);
        paid.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Cleared);
        paid.SourceModule.ShouldBe(ModuleCode.CLM);
        paid.SourceType.ShouldBe("CLM_PAYMENT");
        paid.SourceId.ShouldBe(request.SourceId);
        paid.ClaimId.ShouldBe(claim);
        paid.Amount.ShouldBe(new Money(2400m, Currency.EUR));
        paid.MaskedPayeeAccount.ShouldBe(account.MaskedIban);
        paid.DisbursementNumber!.Value.Value.ShouldStartWith("DSB");
        paid.IssuedAt.ShouldNotBeNull();
        paid.ClearedAt.ShouldNotBeNull();
        paid.ValueDate.ShouldNotBeNull();
        var id = paid.DisbursementId.Value;

        // The screening ran on the payee with the disbursement as caller reference (REQ-BIL-200).
        var screened = _slice.Screening.CallsTo("pty.Screening.screen").Select(c => (ScreeningScreenRequest)c.Arguments[0]!).ToList();
        screened.ShouldContain(s => s.PartyId == party && s.CallerRef.Type == "Disbursement" && s.CallerRef.Id == id.ToString("D"));

        // bil.Disbursement.get in process (as CLM reads it) and over the API (Staff.Billing).
        await using (var scope = _slice.Scope())
        {
            var read = await scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>().GetAsync(id.ToString(), Ct);
            read.Disbursement.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Cleared);
        }

        var (got, body) = await _slice.GetAsync($"/api/bil/v1/disbursements/{id}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("disbursement.status").ShouldBe("Cleared");
        body.Text("disbursement.maskedPayeeAccount").ShouldBe(account.MaskedIban);
        (await _slice.GetAsync($"/api/bil/v1/disbursements/{id}", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.GetAsync($"/api/bil/v1/disbursements/{Guid.CreateVersion7()}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Sub-ledger (REQ-BIL-211): released LA-17 → LA-13, cleared LA-13 → LA-10, each balanced, with dimensions.
        var lines = await _slice.TextsAsync(
            $"""
            SELECT e.entry_type || ':' || l.side || ':' || l.account_code || ':' || l.amount::numeric(19,2) || ':' || l.source_type || ':' || l.source_id || ':' || l.claim_id || ':' || l.disbursement_id
            FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE e.disbursement_id = '{id}' ORDER BY e.recorded_at, e.entry_type DESC, l.line_no
            """);
        var tail = $":2400.00:CLM_PAYMENT:{request.SourceId}:{claim.Value}:{id}";
        lines.ShouldBe(
        [
            "DISBURSEMENT_RELEASED:DEBIT:LA-17" + tail,
            "DISBURSEMENT_RELEASED:CREDIT:LA-13" + tail,
            "DISBURSEMENT_CLEARED:DEBIT:LA-13" + tail,
            "DISBURSEMENT_CLEARED:CREDIT:LA-10" + tail,
        ]);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{id}' AND billing_account_id IS NULL")).ShouldBe(2);

        // Events (REQ-BIL-332): DisbursementIssued, DisbursementCleared and both BillingEntryPosted are written by the
        // transaction that created (and so sealed) both ledger entries: one top-level xid for all of them.
        var outbox = await _slice.TextsAsync(
            $"SELECT event_type || ':' || aggregate_type || ':' || (xmin::text) FROM plt.outbox_message WHERE aggregate_id = '{id}' ORDER BY aggregate_sequence");
        // The top-level transaction id: the ledger header records txid_current() (EF savepoints give the disbursement row a
        // subtransaction xmin, so the row's own xmin is not compared).
        var xid = await _slice.ScalarAsync<string>($"SELECT DISTINCT (created_txid % 4294967296)::text FROM bil.ledger_entry WHERE disbursement_id = '{id}'");
        outbox.ShouldBe(
        [
            $"BillingEntryPosted:Disbursement:{xid}",
            $"DisbursementIssued:Disbursement:{xid}",
            $"BillingEntryPosted:Disbursement:{xid}",
            $"DisbursementCleared:Disbursement:{xid}",
        ]);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{id}' AND (created_txid % 4294967296)::text = '{xid}'"))
            .ShouldBe(2);

        var issued = await _slice.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'DisbursementIssued'");
        foreach (var expected in new[] { "\"sourceModule\": \"CLM\"", "\"sourceType\": \"CLM_PAYMENT\"", $"\"sourceId\": \"{request.SourceId}\"", "\"method\": \"SEPA_CT\"", "\"valueDate\"", "\"amount\"" })
        {
            issued.ShouldContain(expected);
        }

        var cleared = await _slice.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'DisbursementCleared'");
        cleared.ShouldContain("\"sourceModule\": \"CLM\"");
        cleared.ShouldContain("\"amount\"");
        foreach (var payload in new[] { issued, cleared })
        {
            payload.ShouldNotContain("Παπαδοπούλου");
            payload.ShouldNotContain("iban", Case.Insensitive);
            payload.ShouldNotContain(account.MaskedIban);
        }

        var posted = await _slice.TextsAsync($"SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'BillingEntryPosted' ORDER BY aggregate_sequence");
        posted[0].ShouldContain("\"eventType\": \"DISBURSEMENT_RELEASED\"");
        posted[1].ShouldContain("\"eventType\": \"DISBURSEMENT_CLEARED\"");
        foreach (var expected in new[] { $"\"disbursementId\": \"{id}\"", "\"sourceType\": \"CLM_PAYMENT\"", $"\"claimId\": \"{claim.Value}\"", $"\"sourceId\": \"{request.SourceId}\"" })
        {
            posted.ShouldAllBe(p => p.Contains(expected, StringComparison.Ordinal));
        }

        // Audited with the number and the masked account only.
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.Disbursement.request' AND outcome = 'Succeeded' AND object_id = '{id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_009_requests_are_idempotent_on_the_key_and_a_dry_run_stores_nothing()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var request = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 75.5m);
        var options = CommandOptions.New();

        var preview = await _slice.RequestAsync(request, options with { DryRun = true });
        preview.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Cleared);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{request.SourceId}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{preview.DisbursementId.Value}'")).ShouldBe(0);

        var first = await _slice.RequestAsync(request, options);
        var second = await _slice.RequestAsync(request, options);
        second.DisbursementId.ShouldBe(first.DisbursementId);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{request.SourceId}'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{first.DisbursementId.Value}'")).ShouldBe(2);
    }

    [Fact]
    public async Task REQ_BIL_202_a_duplicate_disbursement_is_refused_and_names_the_existing_one()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var request = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 300m);
        var first = await _slice.RequestAsync(request);

        // Same source payment, a different idempotency key (a CLM retry that lost its key).
        var error = await Should.ThrowAsync<DomainException>(() => _slice.RequestAsync(request));
        error.Error.Code.Value.ShouldBe("BIL-ERR-DUPLICATE");
        error.Error.Metadata!["existingDisbursementId"].ShouldBe(first.DisbursementId.Value.ToString());
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{request.SourceId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_202_concurrent_identical_requests_produce_one_disbursement()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var request = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 999.99m);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            try
            {
                return (await _slice.RequestAsync(request)).Status.ToString();
            }
            catch (DomainException ex)
            {
                return ex.Error.Code.Value;
            }
        })));

        outcomes.Count(o => o == "Cleared").ShouldBe(1, string.Join(", ", outcomes));
        outcomes.Count(o => o == "BIL-ERR-DUPLICATE").ShouldBe(5, string.Join(", ", outcomes));
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{request.SourceId}'")).ShouldBe(1);
        (await _slice.ScalarAsync<long>(
            $"SELECT count(*) FROM bil.ledger_entry e JOIN bil.disbursement d ON d.disbursement_id = e.disbursement_id WHERE d.source_id = '{request.SourceId}'")).ShouldBe(2);
        (await _slice.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.outbox_message o JOIN bil.disbursement d ON o.aggregate_id = d.disbursement_id::text WHERE d.source_id = '{request.SourceId}' AND o.event_type = 'DisbursementIssued'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_200_214_screening_that_is_not_Clear_fails_closed()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var cases = new (Action Arrange, string Code)[]
        {
            (() => _slice.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear() with { Result = ScreeningScreenResponse.ResultValue.PotentialHit, CaseNumber = "SCR-1" }), "BIL-ERR-PAYEE-BLOCKED"),
            (() => _slice.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear() with { Result = ScreeningScreenResponse.ResultValue.Blocked, PaymentBlock = true }), "BIL-ERR-PAYEE-BLOCKED"),
            (() => _slice.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear() with { PaymentBlock = true }), "BIL-ERR-PAYEE-BLOCKED"),
            (() => _slice.Screening.Fail("pty.Screening.screen", new DomainException(DomainError.Of(ModuleCode.PTY, "LISTS-STALE", "Lists are stale."))), "BIL-ERR-SCREENING-UNAVAILABLE"),
            (() => _slice.Screening.Fail("pty.Screening.screen", new TimeoutException("down")), "BIL-ERR-SCREENING-UNAVAILABLE"),
        };

        foreach (var (arrange, code) in cases)
        {
            arrange();
            var request = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 50m);
            (await _slice.RefusedAsync(request)).ShouldBe(code);
            (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{request.SourceId}'")).ShouldBe(0);
            (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE source_id = '{request.SourceId}'")).ShouldBe(0);
        }

        (await _slice.ScalarAsync<long>("SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.Disbursement.request' AND outcome = 'Rejected' AND error_code = 'BIL-ERR-PAYEE-BLOCKED'"))
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task REQ_BIL_214_without_a_screening_service_nothing_is_paid()
    {
        await using var slice = new DisbursementSlice(database, screening: false);
        var party = PartyId.New();
        var account = await slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        (await slice.RefusedAsync(DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 10m))).ShouldBe("BIL-ERR-SCREENING-UNAVAILABLE");
    }

    [Fact]
    public async Task REQ_BIL_197_198_source_method_currency_and_approval_hash_are_enforced()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var valid = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 100m);

        (await _slice.RefusedAsync(valid with { SourceType = "RI_SETTLEMENT" })).ShouldBe("BIL-ERR-SOURCE");
        (await _slice.RefusedAsync(valid with { Method = "SEPA_INSTANT" })).ShouldBe("BIL-ERR-METHOD-NOT-ALLOWED");
        (await _slice.RefusedAsync(valid with { Amount = new Money(100m, Currency.USD) })).ShouldBe("BIL-ERR-CURRENCY");
        (await _slice.RefusedAsync(DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 100.01m, approvedAmount: 100m))).ShouldBe("BIL-ERR-APPROVAL-MISMATCH");
        (await _slice.RefusedAsync(valid with { ApprovalContentHash = null })).ShouldBe("BIL-ERR-VALIDATION");
        (await _slice.RefusedAsync(valid with { Amount = new Money(-5m, Currency.EUR) })).ShouldBe("BIL-ERR-VALIDATION");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE payee_account_id = '{account.PayeeAccountId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_BIL_199_204_345_the_payee_account_gates_hold_the_payment()
    {
        var party = PartyId.New();
        var first = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());

        // Not the payee's account; not a claim-payment account; unknown account.
        (await _slice.RefusedAsync(DisbursementSlice.ClaimPayment(PartyId.New(), first.PayeeAccountId, 10m))).ShouldBe("BIL-ERR-PAYEE-ACCOUNT");
        var refund = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban(), PayeeAccountCreateRequest.PurposeValue.Refund);
        (await _slice.RefusedAsync(DisbursementSlice.ClaimPayment(party, refund.PayeeAccountId, 10m))).ShouldBe("BIL-ERR-PAYEE-ACCOUNT");
        (await _slice.RefusedAsync(DisbursementSlice.ClaimPayment(party, Guid.CreateVersion7(), 10m))).ShouldBe("BIL-ERR-NOT-FOUND");

        // A changed account supersedes the first (never an overwrite) and is held within cooling-off (REQ-BIL-199).
        var changed = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        changed.Change.ShouldBeTrue();
        changed.PayeeAccountId.ShouldNotBe(first.PayeeAccountId);
        (await _slice.ScalarAsync<string>($"SELECT status FROM bil.payee_account WHERE payee_account_id = '{first.PayeeAccountId}'")).ShouldBe("SUPERSEDED");
        (await _slice.RefusedAsync(DisbursementSlice.ClaimPayment(party, changed.PayeeAccountId, 10m))).ShouldBe("BIL-ERR-COOLING-OFF");
        (await _slice.RefusedAsync(DisbursementSlice.ClaimPayment(party, first.PayeeAccountId, 10m))).ShouldBe("BIL-ERR-PAYEE-ACCOUNT");

        // Without a VoP adapter the account is VoPNotAvailable and payments to it are held (REQ-BIL-204, -214).
        await using var noVop = new DisbursementSlice(database, payeeVerifier: false);
        var other = PartyId.New();
        var unverified = await noVop.CreateAccountAsync(other, DisbursementSlice.NewIban());
        unverified.VerificationStatus.ShouldBe(PayeeVerificationStatus.VoPNotAvailable);
        (await noVop.RefusedAsync(DisbursementSlice.ClaimPayment(other, unverified.PayeeAccountId, 10m))).ShouldBe("BIL-ERR-VOP-HOLD");
    }

    [Fact]
    public async Task D_SL2_05_without_a_bank_channel_the_disbursement_stays_Approved_with_no_cash_entries()
    {
        await using var slice = new DisbursementSlice(database, bankChannel: false);
        var party = PartyId.New();
        var account = await slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var approved = await slice.RequestAsync(DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 42m));
        approved.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Approved);
        approved.ReleasedAt.ShouldBeNull();
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{approved.DisbursementId.Value}'")).ShouldBe(0);
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{approved.DisbursementId.Value}'")).ShouldBe(0);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    public void D_SL2_05_the_VoP_and_bank_channel_stubs_are_never_bound_in_Production(string environment, bool bound)
    {
        var provider = new ServiceCollection().AddBillingPaymentAdapters(new Environment(environment)).BuildServiceProvider();
        (provider.GetService<IBankChannel>() is StubBankChannel).ShouldBe(bound);
        (provider.GetService<IPayeeVerifier>() is StubPayeeVerifier).ShouldBe(bound);
        if (!bound)
        {
            provider.GetService<IBankChannel>().ShouldBeNull();
            provider.GetService<IPayeeVerifier>().ShouldBeNull();
        }
    }

    [Fact]
    public async Task D_ARC_34_as_the_app_role_disbursement_entries_are_sealed_and_disbursements_and_payee_accounts_are_frozen()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var paid = await _slice.RequestAsync(DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 500m));
        var id = paid.DisbursementId.Value;
        var entry = await _slice.ScalarAsync<Guid>($"SELECT entry_id FROM bil.ledger_entry WHERE disbursement_id = '{id}' AND entry_type = 'DISBURSEMENT_RELEASED'");
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);

        // The review probe: a balanced pair appended to the existing released entry in a later transaction.
        var sealedError = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var connection = await app.OpenConnectionAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var insert = new NpgsqlCommand(
                $"""
                INSERT INTO bil.ledger_line (line_id, entry_id, line_no, account_code, side, amount, currency, rule_id, legal_entity_id, disbursement_id)
                VALUES ('{Guid.CreateVersion7()}', '{entry}', 101, 'LA-17', 'DEBIT', 1000, 'EUR', 'BLR-DISB-RELEASED-CLM', '{ApiHostFactory.LegalEntityId}', '{id}'),
                       ('{Guid.CreateVersion7()}', '{entry}', 102, 'LA-13', 'CREDIT', 1000, 'EUR', 'BLR-DISB-RELEASED-CLM', '{ApiHostFactory.LegalEntityId}', '{id}');
                """, connection, transaction))
            {
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await transaction.CommitAsync(Ct);
        });
        sealedError.SqlState.ShouldBe("BL005");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_line WHERE entry_id = '{entry}'")).ShouldBe(2);

        foreach (var (sql, state) in new[]
                 {
                     ($"UPDATE bil.ledger_line SET amount = 1 WHERE entry_id = '{entry}'", "42501"),
                     ($"UPDATE bil.disbursement SET amount = amount + 1 WHERE disbursement_id = '{id}'", "BL004"),
                     ($"UPDATE bil.disbursement SET payee_account_id = '{Guid.CreateVersion7()}' WHERE disbursement_id = '{id}'", "BL004"),
                     ($"UPDATE bil.payee_account SET iban_last4 = '0000' WHERE payee_account_id = '{account.PayeeAccountId}'", "BL004"),
                     ($"UPDATE bil.payee_account SET iban_encrypted = '\\x00' WHERE payee_account_id = '{account.PayeeAccountId}'", "BL004"),
                     ($"DELETE FROM bil.disbursement WHERE disbursement_id = '{id}'", "42501"),
                 })
        {
            var error = await Should.ThrowAsync<PostgresException>(async () =>
            {
                await using var command = app.CreateCommand(sql);
                await command.ExecuteNonQueryAsync(Ct);
            });
            error.SqlState.ShouldBe(state, sql);
        }

        // Owner-level guards hold even with the privileges: no delete of a disbursement or a payee account (BL002).
        await using var admin = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        foreach (var sql in new[] { $"DELETE FROM bil.disbursement WHERE disbursement_id = '{id}'", $"DELETE FROM bil.payee_account WHERE payee_account_id = '{account.PayeeAccountId}'" })
        {
            var error = await Should.ThrowAsync<PostgresException>(async () =>
            {
                await using var command = admin.CreateCommand(sql);
                await command.ExecuteNonQueryAsync(Ct);
            });
            error.SqlState.ShouldBe("BL002", sql);
        }

        (await _slice.ScalarAsync<bool>("SELECT has_table_privilege('app', 'bil.disbursement', 'DELETE') OR has_table_privilege('app', 'bil.payee_account', 'DELETE')")).ShouldBeFalse();
        (await _slice.ScalarAsync<bool>("SELECT has_table_privilege('app', 'bil.disbursement', 'UPDATE') AND has_table_privilege('app', 'bil.payee_account', 'INSERT')")).ShouldBeTrue();

        // The money is still exactly what was posted, and every disbursement entry balances.
        (await _slice.ScalarAsync<long>(
            $"""
            SELECT count(*) FROM (SELECT e.entry_id FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE e.disbursement_id = '{id}' GROUP BY e.entry_id HAVING sum(CASE l.side WHEN 'DEBIT' THEN l.amount ELSE -l.amount END) <> 0) unbalanced
            """)).ShouldBe(0);
        (await _slice.ScalarAsync<decimal>($"SELECT amount FROM bil.disbursement WHERE disbursement_id = '{id}'")).ShouldBe(500m);
    }

    [Fact]
    public void DisbursementContent_hash_is_stable_and_sensitive_to_every_approved_field()
    {
        var party = new PartyId(Guid.Parse("0192f0c4-0000-7000-8000-0000000000a1"));
        var account = Guid.Parse("0192f0c4-0000-7000-8000-0000000000b2");
        var hash = DisbursementContent.Hash("CLM_PAYMENT", "pay-1", party, account, new Money(2400m, Currency.EUR));
        DisbursementContent.Hash("CLM_PAYMENT", "pay-1", party, account, new Money(2400.00m, Currency.EUR)).ShouldBe(hash);
        DisbursementContent.Hash("CLM_PAYMENT", "pay-1", party, account, new Money(2400.01m, Currency.EUR)).ShouldNotBe(hash);
        DisbursementContent.Hash("CLM_PAYMENT", "pay-2", party, account, new Money(2400m, Currency.EUR)).ShouldNotBe(hash);
        DisbursementContent.Hash("CLM_PAYMENT", "pay-1", PartyId.New(), account, new Money(2400m, Currency.EUR)).ShouldNotBe(hash);
        DisbursementContent.Hash("CLM_PAYMENT", "pay-1", party, Guid.CreateVersion7(), new Money(2400m, Currency.EUR)).ShouldNotBe(hash);
        hash.Value.Length.ShouldBe(64);
        string.Create(CultureInfo.InvariantCulture, $"{hash}").ShouldBe(hash.Value);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
