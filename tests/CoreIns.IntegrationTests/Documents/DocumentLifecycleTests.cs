using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-02: resend, hide/unhide, supersede and withdraw; statutory rules, idempotency, archive kept, customer visibility.</summary>
public sealed class DocumentLifecycleTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Staff = "Staff.Billing";
    private const string Nobody = "Customer.Portal";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<string> RequestAsync(HttpClient client, string template, string requestNo, Guid party, string subjectId)
    {
        var body = new
        {
            documentType = template,
            subject = new { module = "BIL", type = "DelinquencyCase", id = subjectId },
            recipients = new[] { new { role = "ADDRESSEE", partyId = party } },
            deliveryInstruction = new { mode = "EXPLICIT" },
            payloadSupplement = new
            {
                requestNo,
                placeholders = new
                {
                    customerName = "Μαρία", policyNo = "POL000000123", amountDue = "120.00 EUR", dueDate = "2026-10-01",
                    withdrawalDate = "2026-10-02", effectiveDate = "2026-11-02", refundAmount = "55.00 EUR",
                },
            },
        };
        var (response, json) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", body, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json?.ToJsonString());
        return json.Text("documentId");
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(HttpClient client, string action, object body, string roles = Admin) =>
        SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/" + action, body, roles: roles);

    private static async Task<JsonNode?> GetAsync(HttpClient client, string id)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/documents/" + id, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return body;
    }

    [Fact]
    public async Task Resend_adds_a_delivery_on_the_same_archive_and_keeps_the_first()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var id = await RequestAsync(client, "DT-REFUND-ADVICE", $"RS-{marker}", Guid.CreateVersion7(), $"rs-{marker}");
        var before = await GetAsync(client, id);

        var (response, body) = await PostAsync(client, "resend", new { documentId = id, channel = "REGISTERED_POST", reason = "customer lost the e-mail" }, Staff);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("medium").ShouldBe("REGISTERED_POST");

        var after = await GetAsync(client, id);
        after!["deliveries"]!.AsArray().Count.ShouldBe(2);
        after.Text("archiveId").ShouldBe(before.Text("archiveId"));
        after.Text("contentHash").ShouldBe(before.Text("contentHash"));
        after.Text("deliveries.0.deliveryId").ShouldBe(before.Text("deliveries.0.deliveryId"));
        after.Text("deliveries.1.medium").ShouldBe("REGISTERED_POST");
        after.Text("deliveries.1.status").ShouldBe("Sent");
    }

    [Fact]
    public async Task Resend_refuses_missing_reason_unknown_medium_non_staff_and_unknown_document()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var id = await RequestAsync(client, "DT-REFUND-ADVICE", $"RE-{marker}", Guid.CreateVersion7(), $"re-{marker}");

        (await PostAsync(client, "resend", new { documentId = id }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "resend", new { documentId = id, reason = "x", channel = "FAX" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(client, "resend", new { documentId = id, reason = "x" }, Nobody)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(client, "resend", new { documentId = Guid.NewGuid(), reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(client, id))!["deliveries"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Resend_of_a_statutory_notice_stays_on_registered_post()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var id = await RequestAsync(client, "DT-NONPAY-NOTICE", $"ST-{marker}", Guid.CreateVersion7(), $"st-{marker}");

        var (email, _) = await PostAsync(client, "resend", new { documentId = id, channel = "EMAIL", reason = "try e-mail" }, Staff);
        email.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (post, body) = await PostAsync(client, "resend", new { documentId = id, reason = "returned mail, second attempt" }, Staff);
        post.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("medium").ShouldBe("REGISTERED_POST");
    }

    [Fact]
    public async Task Resend_is_refused_for_a_dsar_restricted_recipient_and_for_a_withdrawn_document()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var restricted = await RequestAsync(client, "DT-REFUND-ADVICE", $"DS-{marker}", party, $"ds-{marker}");
        await using (var source = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var command = source.CreateCommand(
                "INSERT INTO doc.dsar_restriction (restriction_id, party_id, record_class, reason, legal_basis, retention_end, restricted_at) VALUES (gen_random_uuid(), @p, 'DELIVERY', 'test', 'test', 'UNSET_F404', now())");
            command.Parameters.AddWithValue("p", party);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await PostAsync(client, "resend", new { documentId = restricted, reason = "again" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var withdrawn = await RequestAsync(client, "DT-REFUND-ADVICE", $"WD-{marker}", Guid.CreateVersion7(), $"wd-{marker}");
        (await PostAsync(client, "withdraw", new { documentId = withdrawn, reason = "issued in error" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, "resend", new { documentId = withdrawn, reason = "again" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Hide_and_unhide_flip_the_flag_idempotently_and_keep_the_document()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var id = await RequestAsync(client, "DT-REFUND-ADVICE", $"HD-{marker}", Guid.CreateVersion7(), $"hd-{marker}");

        (await PostAsync(client, "hide", new { documentId = id }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        for (var i = 0; i < 2; i++)
        {
            var (response, body) = await PostAsync(client, "hide", new { documentId = id, reason = "duplicate in the list" }, Staff);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            body!["hidden"]!.GetValue<bool>().ShouldBeTrue();
        }

        var (_, hiddenList) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/search?q=HD-{marker}", roles: Admin);
        hiddenList!["items"]!.AsArray().Count.ShouldBe(0);
        var (_, withHidden) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/search?q=HD-{marker}&includeHidden=true", roles: Admin);
        withHidden!["items"]!.AsArray().Count.ShouldBe(1);
        (await GetAsync(client, id))!["hidden"]!.GetValue<bool>().ShouldBeTrue();

        var (unhide, body2) = await PostAsync(client, "unhide", new { documentId = id, reason = "needed again" }, Staff);
        unhide.StatusCode.ShouldBe(HttpStatusCode.OK, body2?.ToJsonString());
        body2!["hidden"]!.GetValue<bool>().ShouldBeFalse();
        (await PostAsync(client, "unhide", new { documentId = id, reason = "needed again" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, visible) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/search?q=HD-{marker}", roles: Admin);
        visible!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Supersede_links_the_replacement_and_marks_the_old_document_superseded()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var old = await RequestAsync(client, "DT-REFUND-ADVICE", $"SU-{marker}-1", party, $"su-{marker}");
        var replacement = await RequestAsync(client, "DT-REFUND-ADVICE", $"SU-{marker}-2", party, $"su-{marker}");
        var other = await RequestAsync(client, "DT-REFUND-ADVICE", $"SU-{marker}-3", party, $"su-other-{marker}");
        var otherType = await RequestAsync(client, "DT-CANCELLATION-NOTICE", $"SU-{marker}-4", party, $"su-{marker}");
        var archive = (await GetAsync(client, old)).Text("archiveId");

        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = replacement }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = old, reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = other, reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = otherType, reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = Guid.NewGuid(), reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (ok, body) = await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = replacement, reason = "amount corrected" }, Staff);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("Superseded");
        body.Text("supersededByDocumentId").ShouldBe(replacement);

        var read = await GetAsync(client, old);
        read.Text("status").ShouldBe("Superseded");
        read.Text("supersededByDocumentId").ShouldBe(replacement);
        read.Text("withdrawnReason").ShouldBe("amount corrected");
        read.Text("archiveId").ShouldBe(archive);
        read!["deliveries"]!.AsArray().Count.ShouldBe(1);

        // Idempotent for the same replacement, refused for another outcome.
        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = replacement, reason = "amount corrected" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, "supersede", new { documentId = old, replacementDocumentId = other, reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync(client, "withdraw", new { documentId = old, reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // A superseded document cannot be the replacement of another one.
        (await PostAsync(client, "supersede", new { documentId = replacement, replacementDocumentId = old, reason = "x" }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Withdraw_needs_a_reason_keeps_the_archive_and_evidence_and_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var id = await RequestAsync(client, "DT-REFUND-ADVICE", $"WI-{marker}", Guid.CreateVersion7(), $"wi-{marker}");
        var deliveryId = (await GetAsync(client, id)).Text("deliveries.0.deliveryId");
        var (cb, cbBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/provider-events/stub-email",
            new { providerEventId = $"EV-{marker}", deliveryId, eventType = "DELIVERED" }, roles: Admin);
        cb.StatusCode.ShouldBe(HttpStatusCode.Accepted, cbBody?.ToJsonString());

        (await PostAsync(client, "withdraw", new { documentId = id }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "withdraw", new { documentId = id, reason = "   " }, Staff)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, "withdraw", new { documentId = id, reason = "wrong recipient" }, Nobody)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        for (var i = 0; i < 2; i++)
        {
            var (response, body) = await PostAsync(client, "withdraw", new { documentId = id, reason = "wrong recipient" }, Staff);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            body.Text("status").ShouldBe("Superseded");
        }

        var read = await GetAsync(client, id);
        read.Text("status").ShouldBe("Superseded");
        read.Text("withdrawnReason").ShouldBe("wrong recipient");
        read!["supersededByDocumentId"].ShouldBeNull();
        read.Text("deliveries.0.status").ShouldBe("Delivered");

        var (_, evidence) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/deliveries/evidence-for?documentId={id}", roles: Admin);
        evidence.ShouldNotBeNull();
        var (archive, _) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/{read.Text("archiveId")}", roles: Admin);
        archive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Withdrawing_a_statutory_notice_needs_the_admin_role_and_a_reason()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var notice = await RequestAsync(client, "DT-NONPAY-NOTICE", $"SW-{marker}", party, $"sw-{marker}");

        var (denied, _) = await PostAsync(client, "withdraw", new { documentId = notice, reason = "issued in error" }, Staff);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(client, "withdraw", new { documentId = notice }, Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await GetAsync(client, notice)).Text("status").ShouldBe("Rendered");

        var (ok, body) = await PostAsync(client, "withdraw", new { documentId = notice, reason = "issued in error" }, Admin);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await GetAsync(client, notice)).Text("status").ShouldBe("Superseded");
    }

    [Fact]
    public async Task Customer_visibility_is_a_typed_flag_of_the_document_type()
    {
        using var client = _factory.CreateClient();
        var (response, types) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/document-types", roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refund = types!["items"]!.AsArray().Single(i => i!["documentType"]!.GetValue<string>() == "DT-REFUND-ADVICE");
        refund!["customerVisible"]!.GetValue<bool>().ShouldBeTrue();

        await using (var source = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var command = source.CreateCommand(
                "INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields) VALUES ('DT-INTERNAL-TEST', 'EMAIL', 'x', 'x', ARRAY[]::text[]) ON CONFLICT DO NOTHING");
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (_, again) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/document-types", roles: Admin);
        again!["items"]!.AsArray().Single(i => i!["documentType"]!.GetValue<string>() == "DT-INTERNAL-TEST")!["customerVisible"]!.GetValue<bool>().ShouldBeFalse();

        var marker = Guid.NewGuid().ToString("N")[..8];
        var id = await RequestAsync(client, "DT-REFUND-ADVICE", $"CV-{marker}", Guid.CreateVersion7(), $"cv-{marker}");
        (await GetAsync(client, id))!["customerVisible"]!.GetValue<bool>().ShouldBeTrue();
        var (_, search) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/search?q=CV-{marker}", roles: Admin);
        search!["items"]!.AsArray().Single()!["customerVisible"]!.GetValue<bool>().ShouldBeTrue();
    }
}
