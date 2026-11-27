using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>
/// BO-PTY-09: producer of record over HTTP. POL is the sandbox double (terms are set up per test), PTY is real: history ordering, refusals
/// (inactive intermediary, unappointed product, backdating, missing letter, authority), bulk transfer with a partial failure, at-renewal
/// transfers and cancel.
/// </summary>
public sealed class ProducerOfRecordTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private const string Product = "MOTOR-GR";

    private readonly FakePolicyTermService _terms = new();
    private readonly Dictionary<Guid, (PolicyId Policy, string? Code, TermStateCode State, DateTime Start, DateTime End, string Product)> _termSet = [];
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private static int _afmSeed = 10_000_000;

    public ValueTask InitializeAsync()
    {
        var canned = _terms.Canned<TermGetResponse>("pol.Term.get");
        _terms.Setup("pol.Term.get", call =>
        {
            var id = Guid.Parse((string)call.Arguments[0]!);
            if (!_termSet.TryGetValue(id, out var t))
            {
                throw new CoreIns.Platform.Errors.DomainException(CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.POL, "NOT-FOUND", "No such term."));
            }

            return canned with
            {
                Policy = canned.Policy with { PolicyId = t.Policy, ProductCode = t.Product },
                Term = canned.Term with
                {
                    TermId = new PolicyTermId(id), State = t.State, ProducerCode = t.Code,
                    Period = InstantRange.Of(Instant.FromUtcDateTime(t.Start), Instant.FromUtcDateTime(t.End)),
                },
            };
        });
        _factory = new ApiHostFactory(database.AppConnectionString).WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<CoreIns.Modules.Policy.Contracts.IPolicyTermService>(_ => _terms)));
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string Day(int offset) => DateTime.UtcNow.Date.AddDays(offset).ToString("yyyy-MM-dd");

    private static string NextAfm()
    {
        var digits = Interlocked.Increment(ref _afmSeed).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (digits[i] - '0') << (8 - i);
        }

        return digits + (sum % 11 % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Id, string Code)> IntermediaryAsync(string registerNumber)
    {
        var (_, party) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new
        {
            partyType = "ORGANISATION",
            organisation = new { legalName = "Πράκτορες " + registerNumber, tradeName = "Agency " + registerNumber },
            identifiers = new[] { new { scheme = "AFM", value = NextAfm() } },
        });
        var (created, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries", new
        {
            partyId = party.Text("party.partyId"),
            intermediaryType = "INSURANCE_AGENT",
            register = new
            {
                registerName = "Register", chamber = "Chamber", registerNumber, registrationCategory = "AGENT", registrationDate = "2020-01-15", registerStatus = "ACTIVE",
            },
            authorities = new { collectPremium = true, issueCoverNotes = false, bindWithinAuthority = true, serviceOnly = false },
            validFrom = "2026-01-01",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var id = body.Text("intermediary.intermediaryId");
        var (activated, active) = await SendAsync(_client, HttpMethod.Patch, $"/api/pty/v1/intermediaries/{id}", new { status = "ACTIVE", expectedRecordVersion = 1 });
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, active?.ToJsonString());
        return (id, body.Text("intermediary.producerCode"));
    }

    private (Guid Term, PolicyId Policy) Term(string? producerCode, TermStateCode state = TermStateCode.InForce, string product = Product)
    {
        var term = Guid.NewGuid();
        var policy = PolicyId.New();
        _termSet[term] = (policy, producerCode, state, DateTime.UtcNow.Date.AddDays(-30), DateTime.UtcNow.Date.AddDays(335), product);
        return (term, policy);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ChangeAsync(
        Guid term, string code, string? effective, string roles = Manager, string? letter = null, bool atRenewal = false) =>
        SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-of-records/change", new
        {
            termId = term,
            producerCode = code,
            effectiveDate = effective,
            atRenewal,
            reason = letter is null ? "CUSTOMER_REQUEST" : "BROKER_OF_RECORD_LETTER",
            commissionConsequence = "NEW_PRODUCER_FROM_DATE",
            letterReference = letter,
        }, roles);

    private async Task<JsonNode?> GetAsync(PolicyId policy, string? date = null)
    {
        var (response, body) = await SendAsync(_client, HttpMethod.Get, $"/api/pty/v1/producer-of-records/{policy.Value}" + (date is null ? "" : "?date=" + date), roles: Manager, withKey: false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private async Task AppointAsync(string intermediaryId, string productCode)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var insert = dataSource.CreateCommand(
            $"INSERT INTO pty.intermediary_appointment (appointment_id, intermediary_id, legal_entity_id, product_code, valid_from, valid_to) VALUES (gen_random_uuid(), '{intermediaryId}', '{ApiHostFactory.LegalEntityId}', '{productCode}', '2026-01-01', NULL)");
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_change_keeps_the_history_in_order_and_needs_a_letter_for_another_intermediary()
    {
        var (_, first) = await IntermediaryAsync("POR-0001");
        var (_, second) = await IntermediaryAsync("POR-0002");
        var (term, policy) = Term(first);

        // The term's own producer code is the producer of record until a change is recorded.
        (await GetAsync(policy)).Text("history").ShouldBe("[]");

        var (noLetter, noLetterBody) = await ChangeAsync(term, second, Day(1));
        noLetter.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, noLetterBody?.ToJsonString());
        noLetterBody.Text("code").ShouldBe("PTY-ERR-POR-LETTER-REQUIRED");

        var (changed, body) = await ChangeAsync(term, second, Day(1), letter: "BOR-2026-17");
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("entry.producerCode").ShouldBe(second);
        body.Text("entry.validFrom").ShouldBe(Day(1));
        body.Text("previousProducerCode").ShouldBe(first);

        var history = await GetAsync(policy);
        history.Text("history.0.producerCode").ShouldBe(first);
        history.Text("history.0.validFrom").ShouldBe(Day(-30));
        history.Text("history.0.validTo").ShouldBe(Day(1));
        history.Text("history.1.producerCode").ShouldBe(second);
        history.Text("history.1.validTo").ShouldBe("null");
        history.Text("current.producerCode").ShouldBe(first);
        (await GetAsync(policy, Day(5))).Text("current.producerCode").ShouldBe(second);

        // A later change supersedes the open segment and keeps it as history; the new segment starts where asked.
        var (again, againBody) = await ChangeAsync(term, "x", Day(10), letter: "BOR-2026-18");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        againBody.Text("code").ShouldBe("PTY-ERR-POR-CODE-INVALID");
        var (same, sameBody) = await ChangeAsync(term, second, Day(10));
        same.StatusCode.ShouldBe(HttpStatusCode.Conflict, sameBody?.ToJsonString());
        sameBody.Text("code").ShouldBe("PTY-ERR-STATE");

        var back = await ChangeAsync(term, first, Day(10), letter: "BOR-2026-19");
        back.Response.StatusCode.ShouldBe(HttpStatusCode.OK, back.Body?.ToJsonString());
        var after = await GetAsync(policy);
        var rows = after!["history"]!.AsArray().Select(r => (r!["producerCode"]!.GetValue<string>(), r["validFrom"]!.GetValue<string>(), r["status"]!.GetValue<string>())).ToList();
        rows.Select(r => r.Item2).ShouldBe(rows.Select(r => r.Item2).Order().ToList());
        rows.Count(r => r.Item3 == "SUPERSEDED").ShouldBe(1);
        (await GetAsync(policy, Day(12))).Text("current.producerCode").ShouldBe(first);
        (await GetAsync(policy, Day(5))).Text("current.producerCode").ShouldBe(second);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var events = dataSource.CreateCommand(
            $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ProducerOfRecordChanged' AND aggregate_id = '{term}'");
        Convert.ToInt32(await events.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Refuses_a_suspended_intermediary_an_unappointed_product_a_backdated_date_and_a_caller_without_authority()
    {
        var (_, current) = await IntermediaryAsync("POR-0011");
        var (suspendedId, suspended) = await IntermediaryAsync("POR-0012");
        var (appointedId, appointed) = await IntermediaryAsync("POR-0013");
        var (term, _) = Term(current);

        var (suspend, suspendBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/suspend", new { intermediaryId = suspendedId, expectedRecordVersion = 2, reason = "Licence review" });
        suspend.StatusCode.ShouldBe(HttpStatusCode.OK, suspendBody?.ToJsonString());
        var refusedSuspended = await ChangeAsync(term, suspended, Day(1), letter: "L1");
        refusedSuspended.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        refusedSuspended.Body.Text("code").ShouldBe("PTY-ERR-POR-CODE-INVALID");
        refusedSuspended.Body.Text("detail").ShouldContain("INTERMEDIARY_NOT_ACTIVE");

        await AppointAsync(appointedId, "HOME-GR");
        var refusedProduct = await ChangeAsync(term, appointed, Day(1), letter: "L2");
        refusedProduct.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        refusedProduct.Body.Text("detail").ShouldContain("PRODUCT_NOT_APPOINTED");

        var (_, other) = await IntermediaryAsync("POR-0014");
        var backdated = await ChangeAsync(term, other, Day(-29), letter: "L3");
        backdated.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        backdated.Body.Text("code").ShouldBe("PTY-ERR-BACKDATE");
        var outside = await ChangeAsync(term, other, Day(400), letter: "L4");
        outside.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Changing the producer of record is a manager authority: an underwriter and an anonymous caller are refused.
        (await ChangeAsync(term, other, Day(1), roles: Underwriter, letter: "L5")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ChangeAsync(term, other, Day(1), roles: Billing, letter: "L5")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ChangeAsync(term, other, Day(1), letter: "L5")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bulk_transfer_reports_each_term_and_a_refused_term_does_not_stop_the_others()
    {
        var (_, from) = await IntermediaryAsync("POR-0021");
        var (_, to) = await IntermediaryAsync("POR-0022");
        var ok1 = Term(from);
        var ok2 = Term(from);
        var cancelled = Term(from, TermStateCode.Cancelled);
        var missing = Guid.NewGuid();

        var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-of-records/bulk-transfer", new
        {
            terms = new[] { ok1.Term, cancelled.Term, ok2.Term, missing }.Select(t => new { termId = t }),
            producerCode = to,
            effectiveDate = Day(2),
            reason = "PORTFOLIO_TRANSFER",
            commissionConsequence = "NEW_PRODUCER_FROM_DATE",
            letterReference = "BULK-1",
        }, Manager);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("transferred").ShouldBe("2");
        body.Text("refused").ShouldBe("2");
        var results = body!["results"]!.AsArray().ToDictionary(r => r!["termId"]!.GetValue<string>(), r => r!["outcome"]!.GetValue<string>());
        results[ok1.Term.ToString()].ShouldBe("TRANSFERRED");
        results[ok2.Term.ToString()].ShouldBe("TRANSFERRED");
        results[cancelled.Term.ToString()].ShouldBe("REFUSED");
        results[missing.ToString()].ShouldBe("REFUSED");

        var moved = await GetAsync(ok1.Policy, Day(3));
        moved.Text("current.producerCode").ShouldBe(to);
        moved.Text("current.transferId").ShouldBe(body.Text("transferId"));
        (await GetAsync(cancelled.Policy)).Text("history").ShouldBe("[]");

        // A transfer of an unappointed product is refused per term, not for the whole request.
        var (restrictedId, restrictedCode) = await IntermediaryAsync("POR-0023");
        await AppointAsync(restrictedId, "HOME-GR");
        var (_, partial) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-of-records/bulk-transfer", new
        {
            terms = new[] { new { termId = Term(from).Term }, new { termId = Term(from, product: "HOME-GR").Term } },
            producerCode = restrictedCode,
            effectiveDate = Day(2),
            reason = "PORTFOLIO_TRANSFER",
            commissionConsequence = "NEW_PRODUCER_FROM_DATE",
            letterReference = "BULK-2",
        }, Manager);
        partial.Text("transferred").ShouldBe("1");
        partial.Text("refused").ShouldBe("1");
    }

    [Fact]
    public async Task An_at_renewal_change_starts_after_the_term_and_a_future_change_can_be_cancelled()
    {
        var (_, first) = await IntermediaryAsync("POR-0031");
        var (_, second) = await IntermediaryAsync("POR-0032");
        var (term, policy) = Term(first);

        var (changed, body) = await ChangeAsync(term, second, null, letter: "BOR-R1", atRenewal: true);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var renewalDate = body.Text("entry.validFrom");
        renewalDate.ShouldBe(Day(335));
        (await GetAsync(policy, Day(100))).Text("current.producerCode").ShouldBe(first);
        (await GetAsync(policy, Day(336))).Text("current.producerCode").ShouldBe(second);

        var (cancelled, cancelBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-of-records/cancel",
            new { porId = body.Text("entry.porId"), reason = "Customer stayed" }, Manager);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, cancelBody?.ToJsonString());
        (await GetAsync(policy, Day(336))).Text("current.producerCode").ShouldBe(first);
        var statuses = cancelBody!["history"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()).ToList();
        statuses.ShouldContain("SUPERSEDED");

        // A change already in force cannot be cancelled.
        var inForce = await ChangeAsync(term, second, Day(0), letter: "BOR-R2");
        inForce.Response.StatusCode.ShouldBe(HttpStatusCode.OK, inForce.Body?.ToJsonString());
        var (refused, refusedBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-of-records/cancel",
            new { porId = inForce.Body.Text("entry.porId"), reason = "Too late" }, Manager);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, refusedBody?.ToJsonString());
        refusedBody.Text("code").ShouldBe("PTY-ERR-STATE");
    }
}
