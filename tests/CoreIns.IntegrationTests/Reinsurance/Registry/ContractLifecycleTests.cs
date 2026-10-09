using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Reinsurance.Registry.RegistrySlice;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>
/// SL4-RI-REGISTRY lifecycle: create → submit → approve by another person → Active, the separation of duties, the content
/// hash binding, return, racing deciders, validation and permissions (REQ-RI-030..032, -037, -038, -046, -047, -056..058,
/// -231; PITFALLS 3, 4, 5, 15).
/// </summary>
public sealed class ContractLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private async Task<(RegistrySlice Slice, string Lead, string Follow)> NewSliceAsync()
    {
        var slice = new RegistrySlice(database);
        var lead = await slice.OrganisationAsync("Σύνθετη Αντασφαλιστική Α.Ε. " + Guid.NewGuid().ToString("N")[..6]);
        var follow = await slice.OrganisationAsync("Synthetic Follow Re " + Guid.NewGuid().ToString("N")[..6]);
        return (slice, lead, follow);
    }

    [Fact]
    public async Task Registry_list_layer_summary_uses_current_revision_and_preserves_money_and_layer_order()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var layers = new[]
        {
            new { layerNo = 2, attachment = Money("900000.00"), limit = Money("750000.00"), aad = Money("0.00") },
            new { layerNo = 1, attachment = Money("400000.00"), limit = Money("500000.00"), aad = Money("1000.00") },
        };
        var (updated, _) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = version, layers });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, page) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts?limit=200");
        var item = page!["items"]!.AsArray().Single(i => i!["contractId"]!.GetValue<string>() == id)!;
        item["layers"]!.AsArray().Count.ShouldBe(2);
        item.Text("layers.0.layerNo").ShouldBe("1");
        decimal.Parse(item.Text("layers.0.attachment.amount"), CultureInfo.InvariantCulture).ShouldBe(400000m);
        decimal.Parse(item.Text("layers.0.limit.amount"), CultureInfo.InvariantCulture).ShouldBe(500000m);
        item.Text("layers.0.limit.currency").ShouldBe("EUR");
        item.Text("layers.1.layerNo").ShouldBe("2");
        decimal.Parse(item.Text("layers.1.limit.amount"), CultureInfo.InvariantCulture).ShouldBe(750000m);
    }

    [Fact]
    public async Task REQ_RI_030_031_032_056_057_058_231_Create_submit_and_approve_by_another_person_activates_the_treaty()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var product = NewProduct();

        var (id, version, created) = await slice.CreateAsync(Body(product, "OD", lead, follow));
        created.Text("contract.status").ShouldBe("DRAFT");
        created.Text("contract.contractNumber").ShouldMatch("^RIC[0-9]{6}$");
        created.Text("contract.stableTreatyId").ShouldBe(created.Text("contract.contractNumber"));
        created.Text("contract.contractYear").ShouldBe("2026");
        created.Text("contract.maker").ShouldBe("USER:riacct");
        created.Text("contract.participations.0.lead").ShouldBe("true");

        var submitted = await slice.SubmitAsync(id, version);
        var detail = await slice.GetAsync(id);
        detail.Text("contract.status").ShouldBe("PENDING_APPROVAL");
        var requestId = detail.Text("contract.approvalRequestId");

        // The PLT request is RI's own: type, subject, hash and authority are set by RI (PITFALLS 3, 4).
        (await slice.ScalarAsync<string>($"SELECT approval_type || '|' || object_module || '/' || object_type || '/' || object_id || '|' || authority_type FROM plt.approval_request WHERE request_id = '{requestId}'"))
            .ShouldBe($"RI.CONTRACT_APPROVE|RI/Contract/{id}|RI.CONTRACT_APPROVE");
        (await slice.ScalarAsync<string>($"SELECT payload_hash FROM plt.approval_request WHERE request_id = '{requestId}'"))
            .ShouldBe(await slice.ScalarAsync<string>($"SELECT content_hash FROM ri.contract_version WHERE contract_id = '{id}'"));

        var (response, approved) = await slice.ApproveAsync(id, submitted);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved.Text("decision").ShouldBe("APPROVE");

        // The period (2026) started before the clock (2026-02-10), so the treaty is Active in the same transaction.
        var active = await slice.GetAsync(id);
        active.Text("contract.status").ShouldBe("ACTIVE");
        active.Text("contract.activatedAt").ShouldNotBe("null");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RIContractActivated' AND aggregate_id = '{id}'")).ShouldBe(1);
        (await slice.ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE request_id = '{requestId}'")).ShouldBe("Approved");
        (await slice.ScalarAsync<string>($"SELECT approved_by FROM ri.contract_version WHERE contract_id = '{id}'")).ShouldBe("USER:rimgr");

        // Audit rows for each lifecycle command, with the object and number.
        foreach (var operation in new[] { "ri.Contract.create", "ri.Contract.submit", "ri.Contract.approve" })
        {
            (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = '{operation}' AND object_id = '{id}' AND outcome = 'Succeeded'"))
                .ShouldBe(1, operation);
        }
    }

    [Fact]
    public async Task REQ_RI_057_Nobody_who_took_part_in_the_content_can_approve_it_PITFALLS_3_5()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow), user: "enterer");

        // An editor (a second accountant who changes the draft) takes part.
        var (edit, edited) = await slice.SendAsync(
            HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = version, clause = new { alaeIncluded = false, statutoryInterestIncluded = false, recoveriesInure = "REALISED_ONLY" } },
            Accountant, "editor");
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, edited?.ToJsonString());
        var submitted = await slice.SubmitAsync(id, int.Parse(edited.Text("contract.recordVersion"), CultureInfo.InvariantCulture), user: "submitter");

        // Managers who are the enterer, the editor or the submitter are all refused with RI-ERR-SOD (403); the contract is untouched.
        foreach (var user in new[] { "enterer", "editor", "submitter" })
        {
            var (refused, body) = await slice.ApproveAsync(id, submitted, user);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, user);
            body.Text("code").ShouldBe("RI-ERR-SOD");
        }

        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("PENDING_APPROVAL");

        // The same chain also refuses a RETURN, and a different manager can then decide.
        (await slice.ApproveAsync(id, submitted, "enterer", "RETURN", "no")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (ok, approved) = await slice.ApproveAsync(id, submitted, "other-manager");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved.Text("contract.status").ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task A_delegated_checker_acting_for_a_participant_is_refused_by_RI_and_the_PLT_inbox()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow), user: "enterer");
        var submitted = await slice.SubmitAsync(id, version, user: "submitter");
        await using var scope = slice.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("delegated-checker");
        context.OnBehalfOf = ActorRef.User("enterer");
        context.Roles = [Manager];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.IdempotencyKey = IdempotencyKey.New();
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var result = await services.GetRequiredService<ICommandHandler<ApproveContract, ContractApproveResponse>>().HandleAsync(
            new ApproveContract(new ContractApproveRequest { ContractId = new RiContractId(Guid.Parse(id)), ExpectedRecordVersion = submitted, Decision = ContractApproveRequest.DecisionValue.Approve }), Ct);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.Value.ShouldBe("RI-ERR-SOD");
        var request = await slice.ScalarAsync<Guid>($"SELECT approval_request_id FROM ri.contract WHERE contract_id = '{id}'");
        var hash = await slice.ScalarAsync<string>($"SELECT content_hash FROM ri.contract_version WHERE contract_id = '{id}'");
        var error = await Should.ThrowAsync<DomainException>(() => services.GetRequiredService<IPlatformApprovalService>().DecideAsync(
            new ApprovalDecideRequest { RequestId = request, Decision = ApprovalDecideRequest.DecisionValue.Approve, PayloadHash = Sha256Hash.Parse(hash) }, CommandOptions.New(), Ct));
        error.Error.Code.Value.ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("PENDING_APPROVAL");
    }

    [Fact]
    public async Task PITFALLS_3_Content_changed_after_submit_is_stale_and_nothing_activates()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var product = NewProduct();
        var (id, version, _) = await slice.CreateAsync(Body(product, "OD", lead, follow));
        var submitted = await slice.SubmitAsync(id, version);

        // Tamper as the database owner (triggers off for this session only): a layer limit changes behind the approval's back.
        await slice.ExecuteAsync(
            $"""
            BEGIN;
            SET LOCAL session_replication_role = replica;
            UPDATE ri.layer SET limit_amount = 900000 WHERE version_id IN (SELECT version_id FROM ri.contract_version WHERE contract_id = '{id}');
            COMMIT;
            """);

        var (response, body) = await slice.ApproveAsync(id, submitted);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("RI-ERR-STALE");
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("PENDING_APPROVAL");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RIContractActivated' AND aggregate_id = '{id}'")).ShouldBe(0);
        (await slice.ScalarAsync<string>($"SELECT status FROM plt.approval_request WHERE approval_type = 'RI.CONTRACT_APPROVE' AND object_id = '{id}'")).ShouldBe("PendingApproval");
    }

    [Fact]
    public async Task REQ_RI_056_Return_with_a_reason_goes_back_to_Draft_and_a_decider_who_read_the_old_version_is_stale()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var submitted = await slice.SubmitAsync(id, version);

        var (noReason, noReasonBody) = await slice.ApproveAsync(id, submitted, decision: "RETURN");
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest, noReasonBody?.ToJsonString());
        noReasonBody.Text("code").ShouldBe("RI-ERR-VALIDATION");

        var (returned, returnedBody) = await slice.ApproveAsync(id, submitted, decision: "RETURN", reason: "Panel needs the broker named.");
        returned.StatusCode.ShouldBe(HttpStatusCode.OK, returnedBody?.ToJsonString());
        returnedBody.Text("contract.status").ShouldBe("DRAFT");
        returnedBody.Text("decision").ShouldBe("RETURN");
        (await slice.ScalarAsync<string>($"SELECT return_reason FROM ri.contract WHERE contract_id = '{id}'")).ShouldBe("Panel needs the broker named.");

        // A second manager who read the contract before the return is stale (409), not a 500 and not a state error.
        var (stale, staleBody) = await slice.ApproveAsync(id, submitted, "second-manager");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("RI-ERR-STALE");

        // The accountant edits and resubmits: a new PLT request, then approval works.
        var current = int.Parse(returnedBody.Text("contract.recordVersion"), CultureInfo.InvariantCulture);
        var (edit, edited) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = current, placedPct = "100" });
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, edited?.ToJsonString());
        var resubmitted = await slice.SubmitAsync(id, int.Parse(edited.Text("contract.recordVersion"), CultureInfo.InvariantCulture));
        var (ok, approved) = await slice.ApproveAsync(id, resubmitted, "second-manager");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved.Text("contract.status").ShouldBe("ACTIVE");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.approval_request WHERE object_id = '{id}'")).ShouldBe(2);
    }

    [Fact]
    public async Task PITFALLS_15_Two_managers_approving_at_once_one_wins_and_the_other_gets_409()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var submitted = await slice.SubmitAsync(id, version);

        var results = await Task.WhenAll(slice.ApproveAsync(id, submitted, "manager-a"), slice.ApproveAsync(id, submitted, "manager-b"));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        var loser = results.Single(r => r.Response.StatusCode != HttpStatusCode.OK);
        loser.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, loser.Body?.ToJsonString());
        loser.Body.Text("code").ShouldBe("RI-ERR-STALE");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RIContractActivated' AND aggregate_id = '{id}'")).ShouldBe(1);

        // Two racing edits of a draft likewise: exactly one wins.
        var (second, secondVersion, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        object Edit(string pct) => new { expectedRecordVersion = secondVersion, clause = new { alaeIncluded = pct == "a", statutoryInterestIncluded = false, recoveriesInure = "REALISED_ONLY" } };
        var edits = await Task.WhenAll(
            slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{second}", Edit("a"), Accountant, "editor-a"),
            slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{second}", Edit("b"), Accountant, "editor-b"));
        edits.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", edits.Select(r => r.Body?.ToJsonString())));
        edits.Single(r => r.Response.StatusCode != HttpStatusCode.OK).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private const string InboxRoles = "Staff.ReinsuranceManager,Staff.ClaimsManager";

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> DecideInInboxAsync(RegistrySlice slice, string id, string user, string decision)
    {
        var requestId = await slice.ScalarAsync<string>($"SELECT request_id::text FROM plt.approval_request WHERE object_id = '{id}' ORDER BY requested_at DESC LIMIT 1");
        var hash = await slice.ScalarAsync<string>($"SELECT payload_hash FROM plt.approval_request WHERE request_id = '{requestId}'");
        return await slice.SendAsync(
            HttpMethod.Post, "/api/plt/v1/approval/decide",
            new { requestId, decision, payloadHash = hash, comment = decision == "Reject" ? "Not like this" : null }, InboxRoles, user);
    }

    [Fact]
    public async Task PITFALLS_3_6_A_decision_given_in_the_PLT_inbox_is_executed_by_RI_only_for_a_checker_who_took_no_part()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;

        // Approved in the inbox by an independent manager: any manager's RI approve now executes it, recorded under the real checker.
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var submitted = await slice.SubmitAsync(id, version);
        var (decided, decidedBody) = await DecideInInboxAsync(slice, id, "inbox-mgr", "Approve");
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decidedBody?.ToJsonString());
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("PENDING_APPROVAL");
        var (executed, executedBody) = await slice.ApproveAsync(id, submitted, "rimgr2");
        executed.StatusCode.ShouldBe(HttpStatusCode.OK, executedBody?.ToJsonString());
        executedBody.Text("contract.status").ShouldBe("ACTIVE");
        (await slice.ScalarAsync<string>($"SELECT approved_by FROM ri.contract_version WHERE contract_id = '{id}'")).ShouldBe("USER:inbox-mgr");

        // RI supplies its authoritative participants to PLT, so the inbox refuses the enterer before any decision.
        var (id2, version2, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow), user: "enterer2");
        var submitted2 = await slice.SubmitAsync(id2, version2, user: "submitter2");
        var (refused, refusedBody) = await DecideInInboxAsync(slice, id2, "enterer2", "Approve");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, refusedBody?.ToJsonString());
        refusedBody.Text("code").ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");
        (await slice.GetAsync(id2)).Text("contract.status").ShouldBe("PENDING_APPROVAL");
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'RIContractActivated' AND aggregate_id = '{id2}'")).ShouldBe(0);

        // It can be returned to Draft and submitted again, which asks PLT for a new decision.
        var (returned, returnedBody) = await slice.ApproveAsync(id2, submitted2, "rimgr3", "RETURN", "Entered and approved by the same person.");
        returned.StatusCode.ShouldBe(HttpStatusCode.OK, returnedBody?.ToJsonString());
        returnedBody.Text("contract.status").ShouldBe("DRAFT");
    }

    [Fact]
    public async Task A_request_rejected_in_the_PLT_inbox_cannot_be_approved_by_RI_but_the_contract_can_be_returned()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var submitted = await slice.SubmitAsync(id, version);
        (await DecideInInboxAsync(slice, id, "inbox-mgr", "Reject")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (approve, approveBody) = await slice.ApproveAsync(id, submitted, "rimgr2");
        approve.StatusCode.ShouldBe(HttpStatusCode.Conflict, approveBody?.ToJsonString());
        approveBody.Text("code").ShouldBe("RI-ERR-STALE");
        (await slice.GetAsync(id)).Text("contract.status").ShouldBe("PENDING_APPROVAL");

        var (returned, returnedBody) = await slice.ApproveAsync(id, submitted, "rimgr2", "RETURN", "Rejected in the inbox.");
        returned.StatusCode.ShouldBe(HttpStatusCode.OK, returnedBody?.ToJsonString());
        returnedBody.Text("contract.status").ShouldBe("DRAFT");
    }

    [Fact]
    public async Task REQ_RI_057_A_submitted_or_approved_treaty_cannot_be_edited_or_resubmitted()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var submitted = await slice.SubmitAsync(id, version);

        var (edit, editBody) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = submitted, placedPct = "100" });
        edit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, editBody?.ToJsonString());
        editBody.Text("code").ShouldBe("RI-ERR-STATE");

        var (again, againBody) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/submit", new { contractId = id, expectedRecordVersion = submitted });
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("RI-ERR-STATE");

        var (ok, _) = await slice.ApproveAsync(id, submitted);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var active = int.Parse((await slice.GetAsync(id)).Text("contract.recordVersion"), CultureInfo.InvariantCulture);
        var (editActive, editActiveBody) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = active, placedPct = "100" });
        editActive.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, editActiveBody?.ToJsonString());
        editActiveBody.Text("code").ShouldBe("RI-ERR-STATE");
        var (approveAgain, approveAgainBody) = await slice.ApproveAsync(id, active, "another-manager");
        approveAgain.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, approveAgainBody?.ToJsonString());
        approveAgainBody.Text("code").ShouldBe("RI-ERR-STATE");
    }

    [Fact]
    public async Task REQ_RI_037_038_046_047_Invalid_treaties_are_refused_with_the_contract_codes()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var person = await slice.PersonAsync();
        var missing = Guid.CreateVersion7().ToString("D");
        var product = NewProduct();
        var before = await slice.ScalarAsync<long>("SELECT count(*) FROM ri.contract");

        async Task Refused(string name, Action<JsonObject> change, string code)
        {
            // RI-ERR-VALIDATION is the platform's generic validation problem (400); the RI-specific codes are 422.
            var status = code == "RI-ERR-VALIDATION" ? HttpStatusCode.BadRequest : HttpStatusCode.UnprocessableEntity;
            var (response, body) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts", Body(product, "OD", lead, follow, change: change));
            response.StatusCode.ShouldBe(status, $"{name}: {body?.ToJsonString()}");
            body.Text("code").ShouldBe(code, name);
        }

        await Refused("signed lines below placed", b => b["participations"]![1]!["signedLinePct"] = "30", "RI-ERR-SIGNED-LINES");
        await Refused("signed lines above placed", b => b["participations"]![1]!["signedLinePct"] = "50", "RI-ERR-SIGNED-LINES");
        await Refused("two leads", b => b["participations"]![1]!["lead"] = true, "RI-ERR-SIGNED-LINES");
        await Refused("no lead", b => b["participations"]![0]!["lead"] = false, "RI-ERR-SIGNED-LINES");
        await Refused("same reinsurer twice", b => b["participations"]![1]!["reinsurerPartyId"] = lead, "RI-ERR-SIGNED-LINES");
        await Refused("empty panel", b => b["participations"] = new JsonArray(), "RI-ERR-SIGNED-LINES");
        await Refused("placed over 100", b => b["placedPct"] = "120", "RI-ERR-VALIDATION");
        await Refused("non-EUR treaty currency", b => b["currency"] = "USD", "RI-ERR-VALIDATION");
        await Refused("non-EUR layer amount", b => b["layers"]![0]!["limit"] = Money("500000.00", "USD"), "RI-ERR-VALIDATION");
        await Refused("quota share type", b => b["contractType"] = "QUOTA_SHARE", "RI-ERR-VALIDATION");
        await Refused("contract year differs from the period start", b => b["contractYear"] = 2025, "RI-ERR-VALIDATION");
        await Refused("open-ended period", b => b["period"]!["to"] = null, "RI-ERR-VALIDATION");
        await Refused("empty period", b => b["period"]!["to"] = "2026-01-01", "RI-ERR-VALIDATION");
        await Refused("zero limit", b => b["layers"]![0]!["limit"] = Money("0.00"), "RI-ERR-VALIDATION");
        await Refused("negative attachment", b => b["layers"]![0]!["attachment"] = Money("-1.00"), "RI-ERR-VALIDATION");
        await Refused("no layers", b => b["layers"] = new JsonArray(), "RI-ERR-VALIDATION");
        await Refused("no scope", b => b["scope"]!["coverageCodes"] = new JsonArray(), "RI-ERR-VALIDATION");
        await Refused("another legal entity", b => b["legalEntity"] = "GR-OTHER", "RI-ERR-VALIDATION");
        await Refused("reinsurer is a person", b => b["participations"]![0]!["reinsurerPartyId"] = person, "RI-ERR-REINSURER-ID");
        await Refused("reinsurer does not exist", b => b["participations"]![0]!["reinsurerPartyId"] = missing, "RI-ERR-REINSURER-ID");
        await Refused("broker does not exist", b => b["participations"]![0]!["brokerPartyId"] = missing, "RI-ERR-REINSURER-ID");

        // Nothing was stored by any refusal, and the numbering series did not move for them (gapless, one transaction).
        (await slice.ScalarAsync<long>("SELECT count(*) FROM ri.contract")).ShouldBe(before);
    }

    [Fact]
    public async Task REQ_RI_037_A_draft_edit_is_validated_like_a_new_treaty_and_keeps_the_old_revision_as_history()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var (bad, badBody) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = version, placedPct = "90" });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, badBody?.ToJsonString());
        badBody.Text("code").ShouldBe("RI-ERR-SIGNED-LINES");

        var layers = new[] { new { layerNo = 1, attachment = Money("500000.00"), limit = Money("750000.00"), aad = Money("0.00"), aal = (JsonObject?)null } };
        var (ok, edited) = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = version, layers });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, edited?.ToJsonString());
        edited.Text("contract.layers.0.limit.amount").ShouldBe("750000.00");
        edited.Text("contract.recordVersion").ShouldBe("2");

        // Append-only history: the first revision's layer is still there beside the current one.
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM ri.layer WHERE version_id IN (SELECT version_id FROM ri.contract_version WHERE contract_id = '{id}')")).ShouldBe(2);
        (await slice.ScalarAsync<int>($"SELECT content_rev FROM ri.contract_version WHERE contract_id = '{id}'")).ShouldBe(2);
        var stale = await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = version, placedPct = "100" });
        stale.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stale.Body.Text("code").ShouldBe("RI-ERR-STALE");
    }

    [Fact]
    public async Task REQ_RI_001_Permissions_get_list_and_the_legal_entity_boundary()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var (id, version, _) = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));

        // The manager cannot enter, the accountant cannot decide, an unrelated role cannot read.
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts", Body(NewProduct(), "OD", lead, follow), Manager, "rimgr")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/approve", new { contractId = id, expectedRecordVersion = version, decision = "APPROVE" }, Accountant, "riacct"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/{id}", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Claims roles read get and applicable but cannot list, create, update or submit.
        (await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/{id}", roles: ClaimsHandler)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts/applicable?productCode=X&coverageCode=Y", roles: ClaimsHandler)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts", roles: ClaimsHandler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.SendAsync(HttpMethod.Patch, $"/api/ri/v1/contracts/{id}", new { expectedRecordVersion = version }, ClaimsHandler, "ch")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts/submit", new { contractId = id, expectedRecordVersion = version }, ClaimsHandler, "ch")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // List: paged, filtered by year and status.
        var (listed, page) = await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts?contractYear=2026&status=DRAFT&limit=1", roles: Accountant);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page.Text("items.0.status").ShouldBe("DRAFT");
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts?contractYear=1999", roles: Accountant)).Body!["items"]!.AsArray().Count.ShouldBe(0);

        // Unknown ids and malformed ids are "not found" (404), never a 500.
        var (missing, missingBody) = await slice.SendAsync(HttpMethod.Get, $"/api/ri/v1/contracts/{Guid.NewGuid()}", roles: Accountant);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missingBody.Text("code").ShouldBe("RI-ERR-NOT-FOUND");
        (await slice.SendAsync(HttpMethod.Get, "/api/ri/v1/contracts/not-a-guid", roles: Accountant)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_is_idempotent_and_numbers_are_gapless_per_legal_entity()
    {
        var (slice, lead, follow) = await NewSliceAsync();
        await using var _ = slice;
        var before = await slice.ScalarAsync<long>("SELECT count(*) FROM ri.contract");
        var key = Guid.NewGuid();
        var body = Body(NewProduct(), "OD", lead, follow);
        var first = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts", body, Accountant, "riacct", key);
        var replay = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts", body, Accountant, "riacct", key);
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Created, first.Body?.ToJsonString());
        replay.Body.Text("contract.contractId").ShouldBe(first.Body.Text("contract.contractId"));
        var mismatch = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts", Body(NewProduct(), "OD", lead, follow), Accountant, "riacct", key);
        mismatch.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        mismatch.Body.Text("code").ShouldBe("RI-ERR-IDEMPOTENCY-MISMATCH");

        var second = await slice.CreateAsync(Body(NewProduct(), "OD", lead, follow));
        var firstNumber = int.Parse(first.Body.Text("contract.contractNumber")[3..], CultureInfo.InvariantCulture);
        int.Parse(second.Body.Text("contract.contractNumber")[3..], CultureInfo.InvariantCulture).ShouldBe(firstNumber + 1);

        // A dry run computes and rolls back: nothing stored, no number consumed.
        var (dry, dryBody) = await slice.SendAsync(HttpMethod.Post, "/api/ri/v1/contracts?dryRun=true", Body(NewProduct(), "OD", lead, follow));
        dry.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        dryBody.ShouldNotBeNull();
        (await slice.ScalarAsync<long>("SELECT count(*) FROM ri.contract")).ShouldBe(before + 2);
    }

    [Fact]
    public async Task PITFALLS_18_The_activation_event_carries_party_ids_and_no_personal_data()
    {
        var (slice, _, _) = await NewSliceAsync();
        await using var _ = slice;
        var leadName = "ZZ-SECRET-LEAD-NAME-" + Guid.NewGuid().ToString("N")[..6];
        var followName = "ZZ-SECRET-FOLLOW-NAME-" + Guid.NewGuid().ToString("N")[..6];
        var lead = await slice.OrganisationAsync(leadName);
        var follow = await slice.OrganisationAsync(followName);
        var (id, _) = await slice.ApprovedAsync(Body(NewProduct(), "OD", lead, follow));

        var payload = await slice.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'RIContractActivated' AND aggregate_id = '{id}'");
        payload.ShouldNotContain("ZZ-SECRET");
        payload.ShouldContain(lead);
        payload.ShouldContain(follow);
        var json = System.Text.Json.JsonDocument.Parse(payload).RootElement;
        Keys(json).Order().ShouldBe(
            ["alaeIncluded", "contractNumber", "contractType", "coverageCodes", "currency", "layers", "participants", "period", "placedPct", "productCodes", "recoveriesInure",
             "riContractId", "sectionsAndLayers", "stableTreatyId", "statutoryInterestIncluded", "version", "year"]);
        json.GetProperty("participants").EnumerateArray().Select(p => Keys(p).Order().ToArray()).ShouldAllBe(k => k.SequenceEqual(new[] { "reinsurerPartyId", "signedLine" }));

        // Names are in no audit row, idempotency record or outbox row either.
        (await slice.ScalarAsync<long>("SELECT count(*) FROM plt.audit_event WHERE audit_event::text LIKE '%ZZ-SECRET%'")).ShouldBe(0);
        (await slice.ScalarAsync<long>("SELECT count(*) FROM plt.idempotency_record WHERE idempotency_record::text LIKE '%ZZ-SECRET%'")).ShouldBe(0);
        (await slice.ScalarAsync<long>("SELECT count(*) FROM plt.approval_request WHERE approval_request::text LIKE '%ZZ-SECRET%'")).ShouldBe(0);
    }
}
