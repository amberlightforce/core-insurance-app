using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Party;
using Microsoft.Extensions.DependencyInjection;
using CoreIns.IntegrationTests.Party;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-09: the payee list and verify operations and the public disbursement request for the non-claim sources (commission,
/// levy, tax remittance, other): masked IBAN everywhere, VoP re-run idempotent and fail-closed, BIL.DISBURSEMENT authority on the
/// requester and on the approver, a second approver (maker is never the checker), duplicates refused by the business key,
/// sanctions screening and VoP gates, and the sources that carry their own approval kept off REST.
/// </summary>
public sealed class DisbursementFacadeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Billing = "Staff.Billing";
    private const string Manager = "Staff.BillingManager";
    private DisbursementSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body, string roles, string user)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> Rest(HttpMethod method, string path, object? body = null, string roles = Billing, string user = "maker")
        => SendAsync(_slice.Client, method, path, body, roles, user);

    private async Task<PayeeAccountCreateResponse> AccountAsync(PartyId party, PayeeAccountCreateRequest.PurposeValue purpose, string? iban = null)
    {
        await using var scope = _slice.Scope(Billing);
        return await scope.ServiceProvider.GetRequiredService<IBillingPayeeAccountService>().CreateAsync(
            new PayeeAccountCreateRequest { PartyId = party, Purpose = purpose, Iban = iban ?? DisbursementSlice.NewIban(), HolderName = "Ελένη Παπαδοπούλου" },
            CoreIns.Platform.Contracts.CommandOptions.New(), Ct);
    }

    private static DisbursementRequestRequest Levy(PartyId payee, Guid account, decimal amount, string? sourceId = null, string sourceType = DisbursementCodes.LevyPayment, string? reference = null) => new()
    {
        SourceType = sourceType,
        SourceId = sourceId ?? "LEVY-" + Guid.NewGuid().ToString("N"),
        PayeePartyId = payee,
        PayeeAccountId = account,
        Amount = new Money(amount, Currency.EUR),
        Method = DisbursementCodes.SepaCreditTransfer,
        StatementReference = reference,
        PurposeText = "Levy payment",
    };

    [Fact]
    public async Task BO_BIL_09_payee_accounts_list_masks_the_IBAN_and_filters_by_party_and_purpose()
    {
        var party = PartyId.New();
        var iban = DisbursementSlice.NewIban();
        var levy = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Levy, iban);
        await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Commission);

        var (response, body) = await Rest(HttpMethod.Get, $"/api/bil/v1/payee-accounts?partyId={party.Value}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!["items"]!.AsArray().Count.ShouldBe(2);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, filtered) = await Rest(HttpMethod.Get, $"/api/bil/v1/payee-accounts?partyId={party.Value}&purpose=LEVY");
        var items = filtered!["items"]!.AsArray();
        items.Count.ShouldBe(1);
        items[0]!["payeeAccountId"]!.GetValue<string>().ShouldBe(levy.PayeeAccountId.ToString());
        items[0]!["maskedIban"]!.GetValue<string>().ShouldBe("****" + iban[^4..]);
        items[0]!["verificationStatus"]!.GetValue<string>().ShouldBe("VoPMatched");
        filtered.ToJsonString().ShouldNotContain(iban);

        (await Rest(HttpMethod.Get, $"/api/bil/v1/payee-accounts?partyId={party.Value}", roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Rest(HttpMethod.Get, $"/api/bil/v1/payee-accounts?purpose=lev';--")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BO_BIL_09_verify_records_the_result_idempotently_and_never_returns_the_IBAN()
    {
        var party = PartyId.New();
        var iban = DisbursementSlice.NewIban();
        var account = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Levy, iban);

        var (first, firstBody) = await Rest(HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { payeeAccountId = account.PayeeAccountId });
        var (second, secondBody) = await Rest(HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { payeeAccountId = account.PayeeAccountId });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var body in new[] { firstBody!, secondBody! })
        {
            body["outcome"]!.GetValue<string>().ShouldBe("Match");
            body["verificationStatus"]!.GetValue<string>().ShouldBe("VoPMatched");
            body["maskedIban"]!.GetValue<string>().ShouldBe("****" + iban[^4..]);
            body["preCheck"]!.GetValue<bool>().ShouldBeFalse();
            body.ToJsonString().ShouldNotContain(iban);
        }

        (await _slice.ScalarAsync<string>($"SELECT verification_status FROM bil.payee_account WHERE payee_account_id = '{account.PayeeAccountId}'")).ShouldBe("VOP_MATCHED");
        (await _slice.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.audit_event WHERE operation = 'bil.PayeeAccount.verify' AND outcome = 'Succeeded' AND object_id = '{account.PayeeAccountId}'")).ShouldBeGreaterThanOrEqualTo(2);
        (await _slice.ExposuresAsync(iban)).ShouldBe(0);

        // A pre-check stores nothing and reports the masked IBAN; a bad IBAN is refused without echoing it.
        var (preCheck, preBody) = await Rest(HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { iban = DisbursementSlice.NewIban(), holderName = "Γιώργος Νικολάου" });
        preCheck.StatusCode.ShouldBe(HttpStatusCode.OK);
        preBody!["preCheck"]!.GetValue<bool>().ShouldBeTrue();
        preBody["payeeAccountId"].ShouldBeNull();
        var (bad, badBody) = await Rest(HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { iban = "GR0000000000000000000000000", holderName = "X" });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        badBody.Text("code").ShouldBe("BIL-ERR-IBAN-INVALID");
        (await Rest(HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { payeeAccountId = Guid.NewGuid() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Rest(HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { payeeAccountId = account.PayeeAccountId, iban = "x", holderName = "y" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BO_BIL_09_without_a_verifier_verify_is_503_and_a_new_account_is_held_so_nothing_is_paid()
    {
        await using var slice = new DisbursementSlice(database, payeeVerifier: false);
        var party = PartyId.New();
        await using var scope = slice.Scope(Billing);
        var account = await scope.ServiceProvider.GetRequiredService<IBillingPayeeAccountService>().CreateAsync(
            new PayeeAccountCreateRequest { PartyId = party, Purpose = PayeeAccountCreateRequest.PurposeValue.Levy, Iban = DisbursementSlice.NewIban(), HolderName = "Ελένη Παπαδοπούλου" },
            CoreIns.Platform.Contracts.CommandOptions.New(), Ct);
        account.VerificationStatus.ShouldBe(PayeeVerificationStatus.VoPNotAvailable);

        var (response, body) = await SendAsync(slice.Client, HttpMethod.Post, "/api/bil/v1/payee-accounts/verify", new { payeeAccountId = account.PayeeAccountId }, Billing, "maker");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.Text("code").ShouldBe("BIL-ERR-VOP-UNAVAILABLE");

        var (held, heldBody) = await SendAsync(slice.Client, HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, account.PayeeAccountId, 100m), Billing, "maker");
        held.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        heldBody.Text("code").ShouldBe("BIL-ERR-VOP-HOLD");
    }

    [Fact]
    public async Task BO_BIL_09_a_levy_request_waits_for_a_second_approver_and_the_requester_cannot_approve_it()
    {
        var party = PartyId.New();
        var account = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Levy);
        var request = Levy(party, account.PayeeAccountId, 2500.50m, reference: "LEVY-2026-Q3");

        var (created, body) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", request);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("status").ShouldBe("PendingApproval");
        body.Text("sourceType").ShouldBe("BIL_LEVY");
        body.Text("sourceModule").ShouldBe("BIL");
        body!["releaseApproval"]!["required"]!.GetValue<bool>().ShouldBeTrue();
        var id = body.Text("disbursementId");

        // Business key: the same source again, or the same reference with the same payee account and amount, is a duplicate (EC-08).
        var (dup, dupBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", request with { StatementReference = null });
        dup.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        dupBody.Text("code").ShouldBe("BIL-ERR-DUPLICATE");
        var (dupRef, _) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, account.PayeeAccountId, 2500.50m, reference: "LEVY-2026-Q3"));
        dupRef.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Maker is never the approver (even a billing manager).
        var (self, selfBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId = id, decision = "APPROVE" }, Manager, "maker");
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        selfBody.Text("code").ShouldBe("BIL-ERR-SOD");
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{id}'")).ShouldBe("PENDING_APPROVAL");

        var (approved, approvedBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId = id, decision = "APPROVE" }, Manager, "checker");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        approvedBody!["disbursement"]!["status"]!.GetValue<string>().ShouldBe("Approved");

        // Approved and waiting for its batch: nothing is posted or sent to the bank yet (BO-BIL-10).
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{id}'")).ShouldBe(0);
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{id}'")).ShouldBe("APPROVED");
        var (read, readBody) = await Rest(HttpMethod.Get, $"/api/bil/v1/disbursements/{id}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        readBody!["disbursement"]!["maskedPayeeAccount"]!.GetValue<string>().ShouldStartWith("****");
    }

    [Fact]
    public async Task BO_BIL_09_authority_limits_bind_the_requester_and_the_approver()
    {
        var party = PartyId.New();
        var account = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Commission);

        // Staff.Billing is limited to 10,000.00 and the manager to 1,000,000.00: above the requester's own limit the request refers to
        // a second person (who is checked again at approval); above every limit, or with no grant at all, it is refused.
        var (hugeManager, hugeBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, account.PayeeAccountId, 2_000_000m, sourceType: DisbursementCodes.CommissionPayment), Manager);
        hugeManager.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        hugeBody.Text("code").ShouldBe("BIL-ERR-NOT-PERMITTED");
        var (noGrant, noGrantBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, account.PayeeAccountId, 10m, sourceType: DisbursementCodes.CommissionPayment), "Platform.Admin");
        noGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE payee_account_id = '{account.PayeeAccountId}'")).ShouldBe(0);

        // The manager may request a large payment; a different manager approves it.
        var (big, bigBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, account.PayeeAccountId, 50000m, sourceType: DisbursementCodes.CommissionPayment), Manager, "manager-a");
        big.StatusCode.ShouldBe(HttpStatusCode.OK);
        bigBody.Text("sourceType").ShouldBe("BIL_COMMISSION");
        var (approve, _) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId = bigBody.Text("disbursementId"), decision = "APPROVE" }, Manager, "manager-b");
        approve.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A rejection needs a reason and leaves a Rejected disbursement that no longer blocks the key.
        var (second, secondBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, account.PayeeAccountId, 75m, sourceType: DisbursementCodes.CommissionPayment));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (noReason, _) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId = secondBody.Text("disbursementId"), decision = "REJECT" }, Manager, "manager-b");
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, _) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/approve-release", new { disbursementId = secondBody.Text("disbursementId"), decision = "REJECT", reason = "WRONG_AMOUNT" }, Manager, "manager-b");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BO_BIL_09_the_gates_fail_closed_source_purpose_screening_and_the_sources_with_their_own_approval()
    {
        var party = PartyId.New();
        var levy = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Levy);
        var refund = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Refund);

        // Sources that carry their own approval are not requested over REST (nobody supplies their own approval hash).
        var claim = DisbursementSlice.ClaimPayment(party, levy.PayeeAccountId, 10m);
        var (source, sourceBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", claim);
        source.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        sourceBody.Text("code").ShouldBe("BIL-ERR-SOURCE");

        // The account must be the payee's Active account of the source's purpose.
        var (wrongPurpose, wrongBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(party, refund.PayeeAccountId, 10m));
        wrongPurpose.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        wrongBody.Text("code").ShouldBe("BIL-ERR-PAYEE-ACCOUNT");
        var (otherParty, _) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", Levy(PartyId.New(), levy.PayeeAccountId, 10m));
        otherParty.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);

        // Shape: EUR, SEPA_CT, positive.
        var valid = Levy(party, levy.PayeeAccountId, 10m);
        (await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", valid with { Method = "SEPA_INSTANT" })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", valid with { Amount = new Money(10m, Currency.USD) })).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", valid with { Amount = new Money(-1m, Currency.EUR) })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", valid with { PayeeAccountId = null })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Sanctions screening that is not Clear, or unavailable, refuses the request (fail closed).
        _slice.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear() with { Result = ScreeningScreenResponse.ResultValue.PotentialHit, CaseNumber = "SCR-9" });
        var (blocked, blockedBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", valid);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        blockedBody.Text("code").ShouldBe("BIL-ERR-PAYEE-BLOCKED");
        _slice.Screening.Fail("pty.Screening.screen", new TimeoutException("down"));
        var (down, downBody) = await Rest(HttpMethod.Post, "/api/bil/v1/disbursements/request", valid);
        down.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        downBody.Text("code").ShouldBe("BIL-ERR-SCREENING-UNAVAILABLE");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_id = '{valid.SourceId}'")).ShouldBe(0);
    }

    [Fact]
    public async Task BO_BIL_09_an_in_process_tax_remittance_is_a_FIN_source_and_needs_authority_too()
    {
        var party = PartyId.New();
        var tax = await AccountAsync(party, PayeeAccountCreateRequest.PurposeValue.Tax);
        var request = Levy(party, tax.PayeeAccountId, 900m, sourceType: DisbursementCodes.TaxRemittance, sourceId: "IPT-2026-09");

        // Claims roles hold no BIL.DISBURSEMENT authority: refused, nothing stored.
        await using (var scope = _slice.Scope("Staff.ClaimsHandler"))
        {
            var error = await Should.ThrowAsync<CoreIns.Platform.Errors.DomainException>(() =>
                scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>()
                    .RequestAsync(request, CoreIns.Platform.Contracts.CommandOptions.New(), Ct));
            error.Error.Code.Value.ShouldBe("BIL-ERR-NOT-PERMITTED");
        }

        await using var billing = _slice.Scope(Billing);
        var response = await billing.ServiceProvider.GetRequiredService<IBillingDisbursementService>()
            .RequestAsync(request, CoreIns.Platform.Contracts.CommandOptions.New(), Ct);
        response.SourceModule.ShouldBe(ModuleCode.FIN);
        response.Status.ShouldBe(DisbursementRequestResponse.StatusValue.PendingApproval);

        // The same source id on a retry is a duplicate: one payment per business key (EC-08).
        var retry = await Should.ThrowAsync<CoreIns.Platform.Errors.DomainException>(() =>
            billing.ServiceProvider.GetRequiredService<IBillingDisbursementService>()
                .RequestAsync(request, CoreIns.Platform.Contracts.CommandOptions.New(), Ct));
        retry.Error.Code.Value.ShouldBe("BIL-ERR-DUPLICATE");
    }
}
