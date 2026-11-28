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
/// BO-PTY-12: delegated agency administration over HTTP. An agency administrator acts only inside their own agency; grants and role changes are
/// pushed to PLT as ExternalUserAccessChanged; removing a producer re-assigns the book through the producer-of-record rules (POL is the sandbox
/// double, PTY is real).
/// </summary>
public sealed class AgencyAdminTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Staff = "Staff.UnderwritingManager";
    private const string AgencyAdmin = "Agency.Administrator";
    private const string Product = "MOTOR-GR";

    private readonly FakePolicyTermService _terms = new();
    private readonly Dictionary<Guid, (PolicyId Policy, string? Code, DateTime Start, DateTime End)> _termSet = [];
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private static int _afmSeed = 20_000_000;

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
                Policy = canned.Policy with { PolicyId = t.Policy, ProductCode = Product },
                Term = canned.Term with
                {
                    TermId = new PolicyTermId(id), State = TermStateCode.InForce, ProducerCode = t.Code,
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

    private async Task<(string Id, string Code)> AgencyAsync(string registerNumber)
    {
        var (_, party) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new
        {
            partyType = "ORGANISATION",
            organisation = new { legalName = "Πρακτορείο " + registerNumber, tradeName = "Agency " + registerNumber },
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

    /// <summary>Adds another active producer code to an agency (there is no create operation for a second code yet).</summary>
    private async Task<string> ExtraCodeAsync(string intermediaryId, string code)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var insert = dataSource.CreateCommand(
            "INSERT INTO pty.producer_code (producer_code_id, intermediary_id, legal_entity_id, code, status, collect_premium, issue_cover_notes, bind_within_authority, service_only, valid_from, valid_to, record_version, created_at, created_by) "
            + $"VALUES (gen_random_uuid(), '{intermediaryId}', '{ApiHostFactory.LegalEntityId}', '{code}', 'ACTIVE', true, false, true, false, '2026-01-01', NULL, 1, now(), 'test')");
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return code;
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> CallAsync(HttpMethod method, string path, object? body, string roles, string? user = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> UserAsync(string agency, string subject, string role, string[]? codes = null, string roles = Staff, string? actor = null)
    {
        var (response, body) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users",
            new { intermediaryId = agency, externalSubject = subject, displayName = "User " + subject, role, producerCodes = codes ?? [] }, roles, actor);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("user.agencyUserId");
    }

    private async Task<int> EventCountAsync(string eventType, string aggregateId)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var count = dataSource.CreateCommand($"SELECT count(*) FROM plt.outbox_message WHERE event_type = '{eventType}' AND aggregate_id = '{aggregateId}'");
        return Convert.ToInt32(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agency_administrator_cannot_act_outside_their_own_agency()
    {
        var (agencyA, codeA) = await AgencyAsync("AGY-0001");
        var (agencyB, codeB) = await AgencyAsync("AGY-0002");
        await UserAsync(agencyA, "adminA", "AGENCY_ADMIN");
        await UserAsync(agencyB, "adminB", "AGENCY_ADMIN");
        var userB = await UserAsync(agencyB, "producerB", "PRODUCER", [codeB]);

        // Inside the own agency the administrator can add a user and grant an own code.
        var userA = await UserAsync(agencyA, "producerA", "PRODUCER", roles: AgencyAdmin, actor: "adminA");
        var (granted, grantedBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/grant-access", new { agencyUserId = userA, producerCodes = new[] { codeA } }, AgencyAdmin, "adminA");
        granted.StatusCode.ShouldBe(HttpStatusCode.OK, grantedBody?.ToJsonString());
        grantedBody.Text("user.producerCodes.0").ShouldBe(codeA);

        // Another agency: create, list, a user of it, and a code of it for an own user are all refused.
        var (create, createBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users",
            new { intermediaryId = agencyB, externalSubject = "intruder", displayName = "Intruder", role = "AGENCY_STAFF" }, AgencyAdmin, "adminA");
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden, createBody?.ToJsonString());
        createBody.Text("code").ShouldBe("PTY-ERR-OUT-OF-AGENCY");
        (await CallAsync(HttpMethod.Get, "/api/pty/v1/agency-users?intermediaryId=" + agencyB, null, AgencyAdmin, "adminA")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var onOther = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/grant-access", new { agencyUserId = userB, producerCodes = new[] { codeB } }, AgencyAdmin, "adminA");
        onOther.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        onOther.Body.Text("code").ShouldBe("PTY-ERR-OUT-OF-AGENCY");
        var otherCode = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/grant-access", new { agencyUserId = userA, producerCodes = new[] { codeB } }, AgencyAdmin, "adminA");
        otherCode.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        otherCode.Body.Text("code").ShouldBe("PTY-ERR-OUT-OF-AGENCY");
        (await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/suspend", new { agencyUserId = userB, reason = "x" }, AgencyAdmin, "adminA")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A caller who administers no agency at all, and a producer of the agency, are refused too.
        (await CallAsync(HttpMethod.Get, "/api/pty/v1/agency-users?intermediaryId=" + agencyA, null, AgencyAdmin, "nobody")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CallAsync(HttpMethod.Get, "/api/pty/v1/agency-users?intermediaryId=" + agencyA, null, AgencyAdmin, "producerA")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Own agency list works; a role without the permission gets 403.
        var (list, listBody) = await CallAsync(HttpMethod.Get, "/api/pty/v1/agency-users?intermediaryId=" + agencyA, null, AgencyAdmin, "adminA");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, listBody?.ToJsonString());
        listBody!["users"]!.AsArray().Count.ShouldBe(2);
        (await CallAsync(HttpMethod.Get, "/api/pty/v1/agency-users?intermediaryId=" + agencyA, null, "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Grants_roles_and_suspension_are_pushed_to_PLT_and_the_last_administrator_is_protected()
    {
        var (agency, code) = await AgencyAsync("AGY-0011");
        var second = await ExtraCodeAsync(agency, code + "-B");
        var admin = await UserAsync(agency, "adminC", "AGENCY_ADMIN");
        var producer = await UserAsync(agency, "producerC", "PRODUCER", [code]);
        var userKey = Guid.Parse(producer).ToString("D");

        // Initial grant was pushed once, with the code added.
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        async Task<string> LastPayloadAsync()
        {
            await using var cmd = dataSource.CreateCommand(
                "SELECT payload::text FROM plt.outbox_message WHERE event_type = 'ExternalUserAccessChanged' AND payload::text LIKE '%" + userKey + "%' ORDER BY aggregate_sequence DESC LIMIT 1");
            return (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? string.Empty;
        }

        (await LastPayloadAsync()).ShouldContain(code);

        var (grant, grantBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/grant-access", new { agencyUserId = producer, producerCodes = new[] { second, code } }, Staff);
        grant.StatusCode.ShouldBe(HttpStatusCode.OK, grantBody?.ToJsonString());
        grantBody!["user"]!["producerCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray().ShouldBe([code, second]);
        var payload = await LastPayloadAsync();
        payload.ShouldContain(second);
        payload.ShouldNotContain($"\"{code}\"");

        var (revoke, revokeBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/revoke-access", new { agencyUserId = producer, producerCodes = new[] { second } }, Staff);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, revokeBody?.ToJsonString());
        revokeBody!["user"]!["producerCodes"]!.AsArray().Count.ShouldBe(1);

        // Suspension drops every grant in PLT; reactivation restores them; a suspended user cannot be granted more.
        var (suspend, suspendBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/suspend", new { agencyUserId = producer, reason = "Attestation overdue" }, Staff);
        suspend.StatusCode.ShouldBe(HttpStatusCode.OK, suspendBody?.ToJsonString());
        suspendBody.Text("user.status").ShouldBe("SUSPENDED");
        (await LastPayloadAsync()).ShouldContain("grantsRemoved");
        (await LastPayloadAsync()).ShouldContain(code);
        var whileSuspended = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/grant-access", new { agencyUserId = producer, producerCodes = new[] { second } }, Staff);
        whileSuspended.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (reactivate, reactivateBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/reactivate", new { agencyUserId = producer, reason = "Attested" }, Staff);
        reactivate.StatusCode.ShouldBe(HttpStatusCode.OK, reactivateBody?.ToJsonString());
        reactivateBody.Text("user.status").ShouldBe("ACTIVE");
        reactivateBody.Text("user.producerCodes.0").ShouldBe(code);

        // Role change: a producer becomes an office manager; the only administrator cannot be demoted or suspended.
        var (role, roleBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/change-role", new { agencyUserId = producer, role = "OFFICE_MANAGER" }, Staff);
        role.StatusCode.ShouldBe(HttpStatusCode.OK, roleBody?.ToJsonString());
        roleBody.Text("user.role").ShouldBe("OFFICE_MANAGER");
        var demote = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/change-role", new { agencyUserId = admin, role = "PRODUCER" }, Staff);
        demote.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        demote.Body.Text("code").ShouldBe("PTY-ERR-LAST-ADMIN");
        var suspendLast = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/suspend", new { agencyUserId = admin, reason = "x" }, Staff);
        suspendLast.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        suspendLast.Body.Text("code").ShouldBe("PTY-ERR-LAST-ADMIN");

        // Every action was audited with the agency and the target user.
        await using var audit = dataSource.CreateCommand(
            $"SELECT count(*) FROM plt.audit_event WHERE object_type = 'AgencyUser' AND object_id = '{producer}'");
        Convert.ToInt32(await audit.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task Removing_a_producer_needs_a_re_assignment_and_moves_the_whole_book()
    {
        var (agency, baseCode) = await AgencyAsync("AGY-0021");
        var (_, foreignCode) = await AgencyAsync("AGY-0022");
        var producerCode = await ExtraCodeAsync(agency, baseCode + "-P");
        await UserAsync(agency, "adminD", "AGENCY_ADMIN");
        var producer = await UserAsync(agency, "producerD", "PRODUCER", [producerCode]);

        // Two policies whose producer of record is the producer's code.
        var book = new List<(Guid Term, PolicyId Policy)>();
        for (var i = 0; i < 2; i++)
        {
            var term = Guid.NewGuid();
            var policy = PolicyId.New();
            _termSet[term] = (policy, baseCode, DateTime.UtcNow.Date.AddDays(-30), DateTime.UtcNow.Date.AddDays(335));
            var (changed, changedBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/producer-of-records/change", new
            {
                termId = term, producerCode, effectiveDate = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd"), reason = "CUSTOMER_REQUEST", commissionConsequence = "NEW_PRODUCER_FROM_DATE",
            }, Staff);
            changed.StatusCode.ShouldBe(HttpStatusCode.OK, changedBody?.ToJsonString());
            book.Add((term, policy));
        }

        // No re-assignment code, the producer's own code, and another agency's code are all refused; the producer stays.
        var missing = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/remove-producer", new { agencyUserId = producer, reason = "Left the agency" }, Staff);
        missing.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, missing.Body?.ToJsonString());
        var own = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/remove-producer", new { agencyUserId = producer, reassignToProducerCode = producerCode, reason = "Left" }, Staff);
        own.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, own.Body?.ToJsonString());
        own.Body.Text("code").ShouldBe("PTY-ERR-REASSIGNMENT-REQUIRED");
        var foreign = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/remove-producer", new { agencyUserId = producer, reassignToProducerCode = foreignCode, reason = "Left" }, Staff);
        foreign.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreign.Body.Text("code").ShouldBe("PTY-ERR-OUT-OF-AGENCY");

        // One term can no longer be moved (POL no longer knows it): the other moves, the removal is incomplete and can be repeated.
        var gone = book[1].Term;
        var kept = _termSet[gone];
        _termSet.Remove(gone);
        var (partial, partialBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/remove-producer", new { agencyUserId = producer, reassignToProducerCode = baseCode, reason = "Left" }, Staff);
        partial.StatusCode.ShouldBe(HttpStatusCode.OK, partialBody?.ToJsonString());
        partialBody.Text("removed").ShouldBe("false");
        partialBody.Text("transferred").ShouldBe("1", partialBody?.ToJsonString());
        partialBody.Text("refused").ShouldBe("1");
        partialBody.Text("user.status").ShouldBe("ACTIVE");

        _termSet[gone] = kept;
        var (done, doneBody) = await CallAsync(HttpMethod.Post, "/api/pty/v1/agency-users/remove-producer", new { agencyUserId = producer, reassignToProducerCode = baseCode, reason = "Left" }, Staff);
        done.StatusCode.ShouldBe(HttpStatusCode.OK, doneBody?.ToJsonString());
        doneBody.Text("removed").ShouldBe("true");
        doneBody.Text("refused").ShouldBe("0");
        doneBody.Text("user.status").ShouldBe("REMOVED");
        doneBody!["user"]!["producerCodes"]!.AsArray().Count.ShouldBe(0);

        foreach (var (_, policy) in book)
        {
            var (get, history) = await CallAsync(HttpMethod.Get, $"/api/pty/v1/producer-of-records/{policy.Value}?date={DateTime.UtcNow.Date.AddDays(5):yyyy-MM-dd}", null, Staff);
            get.StatusCode.ShouldBe(HttpStatusCode.OK, history?.ToJsonString());
            history.Text("current.producerCode").ShouldBe(baseCode);
        }

        // A removed user no longer shows in the agency list, and the push to PLT dropped the grant.
        var (list, listBody) = await CallAsync(HttpMethod.Get, "/api/pty/v1/agency-users?intermediaryId=" + agency, null, Staff);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        listBody!["users"]!.AsArray().Select(u => u!["agencyUserId"]!.GetValue<string>()).ShouldNotContain(producer);
    }
}
