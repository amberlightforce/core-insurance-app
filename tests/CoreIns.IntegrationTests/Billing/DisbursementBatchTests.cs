using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.IntegrationTests.Party;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// BO-BIL-10: bank-file batches of the non-claim disbursements. File totals equal the items, the release needs a second person (neither the
/// release requester nor an item requester), a stopped item cannot be in a batch, the file hash is what the second person confirms, payees
/// are screened again at release, a disbursement is released once, the ledger releases each source from its own payable (and a stop after
/// release gives it back), and with no bank channel or file store nothing is prepared or released (fail closed).
/// </summary>
public sealed class DisbursementBatchTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Billing = "Staff.Billing";
    private const string Manager = "Staff.BillingManager";
    private DisbursementSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ValueDate => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpClient client, string path, object body, string roles, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> Post(string path, object body, string roles = Billing, string user = "maker")
        => SendAsync(_slice.Client, path, body, roles, user);

    private sealed record Item(string Id, string Number, decimal Amount, string SourceType);

    /// <summary>A disbursement requested by "maker" and approved by "approver" (Approved, waiting for a batch).</summary>
    private async Task<Item> ApprovedAsync(string sourceType, decimal amount, DisbursementSlice? slice = null)
    {
        slice ??= _slice;
        var party = PartyId.New();
        var purpose = sourceType switch
        {
            DisbursementCodes.LevyPayment => PayeeAccountCreateRequest.PurposeValue.Levy,
            DisbursementCodes.TaxRemittance => PayeeAccountCreateRequest.PurposeValue.Tax,
            _ => PayeeAccountCreateRequest.PurposeValue.Commission,
        };
        var account = await slice.CreateAccountAsync(party, DisbursementSlice.NewIban(), purpose);
        var request = new DisbursementRequestRequest
        {
            SourceType = sourceType,
            SourceId = "SRC-" + Guid.NewGuid().ToString("N"),
            PayeePartyId = party,
            PayeeAccountId = account.PayeeAccountId,
            Amount = new Money(amount, Currency.EUR),
            Method = DisbursementCodes.SepaCreditTransfer,
            StatementReference = "REF-" + Guid.NewGuid().ToString("N")[..8],
            PurposeText = "Batch test",
        };
        var (created, body) = await SendAsync(slice.Client, "/api/bil/v1/disbursements/request", request, Billing, "maker");
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body.Text("disbursementId");
        var (approved, approvedBody) = await SendAsync(slice.Client, "/api/bil/v1/disbursements/approve-release", new { disbursementId = id, decision = "APPROVE" }, Manager, "approver");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        return new Item(id, body.Text("disbursementNumber"), amount, sourceType);
    }

    private async Task<JsonNode> PrepareAsync(params Item[] items)
    {
        var (response, body) = await Post("/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = items.Select(i => i.Id).ToArray() }, Billing, "preparer");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private async Task<JsonNode> RequestReleaseAsync(string batchId)
    {
        var (response, body) = await Post("/api/bil/v1/disbursement-batches/release", new { batchId }, Billing, "releaser");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    private Task<string> StateAsync(string id) => _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{id}'");

    private Task<List<string>> LinesAsync(string id, string entryType) => _slice.TextsAsync(
        $"""
        SELECT l.side || ':' || l.account_code || ':' || l.amount::numeric(19,2)
        FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
        WHERE e.disbursement_id = '{id}' AND e.entry_type = '{entryType}' ORDER BY l.line_no
        """);

    [Fact]
    public async Task BO_BIL_10_prepare_groups_approved_items_into_one_file_whose_totals_equal_the_items_and_posts_nothing()
    {
        var items = new[]
        {
            await ApprovedAsync(DisbursementCodes.CommissionPayment, 120.50m),
            await ApprovedAsync(DisbursementCodes.LevyPayment, 300m),
            await ApprovedAsync(DisbursementCodes.TaxRemittance, 79.50m),
            await ApprovedAsync(DisbursementCodes.OtherPayment, 10.25m),
        };

        var prepared = await PrepareAsync(items);
        var batch = prepared["batch"]!;
        batch.Text("status").ShouldBe("Prepared");
        batch["itemCount"]!.GetValue<int>().ShouldBe(4);
        decimal.Parse(batch["totalAmount"]!["amount"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(510.25m);
        batch["items"]!.AsArray().Select(i => i.Text("disbursementId")).Order().ShouldBe(items.Select(i => i.Id).Order());
        batch.Text("fileHash").ShouldMatch("^[0-9a-f]{64}$");
        batch.Text("maskedBankAccount").ShouldStartWith("****");
        prepared.Text("fileRef").ShouldBe(batch.Text("fileRef"));

        // Nothing is sent or posted before the release: the items are still Approved and no ledger entry exists.
        foreach (var item in items)
        {
            (await StateAsync(item.Id)).ShouldBe("APPROVED");
            (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{item.Id}'")).ShouldBe(0);
        }

        _slice.Factory.Services.GetRequiredService<InMemoryBankFileStore>().Read(prepared.Text("fileRef")).ShouldBeNull();

        // A disbursement is in one batch only (EC-08): naming it again is held, and a rejected explicit call creates nothing.
        var (again, againBody) = await Post("/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = new[] { items[0].Id } }, Billing, "preparer");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        againBody.Text("code").ShouldBe("BIL-ERR-HELD-ITEMS");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement_batch_item WHERE disbursement_id = '{items[0].Id}'")).ShouldBe(1);

        // Shape and permission gates.
        (await Post("/api/bil/v1/disbursement-batches/prepare", new { bankAccount = "GR00", valueDate = ValueDate }, Billing, "preparer")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post("/api/bil/v1/disbursement-batches/prepare", new { bankAccount = DisbursementSlice.NewIban(), valueDate = "2020-01-01", disbursementIds = new[] { items[0].Id } }, Billing, "preparer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post("/api/bil/v1/disbursement-batches/prepare", new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate }, "Staff.Underwriter", "preparer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BO_BIL_10_release_needs_a_second_person_and_posts_each_source_from_its_own_payable_then_clears_it()
    {
        var commission = await ApprovedAsync(DisbursementCodes.CommissionPayment, 200m);
        var levy = await ApprovedAsync(DisbursementCodes.LevyPayment, 300.10m);
        var tax = await ApprovedAsync(DisbursementCodes.TaxRemittance, 400m);
        var other = await ApprovedAsync(DisbursementCodes.OtherPayment, 99.90m);
        var items = new[] { commission, levy, tax, other };
        var batchId = (await PrepareAsync(items))["batch"].Text("batchId");

        // Approving before the release is requested is refused.
        var (early, earlyBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = new string('a', 64) }, Manager, "checker");
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        earlyBody.Text("code").ShouldBe("BIL-ERR-APPROVAL-REQUIRED");

        var requested = await RequestReleaseAsync(batchId);
        requested["batch"].Text("status").ShouldBe("ReleaseRequested");
        var hash = requested["batch"].Text("fileHash");

        // Segregation of duties: not the person who requested the release, not the requester of an item, and only a billing manager.
        foreach (var (user, roles) in new[] { ("releaser", Manager), ("maker", Manager) })
        {
            var (self, selfBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = hash }, roles, user);
            self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            selfBody.Text("code").ShouldBe("BIL-ERR-SOD");
        }

        (await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = hash }, Billing, "checker")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The approver confirms the file hash: a different hash is stale and releases nothing.
        var (stale, staleBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = new string('0', 64) }, Manager, "checker");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("BIL-ERR-STALE");
        (await StateAsync(commission.Id)).ShouldBe("APPROVED");

        var (ok, okBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = hash }, Manager, "checker");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
        okBody!["batch"].Text("status").ShouldBe("Released");
        okBody["batch"].Text("releasedBy").ShouldContain("checker");

        // The file went to the store: its hash is the confirmed hash, and its control sum and transfers equal the items.
        var file = _slice.Factory.Services.GetRequiredService<InMemoryBankFileStore>().Read(okBody.Text("fileRef"));
        file.ShouldNotBeNull();
        var xml = System.Text.Encoding.UTF8.GetString(file);
        Sha256Hash.ComputeUtf8(xml).Value.ShouldBe(hash);
        xml.ShouldContain("<NbOfTxs>4</NbOfTxs>");
        xml.ShouldContain("<CtrlSum>1000.00</CtrlSum>");
        foreach (var item in items)
        {
            xml.ShouldContain($"<EndToEndId>{item.Number}</EndToEndId>");
            xml.ShouldContain($"<InstdAmt Ccy=\"EUR\">{item.Amount:F2}</InstdAmt>");
        }

        // Each source is released from its own payable and, with the auto stub bank, issued and cleared.
        (await LinesAsync(commission.Id, "DISBURSEMENT_RELEASED")).ShouldBe(["DEBIT:LA-14:200.00", "CREDIT:LA-13:200.00"]);
        (await LinesAsync(levy.Id, "DISBURSEMENT_RELEASED")).ShouldBe(["DEBIT:LA-07:300.10", "CREDIT:LA-13:300.10"]);
        (await LinesAsync(tax.Id, "DISBURSEMENT_RELEASED")).ShouldBe(["DEBIT:LA-06:400.00", "CREDIT:LA-13:400.00"]);
        (await LinesAsync(other.Id, "DISBURSEMENT_RELEASED")).ShouldBe(["DEBIT:LA-20:99.90", "CREDIT:LA-13:99.90"]);
        foreach (var item in items)
        {
            (await StateAsync(item.Id)).ShouldBe("CLEARED");
            (await LinesAsync(item.Id, "DISBURSEMENT_CLEARED")).ShouldBe([$"DEBIT:LA-13:{item.Amount:F2}", $"CREDIT:LA-10:{item.Amount:F2}"]);
        }

        // Double release: the batch is Released and its items are not Approved, so a second approval changes nothing (EC-08).
        var (twice, twiceBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = hash }, Manager, "checker");
        twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        twiceBody.Text("code").ShouldBe("BIL-ERR-APPROVAL-REQUIRED");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{commission.Id}' AND entry_type = 'DISBURSEMENT_RELEASED'")).ShouldBe(1);
        (await Post("/api/bil/v1/disbursement-batches/release", new { batchId }, Billing, "releaser")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
    }

    [Fact]
    public async Task BO_BIL_10_a_stopped_item_cannot_be_in_a_batch_and_is_dropped_before_the_release_with_a_new_file_hash()
    {
        var a = await ApprovedAsync(DisbursementCodes.LevyPayment, 100m);
        var b = await ApprovedAsync(DisbursementCodes.LevyPayment, 50m);
        var c = await ApprovedAsync(DisbursementCodes.CommissionPayment, 25m);

        // Stopped before the batch: naming it is held.
        var (stopped, _) = await Post("/api/bil/v1/disbursements/stop", new { disbursementId = c.Id, reason = "WRONG_PAYEE" }, Billing, "maker");
        stopped.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (held, heldBody) = await Post("/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = new[] { a.Id, c.Id } }, Billing, "preparer");
        held.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        heldBody.Text("code").ShouldBe("BIL-ERR-HELD-ITEMS");
        heldBody!.ToJsonString().ShouldContain("NOT_APPROVED");

        var batchId = (await PrepareAsync(a, b))["batch"].Text("batchId");
        var firstHash = (await RequestReleaseAsync(batchId))["batch"].Text("fileHash");

        // Stopped after the release was requested: the approval of the old hash is stale, and requesting again drops the item and re-hashes.
        (await Post("/api/bil/v1/disbursements/stop", new { disbursementId = b.Id, reason = "WRONG_AMOUNT" }, Billing, "maker")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (stale, staleBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = firstHash }, Manager, "checker");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("BIL-ERR-STALE");

        var again = await RequestReleaseAsync(batchId);
        var secondHash = again["batch"].Text("fileHash");
        secondHash.ShouldNotBe(firstHash);
        again["batch"]!["itemCount"]!.GetValue<int>().ShouldBe(1);
        decimal.Parse(again["batch"]!["totalAmount"]!["amount"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(100m);

        var (ok, okBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = secondHash }, Manager, "checker");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
        (await StateAsync(a.Id)).ShouldBe("CLEARED");
        (await StateAsync(b.Id)).ShouldBe("STOPPED");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{b.Id}'")).ShouldBe(0);
        var xml = System.Text.Encoding.UTF8.GetString(_slice.Factory.Services.GetRequiredService<InMemoryBankFileStore>().Read(okBody!.Text("fileRef"))!);
        xml.ShouldNotContain(b.Number);
        xml.ShouldContain("<CtrlSum>100.00</CtrlSum>");

        // A batch whose items were all stopped has nothing to release.
        var d = await ApprovedAsync(DisbursementCodes.LevyPayment, 10m);
        var emptyBatch = (await PrepareAsync(d))["batch"].Text("batchId");
        (await Post("/api/bil/v1/disbursements/stop", new { disbursementId = d.Id, reason = "WRONG_AMOUNT" }, Billing, "maker")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (empty, emptyBody) = await Post("/api/bil/v1/disbursement-batches/release", new { batchId = emptyBatch }, Billing, "releaser");
        empty.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        emptyBody.Text("code").ShouldBe("BIL-ERR-NOTHING-PAYABLE");
    }

    [Fact]
    public async Task BO_BIL_10_payees_are_screened_again_at_release_and_a_hit_releases_nothing()
    {
        var a = await ApprovedAsync(DisbursementCodes.LevyPayment, 100m);
        var b = await ApprovedAsync(DisbursementCodes.CommissionPayment, 40m);
        var batchId = (await PrepareAsync(a, b))["batch"].Text("batchId");
        var hash = (await RequestReleaseAsync(batchId))["batch"].Text("fileHash");

        _slice.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear() with { Result = ScreeningScreenResponse.ResultValue.PotentialHit, CaseNumber = "SCR-10" });
        var (blocked, blockedBody) = await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = hash }, Manager, "checker");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        blockedBody.Text("code").ShouldBe("BIL-ERR-PAYEE-BLOCKED");

        // Nothing was released, posted or written to the bank-file store; the batch can be approved once screening is clear.
        (await StateAsync(a.Id)).ShouldBe("APPROVED");
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id IN ('{a.Id}', '{b.Id}')")).ShouldBe(0);
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement_batch WHERE batch_id = '{batchId}'")).ShouldBe("RELEASE_REQUESTED");

        _slice.Screening.Setup("pty.Screening.screen", DisbursementSlice.Clear());
        (await Post("/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = hash }, Manager, "checker")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StateAsync(a.Id)).ShouldBe("CLEARED");

        // Screening that is down fails closed too (its own host: the fake keeps a failure once set).
        await using var down = new DisbursementSlice(database);
        var item = await ApprovedAsync(DisbursementCodes.LevyPayment, 12m, down);
        var (prep, prepBody) = await SendAsync(down.Client, "/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = new[] { item.Id } }, Billing, "preparer");
        var downBatch = prepBody!["batch"].Text("batchId");
        var (req, reqBody) = await SendAsync(down.Client, "/api/bil/v1/disbursement-batches/release", new { batchId = downBatch }, Billing, "releaser");
        prep.StatusCode.ShouldBe(HttpStatusCode.OK);
        req.StatusCode.ShouldBe(HttpStatusCode.OK);
        down.Screening.Fail("pty.Screening.screen", new TimeoutException("down"));
        var (unavailable, unavailableBody) = await SendAsync(down.Client, "/api/bil/v1/disbursement-batches/approve-release", new { batchId = downBatch, fileHash = reqBody!["batch"].Text("fileHash") }, Manager, "checker");
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        unavailableBody.Text("code").ShouldBe("BIL-ERR-SCREENING-UNAVAILABLE");
        (await down.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{item.Id}'")).ShouldBe("APPROVED");
    }

    [Fact]
    public async Task BO_BIL_10_without_a_bank_channel_or_file_store_nothing_is_prepared_or_released_fail_closed()
    {
        await using var noChannel = new DisbursementSlice(database, bankChannel: false);
        var item = await ApprovedAsync(DisbursementCodes.LevyPayment, 10m, noChannel);
        var (prep, prepBody) = await SendAsync(noChannel.Client, "/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = new[] { item.Id } }, Billing, "preparer");
        prep.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        prepBody.Text("code").ShouldBe("BIL-ERR-METHOD-UNAVAILABLE");

        await using var noStore = new DisbursementSlice(database, bankFileStore: false);
        var item2 = await ApprovedAsync(DisbursementCodes.LevyPayment, 10m, noStore);
        var (prep2, _) = await SendAsync(noStore.Client, "/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = new[] { item2.Id } }, Billing, "preparer");
        prep2.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await noChannel.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement_batch_item WHERE disbursement_id IN ('{item.Id}', '{item2.Id}')")).ShouldBe(0);
    }

    [Fact]
    public async Task BO_BIL_10_a_stop_after_release_gives_the_source_payable_back_with_FIN_rules_for_each_source()
    {
        await using var manual = new DisbursementSlice(database, manualBank: true);
        var items = new[]
        {
            await ApprovedAsync(DisbursementCodes.CommissionPayment, 20m, manual),
            await ApprovedAsync(DisbursementCodes.LevyPayment, 30m, manual),
            await ApprovedAsync(DisbursementCodes.TaxRemittance, 40m, manual),
            await ApprovedAsync(DisbursementCodes.OtherPayment, 50m, manual),
        };
        var (prep, prepBody) = await SendAsync(manual.Client, "/api/bil/v1/disbursement-batches/prepare",
            new { bankAccount = DisbursementSlice.NewIban(), valueDate = ValueDate, disbursementIds = items.Select(i => i.Id).ToArray() }, Billing, "preparer");
        prep.StatusCode.ShouldBe(HttpStatusCode.OK, prepBody?.ToJsonString());
        var batchId = prepBody!["batch"].Text("batchId");
        var (req, reqBody) = await SendAsync(manual.Client, "/api/bil/v1/disbursement-batches/release", new { batchId }, Billing, "releaser");
        req.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (ok, okBody) = await SendAsync(manual.Client, "/api/bil/v1/disbursement-batches/approve-release", new { batchId, fileHash = reqBody!["batch"].Text("fileHash") }, Manager, "checker");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());

        // The manual stub bank holds each payment at Released; a stop reverses the release into the source payable.
        var payables = new[] { "LA-14", "LA-07", "LA-06", "LA-20" };
        for (var i = 0; i < items.Length; i++)
        {
            (await manual.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{items[i].Id}'")).ShouldBe("RELEASED");
            var (stop, stopBody) = await SendAsync(manual.Client, "/api/bil/v1/disbursements/stop", new { disbursementId = items[i].Id, reason = "RETURNED_BY_BANK" }, Billing, "maker");
            stop.StatusCode.ShouldBe(HttpStatusCode.OK, stopBody?.ToJsonString());
            (await manual.TextsAsync(
                $"""
                SELECT l.side || ':' || l.account_code || ':' || l.amount::numeric(19,2)
                FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
                WHERE e.disbursement_id = '{items[i].Id}' AND e.entry_type = 'DISBURSEMENT_REVERSED' ORDER BY l.line_no
                """)).ShouldBe([$"DEBIT:LA-13:{items[i].Amount:F2}", $"CREDIT:{payables[i]}:{items[i].Amount:F2}"]);
        }
    }
}
