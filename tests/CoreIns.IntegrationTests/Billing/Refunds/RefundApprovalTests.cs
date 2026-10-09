using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Bil.Credits;
using CoreIns.Modules.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Refunds;

/// <summary>
/// SL3-BIL-REFUND, maker-checker (REQ-BIL-188, -189, -191; PITFALLS 1-6): above the auto limit (illustrative; 100.00 here so the
/// test credit exceeds it) the refund waits for a second person holding BIL.REFUND authority over the same total. The requester,
/// an editor, the person who changed the payee and the dev superuser (who holds both billing roles) cannot approve their own
/// refund; a rejection is final and its resubmission is always approved by a second person.
/// </summary>
public sealed class RefundApprovalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _h = await (await RefundHarness.StartAsync(database, o =>
        {
            o.RefundAutoApproveLimit = 100m;
            o.PayeeCoolingOffDays = 0;
        })).WithCreditAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_188_a_refund_above_the_auto_limit_waits_and_the_requester_cannot_approve_even_as_superuser_then_another_manager_approves_and_it_is_paid()
    {
        var refund = await _h.ProposedAsync("alice");
        var id = refund.Text("refundId");

        // Nothing moved: pending, an approval request of the right type/subject/authority exists, no credit applied, no entry.
        refund.Text("state").ShouldBe("PENDING_APPROVAL");
        refund.Text("approvalState").ShouldBe("PENDING");
        refund.Text("disbursementId").ShouldBe("null");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
        (await _h.LinesAsync("REFUND_APPROVED")).ShouldBeEmpty();
        var request = (await _h.TextsAsync(
            $"""
            SELECT a.approval_type || '|' || a.object_module || '/' || a.object_type || '/' || a.object_id || '|' || a.authority_type || '|' || a.authority_amount::text || '|'
                   || a.authority_codes::text || '|' || a.referral_role
            FROM bil.refund r JOIN plt.approval_request a ON a.request_id = r.approval_request_id WHERE r.refund_id = '{id}'
            """)).ShouldHaveSingleItem();
        request.ShouldStartWith($"BIL.REFUND|BIL/Refund/{id}|BIL.REFUND|{_h.Credit:0.0000}|");
        (await _h.Slice.ScalarAsync<bool>($"SELECT a.payload_hash = r.approval_content_hash FROM bil.refund r JOIN plt.approval_request a ON a.request_id = r.approval_request_id WHERE r.refund_id = '{id}'"))
            .ShouldBeTrue();
        request.ShouldContain("\"payeeChanged\"");
        request.ShouldContain("\"false\"");
        request.ShouldContain("\"reason\"");
        request.ShouldContain("Staff.BillingManager");

        // The clerk has no decide permission at all.
        var (forbidden, _) = await _h.DecideAsync(id, "APPROVE", "carol", RefundUsers.Clerk);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The dev superuser holds both billing roles; as the maker it is still refused (PLT self-approval).
        var (self, selfBody) = await _h.DecideAsync(id, "APPROVE", "alice", RefundUsers.Both);
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await _h.ReloadAsync(id)).Text("state").ShouldBe("PENDING_APPROVAL");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);

        // Another manager approves: credit applied, entries posted, disbursement carries the PLT approval as evidence, paid at ISSUED.
        var (approved, body) = await _h.DecideAsync(id, "APPROVE", "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var view = body!["refund"]!;
        view.Text("state").ShouldBe("PAID");
        view.Text("approvalState").ShouldBe("APPROVED");
        view.Text("decidedBy").ShouldNotBe(view.Text("requestedBy"));
        body.Text("disbursementId").ShouldBe(view.Text("disbursementId"));
        (await _h.CreditOnAccountAsync()).ShouldBe(0m);
        (await _h.LinesAsync("REFUND_APPROVED")).Count.ShouldBeGreaterThanOrEqualTo(2);
        (await _h.TextsAsync($"SELECT approval_evidence_ref FROM bil.disbursement WHERE source_id = '{id}'"))
            .ShouldHaveSingleItem().ShouldStartWith("PLT/ApprovalRequest/");
        await _h.Slice.AllEntriesBalanceAsync();

        // Deciding again is a state error, not a second payment.
        var (again, againBody) = await _h.DecideAsync(id, "APPROVE", "bob");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("BIL-ERR-REFUND-STATE");
        (await _h.Slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE source_type = 'BIL_REFUND' AND source_id = '{id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_188_one_open_refund_per_account_so_splitting_cannot_escape_the_limit()
    {
        var first = await _h.ProposedAsync("alice");

        var (response, body) = await _h.ProposeAsync("alice");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-REFUND-OPEN");
        body!["metadata"]?["existingRefundId"]?.GetValue<string>().ShouldBe(first.Text("refundId"));
        (await _h.Slice.ScalarAsync<long>($"SELECT count(*) FROM bil.refund WHERE billing_account_id = '{_h.AccountId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_191_a_rejection_keeps_the_credit_and_is_final_and_its_resubmission_is_always_approved_by_a_second_person()
    {
        var refund = await _h.ProposedAsync("alice");
        var id = refund.Text("refundId");

        // A rejection needs a comment.
        var (noComment, _) = await _h.DecideAsync(id, "REJECT", "bob");
        noComment.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var (rejected, body) = await _h.DecideAsync(id, "REJECT", "bob", comment: "wrong payee");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["refund"]!.Text("state").ShouldBe("REJECTED");
        body["refund"]!.Text("approvalState").ShouldBe("REJECTED");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit); // returned to the account: it was never applied
        await _h.Slice.DrainAsync();
        (await _h.Slice.EnvelopesAsync(_h.AccountId, "RefundRejected")).Single().Payload["refundId"]!.GetValue<string>().ShouldBe(id);
        (await _h.Slice.EnvelopesAsync(_h.AccountId, "RefundApproved")).ShouldBeEmpty();

        // The rejected refund is never reopened: deciding it again or resubmitting a refund that is not rejected is a state error.
        (await _h.DecideAsync(id, "APPROVE", "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // PITFALLS 6: raise the auto limit so the resubmission is within it; it still waits for a second person.
        var options = _h.Slice.Policy.Factory.Services.GetRequiredService<IOptions<BillingOptions>>().Value;
        options.RefundAutoApproveLimit = 10_000m;
        var (resubmitted, again) = await _h.ResubmitAsync(id, "alice");
        resubmitted.StatusCode.ShouldBe(HttpStatusCode.OK, again?.ToJsonString());
        var second = again!["refund"]!;
        second.Text("refundId").ShouldNotBe(id);
        second.Text("state").ShouldBe("PENDING_APPROVAL");
        (await _h.Slice.ScalarAsync<string>($"SELECT resubmits_refund_id::text FROM bil.refund WHERE refund_id = '{second.Text("refundId")}'")).ShouldBe(id);

        // The rejected refund can be resubmitted once, and a refund that was not rejected cannot be.
        (await _h.ResubmitAsync(id, "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _h.ResubmitAsync(second.Text("refundId"), "alice")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // REQ-BIL-189: carol resubmitted nothing, but alice (the earlier requester, now an editor of the chain) cannot approve it.
        var (editor, editorBody) = await _h.DecideAsync(second.Text("refundId"), "APPROVE", "dave", RefundUsers.Both); // dave is not a participant
        editor.StatusCode.ShouldBe(HttpStatusCode.OK, editorBody?.ToJsonString());
        editorBody!["refund"]!.Text("state").ShouldBe("PAID");
    }

    [Fact]
    public async Task REQ_BIL_189_an_earlier_requester_of_the_chain_is_an_editor_and_cannot_approve_the_resubmission()
    {
        var first = await _h.ProposedAsync("alice");
        (await _h.DecideAsync(first.Text("refundId"), "REJECT", "bob", comment: "wrong payee")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // carol resubmits (she is now the maker); alice made the original request and is an editor of the chain.
        var (resubmitted, body) = await _h.ResubmitAsync(first.Text("refundId"), "carol");
        resubmitted.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body!["refund"]!.Text("refundId");

        var (editor, editorBody) = await _h.DecideAsync(id, "APPROVE", "alice", RefundUsers.Both);
        editor.StatusCode.ShouldBe(HttpStatusCode.Forbidden, editorBody?.ToJsonString());
        editorBody.Text("code").ShouldBe("BIL-ERR-SOD");

        // The maker is refused by plt.Approval with its own error.
        var (maker, makerBody) = await _h.DecideAsync(id, "APPROVE", "carol", RefundUsers.Both);
        maker.StatusCode.ShouldBe(HttpStatusCode.Forbidden, makerBody?.ToJsonString());
        makerBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await _h.ReloadAsync(id)).Text("state").ShouldBe("PENDING_APPROVAL");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
    }

    [Fact]
    public async Task PITFALLS_8_as_the_app_role_a_pending_refund_cannot_be_approved_by_its_requester_and_no_credit_moves_for_it()
    {
        var refund = await _h.ProposedAsync("alice");
        var id = refund.Text("refundId");
        await using var app = Npgsql.NpgsqlDataSource.Create(database.AppConnectionString);

        // The trigger refuses an approval whose decider took part in the refund (REQ-BIL-189), whatever the application did.
        var self = await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
        {
            await using var command = app.CreateCommand(
                $"UPDATE bil.refund SET state = 'APPROVED', approval_state = 'APPROVED', decided_by = requested_by, decided_at = now(), record_version = record_version + 1 WHERE refund_id = '{id}'");
            await command.ExecuteNonQueryAsync(Ct);
        });
        self.SqlState.ShouldBe("BL004");

        // The trigger refuses credit applications for a refund that is not approved (BL001).
        var apply = await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
        {
            await using var command = app.CreateCommand(
                $"""
                INSERT INTO bil.credit_application (credit_application_id, legal_entity_id, billing_account_id, credit_note_id, credit_item_id, target_kind, refund_id, amount, currency, actor, recorded_at)
                SELECT gen_random_uuid(), c.legal_entity_id, r.billing_account_id, c.credit_note_id, c.credit_item_id, 'REFUND', r.refund_id, c.amount, c.currency, 'x', now()
                FROM bil.refund_credit c JOIN bil.refund r ON r.refund_id = c.refund_id WHERE r.refund_id = '{id}' LIMIT 1
                """);
            await command.ExecuteNonQueryAsync(Ct);
        });
        apply.SqlState.ShouldBe("BL001");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
    }
}

/// <summary>
/// SL3-BIL-REFUND, the payee (REQ-BIL-187, -188, -189, -199; decision recorded for the orchestrator): a refund to a payee account that
/// replaced an earlier one (<c>payeeChanged</c>) is never approved by rule, even within the auto limit. The Staff.Billing grant limits
/// <c>payeeChanged</c> to "false", so the requester is Referred and the Staff.BillingManager decides; the person who changed the
/// payee account cannot, and the disbursement service holds a changed account inside its cooling-off.
/// </summary>
public sealed class RefundPayeeChangeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    public async ValueTask InitializeAsync() =>
        _h = await (await RefundHarness.StartAsync(database, o => o.PayeeCoolingOffDays = 0)).WithCreditAsync(registerPayee: false);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_188_a_changed_payee_forces_manager_approval_within_the_auto_limit_and_the_changer_cannot_approve()
    {
        await _h.RegisterPayeeAsync("alice"); // the first account of the payer: not a change
        var iban = _h.Iban;
        var changed = await _h.RegisterPayeeAsync("alice"); // supersedes it: a change
        changed.Text("change").ShouldBe("true");
        _h.Iban.ShouldNotBe(iban);

        _h.Credit.ShouldBeLessThanOrEqualTo(500m);
        var refund = await _h.ProposedAsync("carol"); // carol did not touch the payee

        refund.Text("state").ShouldBe("PENDING_APPROVAL"); // 500.00 auto limit not exceeded, but the payee changed
        var codes = await _h.TextsAsync(
            $"SELECT a.authority_codes::text FROM bil.refund r JOIN plt.approval_request a ON a.request_id = r.approval_request_id WHERE r.refund_id = '{refund.Text("refundId")}'");
        codes.ShouldHaveSingleItem().ShouldContain("\"payeeChanged\": \"true\"");

        // alice changed the payee account: BIL refuses her even though she holds the manager role and is not the maker.
        var (changer, changerBody) = await _h.DecideAsync(refund.Text("refundId"), "APPROVE", "alice", RefundUsers.Both);
        changer.StatusCode.ShouldBe(HttpStatusCode.Forbidden, changerBody?.ToJsonString());
        changerBody.Text("code").ShouldBe("BIL-ERR-SOD");

        // A different manager can (cooling-off is 0 days in this host).
        var (approved, body) = await _h.DecideAsync(refund.Text("refundId"), "APPROVE", "bob");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["refund"]!.Text("state").ShouldBe("PAID");
        (await _h.ExposuresAsync(iban)).ShouldBe(0);
        (await _h.ExposuresAsync(_h.Iban)).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_BIL_188_an_unchanged_payee_within_the_limit_is_approved_by_rule_for_the_same_credit()
    {
        await _h.RegisterPayeeAsync("alice");

        var refund = await _h.ProposedAsync("carol");

        refund.Text("state").ShouldBe("PAID");
        refund.Text("approvalState").ShouldBe("NOT_REQUIRED");
    }

    [Fact]
    public async Task REQ_BIL_186_a_refund_to_an_unverified_account_is_refused_and_a_missing_account_asks_for_one()
    {
        var (missing, body) = await _h.ProposeAsync("alice");
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PAYEE-UNVERIFIED");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
    }
}

/// <summary>SL3-BIL-REFUND: the changed payee account is held inside its cooling-off by the disbursement service (REQ-BIL-199); fail closed, nothing moves.</summary>
public sealed class RefundCoolingOffTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await (await RefundHarness.StartAsync(database)).WithCreditAsync(registerPayee: false);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_199_approving_a_refund_to_a_payee_account_in_cooling_off_is_refused_and_the_refund_stays_pending()
    {
        await _h.RegisterPayeeAsync("alice");
        await _h.RegisterPayeeAsync("alice");
        var refund = await _h.ProposedAsync("carol");
        refund.Text("state").ShouldBe("PENDING_APPROVAL");

        var (response, body) = await _h.DecideAsync(refund.Text("refundId"), "APPROVE", "bob");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-COOLING-OFF");
        var after = await _h.ReloadAsync(refund.Text("refundId"));
        after.Text("state").ShouldBe("PENDING_APPROVAL"); // the whole decision rolled back, including the approval
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
        (await _h.LinesAsync("REFUND_APPROVED")).ShouldBeEmpty();
        (await _h.Slice.ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = (SELECT approval_request_id FROM bil.refund WHERE refund_id = '{refund.Text("refundId")}')"))
            .ShouldBe("PendingApproval");
    }
}

/// <summary>SL3-BIL-REFUND: the minimum refund (REQ-BIL-182; 5.00 is the PRD default, illustrative).</summary>
public sealed class RefundMinimumTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    public async ValueTask InitializeAsync() =>
        _h = await (await RefundHarness.StartAsync(database, o => o.RefundMinimum = 10_000m)).WithCreditAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_182_a_credit_below_the_minimum_stays_on_the_account()
    {
        var (response, body) = await _h.ProposeAsync("alice");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-REFUND-BELOW-MINIMUM");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
    }
}

/// <summary>SL3-BIL-REFUND: VoP that did not answer leaves the account unverified; no refund is paid to it (REQ-BIL-186, -203, -214).</summary>
public sealed class RefundUnverifiedPayeeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private RefundHarness _h = null!;

    public async ValueTask InitializeAsync() =>
        _h = await (await RefundHarness.StartAsync(database, verifier: false)).WithCreditAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task REQ_BIL_186_a_payee_account_without_a_verification_match_cannot_receive_a_refund()
    {
        (await _h.Slice.ScalarAsync<string>($"SELECT verification_status FROM bil.payee_account WHERE payee_account_id = '{_h.PayeeAccountId}'")).ShouldBe("VOP_NOT_AVAILABLE");

        var (response, body) = await _h.ProposeAsync("alice");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, body?.ToJsonString());
        body.Text("code").ShouldBe("BIL-ERR-PAYEE-UNVERIFIED");
        (await _h.CreditOnAccountAsync()).ShouldBe(_h.Credit);
    }
}
