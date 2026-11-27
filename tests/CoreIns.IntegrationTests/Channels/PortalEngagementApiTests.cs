using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Channels.Events;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>
/// BO-CHN-04 without a matrix in force: scope on every read (owner, household, another customer, staff, unlinked), the vault filters, secure
/// messages, notifications and the fail-closed matrix answer (DENY) of pay-now and download.
/// </summary>
public sealed class PortalEngagementApiTests(PostgresFixture database) : PortalEngagementHost(database), IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Billing_is_scoped_to_the_own_policies_and_never_shows_another_customer()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        var open = await GetAsync("/api/chn/v1/portal/billing/invoices", a);
        open.Response.StatusCode.ShouldBe(HttpStatusCode.OK, open.Text);
        open.Body!["items"]!.AsArray().Select(i => i!["policyId"]!.GetValue<string>()).ShouldBe([PolicyAlice.Value.ToString("D"), PolicyBob.Value.ToString("D")], ignoreOrder: true);
        open.Body["items"]!.AsArray().All(i => i!["payable"]!.GetValue<bool>() && i["overdue"]!.GetValue<bool>()).ShouldBeTrue();
        open.Body["items"]![0]!["policyNumber"]!.GetValue<string>().ShouldStartWith("POL-");
        open.Text.ShouldNotContain(PolicyCarol.Value.ToString("D"));

        var all = await GetAsync("/api/chn/v1/portal/billing/invoices?state=all", a);
        all.Body!["items"]!.AsArray().Count.ShouldBe(4);

        var paged = await GetAsync("/api/chn/v1/portal/billing/invoices?state=all&limit=3", a);
        paged.Body!["items"]!.AsArray().Count.ShouldBe(3);
        (await GetAsync($"/api/chn/v1/portal/billing/invoices?state=all&limit=3&cursor={paged.Body["nextCursor"]!.GetValue<string>()}", a)).Body!["items"]!.AsArray().Count.ShouldBe(1);

        // Carol sees her own invoice only, and no balances: she does not pay the account.
        var carol = await GetAsync("/api/chn/v1/portal/billing/invoices?state=all", c);
        carol.Body!["items"]!.AsArray().Select(i => i!["policyId"]!.GetValue<string>()).Distinct().ShouldBe([PolicyCarol.Value.ToString("D")]);
        var carolSummary = await GetAsync("/api/chn/v1/portal/billing/summary", c);
        carolSummary.Body!["accounts"]!.AsArray().Count.ShouldBe(0);
        carolSummary.Body["openInvoiceCount"]!.GetValue<int>().ShouldBe(1);

        var summary = await GetAsync("/api/chn/v1/portal/billing/summary", a);
        summary.Body!["accounts"]!.AsArray().Count.ShouldBe(1);
        summary.Body["openInvoiceCount"]!.GetValue<int>().ShouldBe(2);
        summary.Body["overdueInvoiceCount"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Refund_status_shows_amount_and_state_only()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        var refunds = await GetAsync("/api/chn/v1/portal/billing/refunds", a);
        refunds.Response.StatusCode.ShouldBe(HttpStatusCode.OK, refunds.Text);
        refunds.Body!["items"]!.AsArray().Count.ShouldBe(1);
        refunds.Body["items"]![0]!["amount"]!["amount"]!.GetValue<string>().ShouldBe("12.5");
        foreach (var forbidden in new[] { "payee", "iban", "Iban", "decisionComment", "reasonCode", "requestedBy", "breakdown", "netting" })
        {
            refunds.Text.ShouldNotContain(forbidden);
        }

        // Carol pays no account, so no refunds.
        (await GetAsync("/api/chn/v1/portal/billing/refunds", c)).Body!["items"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Pay_now_is_a_404_for_a_foreign_invoice_and_DENY_without_a_matrix_in_force()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        // Carol cannot even probe Alice's invoice: 404, like an unknown id or a malformed one.
        (await PostAsync($"/api/chn/v1/portal/billing/invoices/{InvoiceIdOf(PolicyAlice, 1)}/pay-now", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync($"/api/chn/v1/portal/billing/invoices/{Guid.NewGuid()}/pay-now", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/portal/billing/invoices/nope/pay-now", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var denied = await PostAsync($"/api/chn/v1/portal/billing/invoices/{InvoiceIdOf(PolicyAlice, 1)}/pay-now", a);
        denied.Response.StatusCode.ShouldBe(HttpStatusCode.OK, denied.Text);
        denied.Body!["decision"]!.GetValue<string>().ShouldBe("DENY");
        denied.Body["payable"]!.GetValue<bool>().ShouldBeFalse();
        denied.Body["payUrl"].ShouldBeNull();
        denied.Body["reasons"]![0]!.GetValue<string>().ShouldBe("NO_ACTIVE_MATRIX");
    }

    [Fact]
    public async Task The_vault_lists_only_rendered_customer_documents_addressed_to_the_scope()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        var list = await GetAsync("/api/chn/v1/portal/documents", a);
        list.Response.StatusCode.ShouldBe(HttpStatusCode.OK, list.Text);
        var ids = list.Body!["items"]!.AsArray().Select(i => i!["documentId"]!.GetValue<string>()).ToList();
        ids.ShouldBe([DocAliceLetter.ToString("D"), DocBobLetter.ToString("D")], ignoreOrder: true);
        foreach (var hidden in new[] { DocCarolLetterOnAlicePolicy, DocInternal, DocFailed, DocCarolLetter })
        {
            list.Text.ShouldNotContain(hidden.ToString("D"));
        }

        var carol = await GetAsync("/api/chn/v1/portal/documents", c);
        carol.Body!["items"]!.AsArray().Select(i => i!["documentId"]!.GetValue<string>()).ShouldBe([DocCarolLetter.ToString("D")]);

        // Filtering by a foreign policy is a 404; by an own policy narrows.
        (await GetAsync($"/api/chn/v1/portal/documents?policyId={PolicyCarol.Value:D}", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/documents?policyId={PolicyBob.Value:D}", a)).Body!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Download_is_refused_by_the_matrix_when_none_is_in_force_and_404_for_other_peoples_documents()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);

        // Addressed to Carol or unknown: 404 (never 403), whatever the matrix says.
        (await GetAsync($"/api/chn/v1/portal/documents/{DocCarolLetter:D}/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/documents/{DocCarolLetterOnAlicePolicy:D}/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/documents/{Guid.NewGuid():D}/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync("/api/chn/v1/portal/documents/nope/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Her own document, but fail closed without a matrix: 403 and nothing recorded as opened.
        (await GetAsync($"/api/chn/v1/portal/documents/{DocAliceLetter:D}/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync("/api/chn/v1/portal/documents", a)).Body!["items"]!.AsArray()
            .Single(i => i!["documentId"]!.GetValue<string>() == DocAliceLetter.ToString("D"))!["openCount"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Staff_and_unlinked_callers_get_nothing_from_the_portal_billing_document_and_inbox_endpoints()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);
        var paths = new[]
        {
            "/api/chn/v1/portal/billing/summary", "/api/chn/v1/portal/billing/invoices", "/api/chn/v1/portal/billing/refunds", "/api/chn/v1/portal/documents",
            "/api/chn/v1/notifications", "/api/chn/v1/notification-subscriptions",
        };
        foreach (var path in paths)
        {
            (await GetAsync(path, "stranger-" + Id())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            ((int)(await GetAsync(path, a, "Staff.Underwriter")).Response.StatusCode).ShouldBeOneOf(403, 404);
            (await GetAsync(path, a, "Staff.Underwriter,Customer.Portal")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }

        (await PostAsync($"/api/chn/v1/portal/billing/invoices/{InvoiceIdOf(PolicyAlice, 1)}/pay-now", "stranger-" + Id())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/messages/send", "stranger-" + Id(), new { text = "hello" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Messages_keep_thread_order_stay_private_and_a_staff_reply_notifies_the_customer()
    {
        var a = "cust-a-" + Id();
        var b = "cust-b-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(b, Bob);
        await LinkAsync(c, Carol);

        // A new thread about an own policy; a household member's policy is in scope too, a stranger's is a 404.
        var first = await PostAsync("/api/chn/v1/messages/send", a, new { topic = "BILLING", policyId = PolicyBob.Value, text = "  First question  " });
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK, first.Text);
        var thread = first.Body!["thread"]!;
        var threadId = thread["threadId"]!.GetValue<string>();
        thread["awaitingStaff"]!.GetValue<bool>().ShouldBeTrue();
        thread["messages"]![0]!["text"]!.GetValue<string>().ShouldBe("First question");
        thread["partyId"].ShouldBeNull("the customer never sees the party id of their own thread");
        (await PostAsync("/api/chn/v1/messages/send", a, new { topic = "POLICY", policyId = PolicyCarol.Value, text = "not mine" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/messages/send", a, new { text = new string('x', 4001) })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Follow-up in the same thread; order is oldest first.
        var second = await PostAsync("/api/chn/v1/messages/send", a, new { threadId, text = "Second" });
        second.Response.StatusCode.ShouldBe(HttpStatusCode.OK, second.Text);
        second.Body!["thread"]!["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()).ShouldBe(["First question", "Second"]);

        // Another customer, even in the same household, cannot read or continue it.
        (await PostAsync("/api/chn/v1/messages/send", b, new { threadId, text = "hijack" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync("/api/chn/v1/messages", b)).Body!["items"]!.AsArray().Count.ShouldBe(0);
        (await GetAsync("/api/chn/v1/messages", c)).Body!["items"]!.AsArray().Count.ShouldBe(0);

        // The open request shows on the dashboard until staff answer.
        var dashboard = await GetAsync("/api/chn/v1/portal/dashboard", a);
        dashboard.Body!["openRequests"]!.AsArray().Select(r => r!["threadId"]!.GetValue<string>()).ShouldContain(threadId);

        // Customers cannot reply as staff; staff can, and see the party.
        ((int)(await PostAsync("/api/chn/v1/messages/reply", a, new { threadId, text = "self-reply" })).Response.StatusCode).ShouldBe(403);
        var reply = await PostAsync("/api/chn/v1/messages/reply", "staff-1", new { threadId, text = "We have a look" }, "Staff.Billing");
        reply.Response.StatusCode.ShouldBe(HttpStatusCode.OK, reply.Text);
        reply.Body!["thread"]!["awaitingStaff"]!.GetValue<bool>().ShouldBeFalse();
        reply.Body["thread"]!["partyId"]!.GetValue<string>().ShouldBe(Alice.Value.ToString("D"));
        (await PostAsync("/api/chn/v1/messages/reply", "staff-1", new { threadId = Guid.NewGuid(), text = "?" }, "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var listed = await GetAsync("/api/chn/v1/messages", a);
        var messages = listed.Body!["items"]![0]!["thread"]!["messages"]!.AsArray();
        messages.Select(m => m!["direction"]!.GetValue<string>()).ShouldBe(["CUSTOMER", "CUSTOMER", "STAFF"]);
        listed.Text.ShouldNotContain(Alice.Value.ToString("D"));
        (await GetAsync("/api/chn/v1/portal/dashboard", a)).Body!["openRequests"]!.AsArray().Count.ShouldBe(0);

        // Staff see every thread.
        var staffList = await GetAsync("/api/chn/v1/messages", "staff-1", "Staff.Billing");
        staffList.Body!["items"]!.AsArray().Select(i => i!["thread"]!["threadId"]!.GetValue<string>()).ShouldContain(threadId);

        // The reply raised one unread notification for Alice only.
        var notes = await GetAsync("/api/chn/v1/notifications", a);
        notes.Body!["unreadCount"]!.GetValue<int>().ShouldBe(1);
        notes.Body["items"]![0]!["kind"]!.GetValue<string>().ShouldBe("MESSAGE_REPLY");
        (await GetAsync("/api/chn/v1/notifications", c)).Body!["items"]!.AsArray().Count.ShouldBe(0);
        (await GetAsync("/api/chn/v1/portal/dashboard", a)).Body!["unreadNotificationCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Mark_read_is_idempotent_and_touches_only_the_own_notifications_and_a_switched_off_class_raises_none()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);
        var thread = (await PostAsync("/api/chn/v1/messages/send", a, new { text = "ping" })).Body!["thread"]!["threadId"]!.GetValue<string>();
        (await PostAsync("/api/chn/v1/messages/reply", "staff-1", new { threadId = thread, text = "one" }, "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync("/api/chn/v1/messages/reply", "staff-1", new { threadId = thread, text = "two" }, "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var notes = (await GetAsync("/api/chn/v1/notifications", a)).Body!;
        notes["unreadCount"]!.GetValue<int>().ShouldBe(2);
        var ids = notes["items"]!.AsArray().Select(i => i!["notificationId"]!.GetValue<string>()).ToList();

        // Another customer marking Alice's ids changes nothing.
        (await PostAsync("/api/chn/v1/notifications/mark-read", c, new { notificationIds = ids })).Body!["updated"]!.GetValue<int>().ShouldBe(0);
        (await GetAsync("/api/chn/v1/notifications", a)).Body!["unreadCount"]!.GetValue<int>().ShouldBe(2);

        var firstRun = await PostAsync("/api/chn/v1/notifications/mark-read", a, new { notificationIds = new[] { ids[0] } });
        firstRun.Body!["updated"]!.GetValue<int>().ShouldBe(1);
        firstRun.Body["unreadCount"]!.GetValue<int>().ShouldBe(1);
        var again = await PostAsync("/api/chn/v1/notifications/mark-read", a, new { notificationIds = new[] { ids[0] } });
        again.Body!["updated"]!.GetValue<int>().ShouldBe(0, "marking read twice changes nothing");
        again.Body["unreadCount"]!.GetValue<int>().ShouldBe(1);
        (await PostAsync("/api/chn/v1/notifications/mark-read", a, new { all = true })).Body!["unreadCount"]!.GetValue<int>().ShouldBe(0);
        ((int)(await PostAsync("/api/chn/v1/notifications/mark-read", a, new { })).Response.StatusCode).ShouldBe(400);

        // Switch the class off: the next reply raises nothing.
        var set = await PostAsync("/api/chn/v1/notification-subscriptions/set", a, new { notificationClass = "MESSAGE", enabled = false });
        set.Response.StatusCode.ShouldBe(HttpStatusCode.OK, set.Text);
        set.Body!["subscriptions"]!.AsArray().Single(s => s!["notificationClass"]!.GetValue<string>() == "MESSAGE")!["enabled"]!.GetValue<bool>().ShouldBeFalse();
        (await PostAsync("/api/chn/v1/messages/reply", "staff-1", new { threadId = thread, text = "three" }, "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("/api/chn/v1/notifications", a)).Body!["unreadCount"]!.GetValue<int>().ShouldBe(0);
        (await GetAsync("/api/chn/v1/notification-subscriptions", a)).Body!["subscriptions"]!.AsArray().Count.ShouldBe(5);
        // The other customer is unaffected.
        (await GetAsync("/api/chn/v1/notification-subscriptions", c)).Body!["subscriptions"]!.AsArray().All(s => s!["enabled"]!.GetValue<bool>()).ShouldBeTrue();
    }

    [Fact]
    public async Task Owner_events_raise_one_notification_for_the_right_party_and_a_redelivery_is_a_no_op()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        await using (var scope = Factory().Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service("test-events");
            context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
            context.Jurisdiction = Jurisdiction.Parse("GR");
            var notifier = scope.ServiceProvider.GetRequiredService<PortalNotifier>();

            var renewal = Envelope("RenewalOffered", "Policy", PolicyCarol.Value.ToString("D"), ("policyId", PolicyCarol.Value.ToString("D")));
            var renewalHandler = new RenewalOfferedNotificationHandler(notifier);
            await renewalHandler.HandleAsync(renewal, null!, TestContext.Current.CancellationToken);
            await renewalHandler.HandleAsync(renewal, null!, TestContext.Current.CancellationToken);

            var invoice = Envelope("InvoiceIssued", "BillingAccount", BillingAccount.Value.ToString("D"), ("billingAccountId", BillingAccount.Value.ToString("D")));
            await new InvoiceIssuedNotificationHandler(notifier).HandleAsync(invoice, InvoicePayload(), TestContext.Current.CancellationToken);

            var claim = Envelope("ClaimUpdated", "Claim", Guid.NewGuid().ToString("D"), ("claimId", Guid.NewGuid().ToString("D")));
            var claimHandler = new ClaimUpdatedNotificationHandler(notifier);
            await claimHandler.HandleAsync(claim, new ClaimUpdatedV1 { ChangedAttributeGroups = ["status"], NewMilestone = "ASSESSED" }, TestContext.Current.CancellationToken);
            await claimHandler.HandleAsync(Envelope("ClaimUpdated", "Claim", Guid.NewGuid().ToString("D"), ("claimId", Guid.NewGuid().ToString("D"))),
                new ClaimUpdatedV1 { ChangedAttributeGroups = ["notes"] }, TestContext.Current.CancellationToken);
        }

        // Carol (policyholder of C) got the renewal; Alice (payer, insured of the claim) got the invoice and the claim, not the renewal.
        var carol = (await GetAsync("/api/chn/v1/notifications", c)).Body!["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>()).ToList();
        carol.ShouldBe(["RENEWAL_OFFERED"], "the redelivery created no second one");
        var alice = (await GetAsync("/api/chn/v1/notifications", a)).Body!["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>()).ToList();
        alice.ShouldBe(["INVOICE_ISSUED", "CLAIM_UPDATED"], ignoreOrder: true, "an update without a milestone or sub-status is not worth a notification");
    }

    private static InvoiceIssuedV1 InvoicePayload() => new()
    {
        InvoiceId = InvoiceId.New(), InvoiceNumber = InvoiceNumber.Parse("INV-EVT-1"), Kind = InvoiceIssuedV1.KindValue.Invoice, TermIds = [], TotalsByCategory = [],
        DueDate = new BusinessDate(new DateOnly(2026, 2, 1)), Method = "BANK_TRANSFER",
    };

    internal static EventEnvelope Envelope(string type, string aggregateType, string aggregateId, params (string Key, string Value)[] keys)
    {
        var businessKeys = BusinessKeys.Empty;
        foreach (var (key, value) in keys)
        {
            businessKeys = businessKeys.With(key, value);
        }

        return new EventEnvelope
        {
            EventId = EventId.New(), EventType = EventTypeName.Parse(type), SchemaVersion = "1.0", Producer = ModuleCode.POL, AggregateType = aggregateType, AggregateId = aggregateId,
            OccurredAt = Instant.FromUtcDateTime(DateTime.UtcNow), RecordedAt = Instant.FromUtcDateTime(DateTime.UtcNow), LegalEntity = LegalEntityCode.Parse("GR-TEST"),
            Jurisdiction = Jurisdiction.Parse("GR"), ConfigurationHash = ConfigurationHash.Parse(new string('a', 64)), BusinessKeys = businessKeys, CorrelationId = CorrelationId.New(),
            Actor = ActorRef.Service("test-events"), Origin = EventOrigin.Live, DataClassification = DataClassification.P0, Payload = new JsonObject(),
        };
    }
}

/// <summary>BO-CHN-04 with a matrix in force (the baseline lets customers pay and download on the web): pay-now link, download and the opened record.</summary>
public sealed class PortalEngagementMatrixTests(PostgresFixture database) : PortalEngagementHost(database), IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Pay_now_returns_the_link_for_an_open_own_invoice_and_nothing_to_pay_for_a_paid_one()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        var due = await PostAsync($"/api/chn/v1/portal/billing/invoices/{InvoiceIdOf(PolicyAlice, 1)}/pay-now", a);
        due.Response.StatusCode.ShouldBe(HttpStatusCode.OK, due.Text);
        due.Body!["decision"]!.GetValue<string>().ShouldBe("ALLOW");
        due.Body["payable"]!.GetValue<bool>().ShouldBeTrue();
        due.Body["payUrl"]!.GetValue<string>().ShouldBe($"/portal/billing/pay/{InvoiceIdOf(PolicyAlice, 1)}");

        var paid = await PostAsync($"/api/chn/v1/portal/billing/invoices/{InvoiceIdOf(PolicyAlice, 2)}/pay-now", a);
        paid.Body!["payable"]!.GetValue<bool>().ShouldBeFalse();
        paid.Body["payUrl"].ShouldBeNull();
        paid.Body["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain("NOTHING_TO_PAY");

        (await PostAsync($"/api/chn/v1/portal/billing/invoices/{InvoiceIdOf(PolicyAlice, 1)}/pay-now", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_streams_the_own_document_and_records_that_the_portal_opened_it()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(c, Carol);

        var first = await GetAsync($"/api/chn/v1/portal/documents/{DocAliceLetter:D}/download", a);
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await first.Response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("letter ");
        first.Response.Headers.GetValues("X-Content-SHA256").Single().ShouldBe(new string('a', 64));

        // A household member's document is allowed too; Carol cannot fetch Alice's.
        (await GetAsync($"/api/chn/v1/portal/documents/{DocBobLetter:D}/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync($"/api/chn/v1/portal/documents/{DocAliceLetter:D}/download", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/documents/{DocAliceLetter:D}/download", a)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await GetAsync("/api/chn/v1/portal/documents", a)).Body!["items"]!.AsArray().Single(i => i!["documentId"]!.GetValue<string>() == DocAliceLetter.ToString("D"))!;
        item["openCount"]!.GetValue<int>().ShouldBe(2);
        item["firstOpenedAt"].ShouldNotBeNull();
        (await GetAsync("/api/chn/v1/portal/documents", c)).Text.ShouldNotContain("firstOpenedAt");
    }
}
