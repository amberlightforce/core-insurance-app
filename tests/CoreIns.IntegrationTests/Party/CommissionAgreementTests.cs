using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-10: commission agreement versions, maker-checker approval, backdating, intermediary status and compare over HTTP.</summary>
public sealed class CommissionAgreementTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";

    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private static string DaysFromToday(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd");

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> AsAsync(string user, string roles, HttpMethod method, string path, object? body = null)
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

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> ActiveIntermediaryAsync(string afm, string registerNumber)
    {
        var (_, party) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new
        {
            partyType = "ORGANISATION",
            organisation = new { legalName = "Πράκτορες " + registerNumber, tradeName = "Agency " + registerNumber },
            identifiers = new[] { new { scheme = "AFM", value = afm } },
        });
        var (created, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries", new
        {
            partyId = party.Text("party.partyId"),
            intermediaryType = "INSURANCE_AGENT",
            register = new
            {
                registerName = "Register", chamber = "Chamber", registerNumber, registrationCategory = "AGENT", registrationDate = "2020-01-15",
                registerStatus = "ACTIVE",
            },
            authorities = new { collectPremium = true, issueCoverNotes = false, bindWithinAuthority = true, serviceOnly = false },
            validFrom = "2026-01-01",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var id = body.Text("intermediary.intermediaryId");
        var (activated, active) = await SendAsync(_client, HttpMethod.Patch, $"/api/pty/v1/intermediaries/{id}", new { status = "ACTIVE", expectedRecordVersion = 1 });
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, active?.ToJsonString());
        return id;
    }

    private static object[] Lines(string newBusiness = "12.5", string renewal = "10") =>
    [
        new { businessType = "NEW_BUSINESS", lineOfBusiness = (string?)null, basis = "NET_PREMIUM", ratePct = newBusiness },
        new { businessType = "RENEWAL", lineOfBusiness = (string?)null, basis = "NET_PREMIUM", ratePct = renewal },
    ];

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CreateVersionAsync(
        string intermediaryId, string effectiveFrom, object? lines = null, string volume = "100000.00", string? product = null) =>
        AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/create-version", new
        {
            intermediaryId,
            productCode = product,
            effectiveFrom,
            expectedAnnualVolume = new { amount = volume, currency = "EUR" },
            rateLines = lines ?? Lines(),
        });

    private async Task<string> ActiveVersionAsync(string intermediaryId, string effectiveFrom, object? lines = null, string? product = null)
    {
        var (created, body) = await CreateVersionAsync(intermediaryId, effectiveFrom, lines, product: product);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        var versionId = body.Text("version.versionId");
        (await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/submit", new { versionId })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (approved, approvedBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        return versionId;
    }

    [Fact]
    public async Task A_version_goes_from_draft_to_pending_to_active_and_the_maker_cannot_decide()
    {
        var intermediary = await ActiveIntermediaryAsync("094014201", "CA-0001");
        var (created, body) = await CreateVersionAsync(intermediary, Today);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body.Text("version.status").ShouldBe("DRAFT");
        body.Text("version.versionNo").ShouldBe("1");
        body.Text("version.maxRatePct").ShouldBe("12.5");
        var versionId = body.Text("version.versionId");
        var agreementId = body.Text("agreement.agreementId");

        // A draft cannot be approved, and a second open version is refused.
        var (early, earlyBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE" });
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict, earlyBody?.ToJsonString());
        earlyBody.Text("code").ShouldBe("PTY-ERR-STATE");
        (await CreateVersionAsync(intermediary, DaysFromToday(30))).Body.Text("code").ShouldBe("PTY-ERR-STATE");

        var (submitted, submitBody) = await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/submit", new { versionId });
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submitBody?.ToJsonString());
        submitBody.Text("version.status").ShouldBe("PENDING_APPROVAL");
        (await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/submit", new { versionId })).Body.Text("code").ShouldBe("PTY-ERR-STATE");

        // Maker is not the checker, even with the checker role.
        var (self, selfBody) = await AsAsync("maker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE" });
        self.IsSuccessStatusCode.ShouldBeFalse();
        selfBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");

        var (approved, approvedBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE", comment = "ok" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody.Text("version.status").ShouldBe("ACTIVE");
        approvedBody.Text("version.decidedBy").ShouldNotBe(approvedBody.Text("version.submittedBy"));

        var (got, getBody) = await AsAsync("maker", Underwriter, HttpMethod.Get, $"/api/pty/v1/commission-agreements/{agreementId}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, getBody?.ToJsonString());
        getBody.Text("agreement.versions.0.status").ShouldBe("ACTIVE");
        (await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE" })).Body.Text("code").ShouldBe("PTY-ERR-STATE");
    }

    [Fact]
    public async Task A_checker_without_authority_for_the_volume_or_the_rate_band_is_refused_and_a_rejection_ends_the_version()
    {
        var intermediary = await ActiveIntermediaryAsync("123456783", "CA-0002");
        // Volume above the underwriter's limit: a different underwriter cannot decide, the manager can.
        var (created, body) = await CreateVersionAsync(intermediary, Today, volume: "900000.00");
        var versionId = body.Text("version.versionId");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/submit", new { versionId });
        var (denied, deniedBody) = await AsAsync("peer", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE" });
        denied.IsSuccessStatusCode.ShouldBeFalse();
        deniedBody.Text("code").ShouldBeOneOf("PLT-ERR-AUTHORITY-REFERRAL-REQUIRED", "PLT-ERR-AUTHORITY-DENIED");

        var (rejected, rejectedBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "REJECT", comment = "Too generous" });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("version.status").ShouldBe("REJECTED");

        // A rejected version frees the agreement for a new one; its numbering continues.
        var (again, againBody) = await CreateVersionAsync(intermediary, Today);
        again.StatusCode.ShouldBe(HttpStatusCode.Created, againBody?.ToJsonString());
        againBody.Text("version.versionNo").ShouldBe("2");
    }

    [Fact]
    public async Task Backdating_and_rates_above_the_maximum_are_refused()
    {
        var intermediary = await ActiveIntermediaryAsync("094259216", "CA-0003");
        var past = DateTime.UtcNow.AddMonths(-2).ToString("yyyy-MM-01");
        var (backdated, backdatedBody) = await CreateVersionAsync(intermediary, past);
        backdated.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, backdatedBody?.ToJsonString());
        backdatedBody.Text("code").ShouldBe("PTY-ERR-BACKDATE");

        var (high, highBody) = await CreateVersionAsync(intermediary, Today, Lines(newBusiness: "45"));
        high.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, highBody?.ToJsonString());
        highBody.Text("code").ShouldBe("PTY-ERR-RATE-ABOVE-MAX");

        var (precise, preciseBody) = await CreateVersionAsync(intermediary, Today, Lines(newBusiness: "12.12345"));
        precise.StatusCode.ShouldBe(HttpStatusCode.BadRequest, preciseBody?.ToJsonString());
        preciseBody.Text("code").ShouldBe("PTY-ERR-VALIDATION");

        // A new version must also start after the latest active one.
        await ActiveVersionAsync(intermediary, DaysFromToday(10));
        var (order, orderBody) = await CreateVersionAsync(intermediary, DaysFromToday(5));
        order.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, orderBody?.ToJsonString());
        orderBody.Text("code").ShouldBe("PTY-ERR-BACKDATE");
    }

    [Fact]
    public async Task A_suspended_intermediary_cannot_get_an_agreement_or_have_a_pending_one_approved()
    {
        var intermediary = await ActiveIntermediaryAsync("090000045", "CA-0004");
        var (created, body) = await CreateVersionAsync(intermediary, Today);
        var versionId = body.Text("version.versionId");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        await AsAsync("maker", Underwriter, HttpMethod.Post, "/api/pty/v1/commission-agreements/submit", new { versionId });

        var (suspended, suspendBody) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries/suspend",
            new { intermediaryId = intermediary, expectedRecordVersion = 2, reason = "Licence review" });
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK, suspendBody?.ToJsonString());

        var (approve, approveBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "APPROVE" });
        approve.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, approveBody?.ToJsonString());
        approveBody.Text("code").ShouldBe("PTY-ERR-INTERMEDIARY-NOT-ACTIVE");

        var (other, otherBody) = await CreateVersionAsync(intermediary, Today, product: "MOTOR_PRIVATE_CAR");
        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, otherBody?.ToJsonString());
        otherBody.Text("code").ShouldBe("PTY-ERR-INTERMEDIARY-NOT-ACTIVE");

        // The pending version can still be rejected.
        var (rejected, rejectedBody) = await AsAsync("checker", Manager, HttpMethod.Post, "/api/pty/v1/commission-agreements/approve", new { versionId, decision = "REJECT", comment = "Not now" });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
    }

    [Fact]
    public async Task Compare_shows_changed_added_and_removed_rate_lines_and_the_old_version_gets_an_end_date()
    {
        var intermediary = await ActiveIntermediaryAsync("094014298", "CA-0005");
        var first = await ActiveVersionAsync(intermediary, DaysFromToday(1));
        var secondLines = new[]
        {
            new { businessType = "NEW_BUSINESS", lineOfBusiness = (string?)null, basis = "NET_PREMIUM", ratePct = "14.0000" },
            new { businessType = "ANY", lineOfBusiness = (string?)"MOTOR", basis = "GROSS_PREMIUM", ratePct = "8" },
        };
        var second = await ActiveVersionAsync(intermediary, DaysFromToday(40), secondLines);

        var (compared, compareBody) = await AsAsync("maker", Underwriter, HttpMethod.Get, $"/api/pty/v1/commission-agreements/compare?fromVersionId={first}&toVersionId={second}");
        compared.StatusCode.ShouldBe(HttpStatusCode.OK, compareBody?.ToJsonString());
        compareBody.Text("identical").ShouldBe("false");
        var changes = compareBody!["rateLineChanges"]!.AsArray().Select(c => $"{c!["kind"]}:{c["businessType"]}").Order().ToList();
        changes.ShouldBe(["ADDED:ANY", "CHANGED:NEW_BUSINESS", "REMOVED:RENEWAL"]);
        compareBody["fieldChanges"]!.AsArray().Select(c => c!["field"]!.GetValue<string>()).ShouldContain("effectiveFrom");

        var (same, sameBody) = await AsAsync("maker", Underwriter, HttpMethod.Get, $"/api/pty/v1/commission-agreements/compare?fromVersionId={first}&toVersionId={first}");
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
        sameBody.Text("identical").ShouldBe("true");

        var (missing, _) = await AsAsync("maker", Underwriter, HttpMethod.Get, $"/api/pty/v1/commission-agreements/compare?fromVersionId={first}&toVersionId={Guid.NewGuid()}");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The first version ends the day before the second starts.
        var (_, agreement) = await AsAsync("maker", Underwriter, HttpMethod.Get, $"/api/pty/v1/commission-agreements/{compareBody.Text("agreementId")}");
        agreement.Text("agreement.versions.0.effectiveTo").ShouldBe(DaysFromToday(39));
        agreement.Text("agreement.versions.1.effectiveTo").ShouldBe("null");
    }
}
