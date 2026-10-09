using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Bil.Credits;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Refunds;

/// <summary>
/// SL3-BIL-REFUND, refunds approved by rule (REQ-BIL-007, -181, -186, -187, -190; D-SL3-09, D-SL3-14): E2E-03 step 7 money.
/// A paid policy cancelled by the policyholder leaves credit on the account; within the (illustrative) auto limit and to an
/// unchanged payee the refund is approved by rule, screened, paid through the disbursement service (source BIL_REFUND) and
/// published as disbursed at ISSUED, with sealed ledger entries LA-02 → LA-12 → LA-13 → LA-10.
/// </summary>
public sealed class RefundAutoApprovalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await (await RefundHarness.StartAsync(database)).WithCreditAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task E2E_03_step_7_the_credit_is_refunded_by_rule_to_the_verified_account_and_paid_at_issued()
    {
        _h.Credit.ShouldBeGreaterThan(5m);
        _h.Credit.ShouldBeLessThanOrEqualTo(500m);
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);

        var refund = await _h.ProposedAsync();

        // REQ-BIL-190: approved by rule and paid at once (stub bank accepts and debits); ISSUED is the paid point.
        refund.Text("state").ShouldBe("PAID");
        refund.Text("approvalState").ShouldBe("NOT_REQUIRED");
        RefundHarness.Money(refund["amount"]).ShouldBe(_h.Credit);
        refund.Text("payee.payeePartyId").ShouldBe(_h.Scenario.Policy.PartyId);
        refund.Text("payee.maskedIban").ShouldBe("****" + _h.Iban[^4..]);
        refund.Text("payoutMethod").ShouldBe("SEPA_CT");
        refund["sourcePolicyIds"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe([_h.Scenario.Policy.PolicyId]);
        refund["breakdown"]!.AsArray().Sum(l => RefundHarness.Money(l!["amount"])).ShouldBe(_h.Credit);
        refund["breakdown"]!.AsArray().ShouldAllBe(l => l!["transactionKind"]!.GetValue<string>() == "CANCELLATION");
        refund["netting"]!.AsArray().Count.ShouldBe(0);
        refund.Text("disbursementId").ShouldNotBe("null");

        // The credit is used up and the account is square.
        (await _h.CreditOnAccountAsync()).ShouldBe(0m);

        // PRD-06 §4.13: LA-02 → LA-12 on approval (sourceType BIL_REFUND, sourceId = refund id, transactionKind REFUND on every line),
        // LA-12 → LA-13 on release, LA-13 → LA-10 on the statement debit.
        var id = refund.Text("refundId");
        var approved = await _h.LinesAsync("REFUND_APPROVED");
        approved.ShouldContain(l => l.StartsWith("LA-02 DEBIT", StringComparison.Ordinal));
        approved.ShouldContain(l => l.StartsWith("LA-12 CREDIT", StringComparison.Ordinal));
        approved.ShouldAllBe(l => l.EndsWith($" BIL_REFUND {id} REFUND", StringComparison.Ordinal));
        approved.Where(l => l.StartsWith("LA-12 CREDIT", StringComparison.Ordinal)).Sum(l => decimal.Parse(l.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(_h.Credit);
        var released = await _h.LinesAsync("DISBURSEMENT_RELEASED");
        released.ShouldBe([$"LA-12 DEBIT {_h.Credit:0.0000} BIL_REFUND {id} REFUND", $"LA-13 CREDIT {_h.Credit:0.0000} BIL_REFUND {id} REFUND"]);
        (await _h.LinesAsync("DISBURSEMENT_CLEARED")).ShouldBe([$"LA-13 DEBIT {_h.Credit:0.0000} BIL_REFUND {id} REFUND", $"LA-10 CREDIT {_h.Credit:0.0000} BIL_REFUND {id} REFUND"]);
        await _h.Slice.AllEntriesBalanceAsync();

        // The disbursement is the shared service's: source BIL_REFUND from BIL, the refund's own evidence and credit-set reference.
        var row = await _h.TextsAsync(
            $"SELECT source_module || '|' || source_type || '|' || source_id || '|' || state || '|' || approval_evidence_ref || '|' || (business_ref IS NOT NULL)::text FROM bil.disbursement WHERE source_id = '{id}'");
        row.ShouldBe([$"BIL|BIL_REFUND|{id}|CLEARED|BIL/RefundAuto/{id}|true"], "the stub bank accepts (ISSUED, which is the paid point) and debits at once (CLEARED)");
    }

    [Fact]
    public async Task REQ_BIL_190_RefundApproved_and_RefundDisbursed_are_published_with_the_ids_the_consumers_need_and_no_IBAN_is_exposed_anywhere()
    {
        var refund = await _h.ProposedAsync();
        await _h.Slice.DrainAsync();
        var id = refund.Text("refundId");

        var approved = (await _h.Slice.EnvelopesAsync(_h.AccountId, "RefundApproved")).Single();
        approved.Payload["refundId"]!.GetValue<string>().ShouldBe(id);
        approved.Payload["payeePartyId"]!.GetValue<string>().ShouldBe(_h.Scenario.Policy.PartyId);
        approved.BusinessKeys.Values.ShouldContain(id);
        var disbursed = (await _h.Slice.EnvelopesAsync(_h.AccountId, "RefundDisbursed")).Single();
        disbursed.Payload["refundId"]!.GetValue<string>().ShouldBe(id);
        disbursed.Payload["disbursementId"]!.GetValue<string>().ShouldBe(refund.Text("disbursementId"));
        disbursed.Payload["sourcePolicyIds"]!.AsArray().Count.ShouldBe(1);
        (await _h.Slice.EnvelopesAsync(_h.AccountId, "RefundRejected")).ShouldBeEmpty();

        // PITFALLS 18/20: the IBAN is encrypted at rest and appears nowhere else (events, ledger, audit, idempotency records, refund rows).
        (await _h.ExposuresAsync(_h.Iban)).ShouldBe(0);
        (await _h.ExposuresAsync(_h.Iban[^10..])).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_BIL_188_one_open_refund_per_account_and_a_second_proposal_after_payment_finds_no_credit()
    {
        var first = await _h.ProposedAsync();
        first.Text("state").ShouldBe("PAID");

        var (response, body) = await _h.ProposeAsync("alice");
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-NO-CREDIT");
    }

    [Fact]
    public async Task REQ_BIL_187_a_refund_needs_a_verified_REFUND_account_of_the_payer_and_the_client_cannot_name_another_payee()
    {
        // A second payer's account (another party) or a CLAIM_PAYMENT account is not the payer's REFUND account.
        var (created, other) = await _h.SendAsync(
            HttpMethod.Post, "/api/bil/v1/payee-accounts", new { partyId = Guid.NewGuid(), purpose = "REFUND", iban = DisbursementSlice.NewIban(), holderName = "Someone Else" },
            RefundUsers.Clerk, "alice");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, other?.ToJsonString());

        var (response, body) = await _h.ProposeAsync(
            "alice", extra: new { billingAccountId = _h.AccountId, reasonCode = RefundHarness.ReasonCode, payeeAccountId = other.Text("payeeAccountId") });
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PAYEE-ACCOUNT");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
    }

    [Fact]
    public async Task REQ_BIL_200_214_sanctions_screening_that_is_not_clear_stops_the_refund_and_nothing_moves()
    {
        _h.Screening.Setup("pty.Screening.screen", new CoreIns.Modules.Party.Contracts.Api.ScreeningScreenResponse
        {
            Result = CoreIns.Modules.Party.Contracts.Api.ScreeningScreenResponse.ResultValue.Blocked,
            ListVersions = ["EU-TEST-2026-10"],
            PaymentBlock = true,
        });

        var (response, body) = await _h.ProposeAsync("alice");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PAYEE-BLOCKED");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit); // rolled back as one transaction
        (await _h.Slice.ScalarAsync<long>($"SELECT count(*) FROM bil.refund WHERE billing_account_id = '{_h.AccountId}'")).ShouldBe(0);
        (await _h.LinesAsync("REFUND_APPROVED")).ShouldBeEmpty();
    }

    [Fact]
    public async Task PITFALLS_4_6_the_disbursement_service_pays_a_BIL_REFUND_only_as_the_stored_refund_was_approved()
    {
        await using var scope = CoreIns.IntegrationTests.Rating.RatingTestSupport.Scope(_h.Slice.Policy.Factory.Services, RefundUsers.Clerk);
        var disbursements = scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>();
        var forged = new DisbursementRequestRequest
        {
            SourceType = DisbursementCodes.RefundPayment,
            SourceId = Guid.NewGuid().ToString("D"),
            PayeePartyId = new PartyId(Guid.Parse(_h.Scenario.Policy.PartyId)),
            PayeeAccountId = Guid.Parse(_h.PayeeAccountId),
            Amount = new CoreIns.SharedKernel.Money(100m, CoreIns.SharedKernel.Currency.EUR),
            Method = DisbursementCodes.SepaCreditTransfer,
            ApprovalEvidenceRef = DisbursementApproval.RefundAutoEvidencePrefix + "forged",
            ApprovalContentHash = DisbursementContent.Hash(DisbursementCodes.RefundPayment, "x", new PartyId(Guid.NewGuid()), Guid.NewGuid(), new CoreIns.SharedKernel.Money(1m, CoreIns.SharedKernel.Currency.EUR)),
        };

        // A refund id BIL never created, however it is dressed up, is not paid (nothing is paid that was not approved).
        var error = await Should.ThrowAsync<CoreIns.Platform.Errors.DomainException>(() => disbursements.RequestAsync(forged, CommandOptions.New(), Ct));
        error.Error.Code.Value.ShouldBeOneOf("BIL-ERR-APPROVAL-MISMATCH", "BIL-ERR-VALIDATION");

        // A refund that was paid is not paid twice: the state is no longer APPROVED.
        var refund = await _h.ProposedAsync();
        var paid = forged with
        {
            SourceId = refund.Text("refundId"),
            Amount = new CoreIns.SharedKernel.Money(_h.Credit, CoreIns.SharedKernel.Currency.EUR),
            ApprovalEvidenceRef = DisbursementApproval.RefundAutoEvidencePrefix + refund.Text("refundId"),
            ApprovalContentHash = DisbursementContent.Hash(
                DisbursementCodes.RefundPayment, refund.Text("refundId"), new PartyId(Guid.Parse(_h.Scenario.Policy.PartyId)), Guid.Parse(_h.PayeeAccountId),
                new CoreIns.SharedKernel.Money(_h.Credit, CoreIns.SharedKernel.Currency.EUR)),
        };
        var twice = await Should.ThrowAsync<CoreIns.Platform.Errors.DomainException>(() => disbursements.RequestAsync(paid, CommandOptions.New(), Ct));
        twice.Error.Code.Value.ShouldBe("BIL-ERR-APPROVAL-MISMATCH");
        (await _h.Slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_type = 'BIL_REFUND' AND source_id = '{refund.Text("refundId")}'")).ShouldBe(1);
    }

    [Fact]
    public async Task PITFALLS_8_9_the_database_refuses_a_second_live_disbursement_for_the_same_credit_set_and_edits_to_a_refund()
    {
        var refund = await _h.ProposedAsync();
        var id = refund.Text("refundId");

        // Duplicate key on the business reference: the same payee account, amount and credit set (a superset of the source id would catch nothing).
        var copy = await Should.ThrowAsync<PostgresException>(() => _h.Slice.ScalarAsync<long>(
            $"""
            INSERT INTO bil.disbursement (disbursement_id, legal_entity_id, jurisdiction, disbursement_number, source_module, source_type, source_id, payee_party_id, payee_account_id,
                amount, currency, method, approval_evidence_ref, approval_content_hash, state, screening_result, screened_at, vop_result, requested_at, created_by, record_version, business_ref)
            SELECT gen_random_uuid(), legal_entity_id, jurisdiction, 'DX-1', source_module, source_type, gen_random_uuid()::text, payee_party_id, payee_account_id,
                amount, currency, method, approval_evidence_ref, approval_content_hash, 'APPROVED', screening_result, screened_at, vop_result, requested_at, created_by, 1, business_ref
            FROM bil.disbursement WHERE source_id = '{id}' RETURNING 1
            """));
        copy.ConstraintName.ShouldBe("ux_disbursement_business_ref");

        // As the app role: the refund's money and payee are frozen (BL004), a Paid refund is final, nothing is deleted (BL002).
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var amount = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand($"UPDATE bil.refund SET amount = amount + 1 WHERE refund_id = '{id}'");
            await command.ExecuteNonQueryAsync(Ct);
        });
        amount.SqlState.ShouldBe("BL004");
        var state = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand($"UPDATE bil.refund SET state = 'PENDING_APPROVAL', approval_state = 'PENDING' WHERE refund_id = '{id}'");
            await command.ExecuteNonQueryAsync(Ct);
        });
        state.SqlState.ShouldBe("BL004");
        var delete = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand($"DELETE FROM bil.refund WHERE refund_id = '{id}'");
            await command.ExecuteNonQueryAsync(Ct);
        });
        delete.SqlState.ShouldBeOneOf("BL002", "42501"); // the app role has no DELETE grant at all; the trigger refuses it for every other role
        var lines = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = app.CreateCommand($"UPDATE bil.refund_credit SET amount = amount + 1 WHERE refund_id = '{id}'");
            await command.ExecuteNonQueryAsync(Ct);
        });
        lines.SqlState.ShouldBeOneOf("BL002", "42501");
    }
}

/// <summary>
/// SL3-BIL-REFUND, netting (REQ-BIL-182, REQ-BIL-184): the credit is first set against the account's open debit items and only the rest is
/// refunded; the breakdown shows the netting. The credit transfer moves no cash and posts no ledger entry (both sides are in LA-02).
/// </summary>
public sealed class RefundNettingTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await (await RefundHarness.StartAsync(database)).WithNettingAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_182_open_debit_is_netted_against_the_credit_before_the_refund()
    {
        _h.DebitOpen.ShouldBeGreaterThan(0m);
        var (response, body) = await _h.ProposeAsync("alice");
        if (_h.Credit - _h.DebitOpen < 5m)
        {
            // Not enough credit is left above the debit: nothing is refunded and nothing is netted by the refund command.
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var refund = body!["refund"]!;
        refund.Text("state").ShouldBe("PAID");
        RefundHarness.Money(refund["amount"]).ShouldBe(_h.Credit - _h.DebitOpen);
        var netting = refund["netting"]!.AsArray().ShouldHaveSingleItem()!;
        netting.Text("kind").ShouldBe("OPEN_INVOICE");
        netting.Text("invoiceId").ShouldBe(_h.SecondInvoiceId);
        RefundHarness.Money(netting["amount"]).ShouldBe(_h.DebitOpen);

        // Invoice 2 is settled by the credit (no cash), the account is square, and only the refunded part reached LA-12.
        var second = (await _h.Scenario.DocumentsAsync()).Single(d => d.Text("invoice.invoiceId") == _h.SecondInvoiceId);
        RefundHarness.Money(second["invoice"]!["open"]).ShouldBe(0m);
        second.Text("invoice.state").ShouldBe("PAID");
        (await _h.CreditOnAccountAsync()).ShouldBe(0m);
        (await _h.LinesAsync("REFUND_APPROVED")).Where(l => l.StartsWith("LA-12 CREDIT", StringComparison.Ordinal))
            .Sum(l => decimal.Parse(l.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(_h.Credit - _h.DebitOpen);
        (await _h.Slice.ScalarAsync<long>($"SELECT count(*) FROM bil.credit_application WHERE refund_id = '{refund.Text("refundId")}' AND target_kind = 'NETTING'")).ShouldBeGreaterThan(0);
        await _h.Slice.AllEntriesBalanceAsync();
    }
}
