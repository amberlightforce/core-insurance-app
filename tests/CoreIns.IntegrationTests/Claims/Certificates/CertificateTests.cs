using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.Certificates;

/// <summary>
/// BO-CLM-07: the claims-history certificate (request, assemble, issue) over HTTP on a real PostgreSQL: withdrawn claims are left out,
/// issue is idempotent (one number, one document, one event), a data-subject restriction hides the claims, and an unverified look-back
/// is refused in Production.
/// </summary>
public sealed class CertificateTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<(HttpResponseMessage Response, JsonNode? Body)> StepAsync(string step, object body, string roles = Manager, Guid? key = null) =>
        _money.SendAsync(HttpMethod.Post, $"/api/clm/v1/certificates/{step}", body, roles, ManagerUser, key);

    private async Task<string> RequestAsync(string party, object? extra = null)
    {
        var body = new JsonObject { ["requesterPartyId"] = party };
        foreach (var property in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra ?? new { }))!.AsObject())
        {
            body[property.Key] = property.Value?.DeepClone();
        }

        var (response, result) = await StepAsync("request", body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, result?.ToJsonString());
        result.Text("certificate.status").ShouldBe("REQUESTED");
        return result.Text("certificate.certificateId");
    }

    private async Task<MoneyClaim> OpenAsync(ScriptedPolicy policy, int daysAgo, string cause)
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy, ClaimsSlice.DaysAgo(daysAgo), cause));
        return claim;
    }

    private async Task WithdrawAsync(MoneyClaim claim)
    {
        var version = int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), CultureInfo.InvariantCulture);
        var (response, body) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/claims/close", new { claimId = claim.ClaimId, expectedRecordVersion = version, outcome = "WITHDRAWN" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    [Fact]
    public async Task Assemble_excludes_withdrawn_claims_and_issue_is_idempotent()
    {
        var policy = _slice.Policy();
        var kept = await OpenAsync(policy, 20, "COLLISION");
        var withdrawn = await OpenAsync(policy, 10, "FIRE");
        await WithdrawAsync(withdrawn);
        var party = kept.InsuredPartyId;

        var id = await RequestAsync(party);
        var (assembled, body) = await StepAsync("assemble", new { certificateId = id }, Handler);
        assembled.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var claims = body!["certificate"]!["claims"]!.AsArray();
        claims.Single()!.Text("claimNumber").ShouldBe(kept.ClaimNumber);
        claims.Select(c => c.Text("claimNumber")).ShouldNotContain(withdrawn.ClaimNumber);
        body.Text("certificate.status").ShouldBe("ASSEMBLED");
        body.Text("certificate.legalStatus").ShouldBe("Unverified");
        body.Text("certificate.lookbackYears").ShouldBe("5");
        body.ToJsonString().ShouldNotContain("afm", Case.Insensitive);

        // Issue needs the manager; a handler is refused.
        (await StepAsync("issue", new { certificateId = id }, Handler)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (issued, issuedBody) = await StepAsync("issue", new { certificateId = id });
        issued.StatusCode.ShouldBe(HttpStatusCode.OK, issuedBody?.ToJsonString());
        issuedBody.Text("certificate.status").ShouldBe("ISSUED");
        var number = issuedBody.Text("certificate.certificateNumber");
        number.ShouldStartWith("CCH");
        var documentId = issuedBody.Text("certificate.documentId");

        // A second issue (new idempotency key) returns the same certificate and does nothing more.
        var (again, againBody) = await StepAsync("issue", new { certificateId = id });
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("certificate.certificateNumber").ShouldBe(number);
        againBody.Text("certificate.documentId").ShouldBe(documentId);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'ClaimsHistoryCertificateIssued'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM doc.document WHERE document_id = '{documentId}'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.Certificate.issue' AND outcome = 'Succeeded' AND object_id = '{id}'")).ShouldBeGreaterThanOrEqualTo(1);

        // The claims no longer change once issued.
        var (late, lateBody) = await StepAsync("assemble", new { certificateId = id });
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        lateBody.Text("code").ShouldBe("CLM-ERR-CERT-STATE");
    }

    [Fact]
    public async Task Policies_filter_the_claims_and_bad_input_or_order_is_refused()
    {
        var first = _slice.Policy();
        var second = _slice.Policy();
        var claim = await OpenAsync(first, 5, "COLLISION");

        // Another party's policy never matches: the claim is matched by party id.
        var stranger = Guid.CreateVersion7().ToString();
        var strangerId = await RequestAsync(stranger);
        (await StepAsync("assemble", new { certificateId = strangerId })).Body!["certificate"]!["claims"]!.AsArray().ShouldBeEmpty();

        var onlyOther = await RequestAsync(claim.InsuredPartyId, new { policyNumbers = new[] { second.PolicyNumber } });
        (await StepAsync("assemble", new { certificateId = onlyOther })).Body!["certificate"]!["claims"]!.AsArray().ShouldBeEmpty();
        var onlyFirst = await RequestAsync(claim.InsuredPartyId, new { policyNumbers = new[] { first.PolicyNumber } });
        (await StepAsync("assemble", new { certificateId = onlyFirst })).Body!["certificate"]!["claims"]!.AsArray().Count.ShouldBe(1);

        // Issue before assemble, an unknown certificate, and bad periods.
        var fresh = await RequestAsync(claim.InsuredPartyId);
        var (early, earlyBody) = await StepAsync("issue", new { certificateId = fresh });
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        earlyBody.Text("code").ShouldBe("CLM-ERR-CERT-STATE");
        (await StepAsync("assemble", new { certificateId = Guid.NewGuid() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var tooOld = ClaimsSlice.DaysAgo(365 * 7).ToString()[..10];
        var (old, oldBody) = await StepAsync("request", new { requesterPartyId = claim.InsuredPartyId, periodFrom = tooOld });
        old.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        oldBody.Text("code").ShouldBe("CLM-ERR-CERT-REQUESTER");
        var future = ClaimsSlice.DaysAgo(-30).ToString()[..10];
        (await StepAsync("request", new { requesterPartyId = claim.InsuredPartyId, periodTo = future })).Body.Text("code").ShouldBe("CLM-ERR-CERT-REQUESTER");
        (await StepAsync("request", new { requesterPartyId = Guid.Empty })).Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        (await StepAsync("request", new { requesterPartyId = claim.InsuredPartyId }, "Staff.Nobody")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_data_subject_restriction_hides_the_restricted_claims()
    {
        var policy = _slice.Policy();
        var claim = await OpenAsync(policy, 4, "COLLISION");
        var id = await RequestAsync(claim.InsuredPartyId);
        (await StepAsync("assemble", new { certificateId = id })).Body!["certificate"]!["claims"]!.AsArray().Count.ShouldBe(1);

        await _money.ScalarAsync<long>(
            $"""
            INSERT INTO clm.dsar_restriction (restriction_id, subject_party_id, record_class, legal_basis, retention_end, reason, record_count, restricted_by, restricted_at)
            VALUES (gen_random_uuid(), '{claim.InsuredPartyId}', 'CLAIM_FILE', 'test', 'UNSET_F404', 'test', 1, 'test', now())
            """);
        var (response, body) = await StepAsync("assemble", new { certificateId = id });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["certificate"]!["claims"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unverified_look_back_cannot_be_issued_in_Production()
    {
        await using var production = new ClaimsSlice(database.AppConnectionString, services =>
            services.AddSingleton<IHostEnvironment>(sp => new ProductionEnvironment("Production")));
        var money = new ClaimsMoney(production.Factory, production.Client, database.SuperuserConnectionString);
        var policy = production.Policy();
        var claim = await money.OpenClaimAsync(ClaimsSlice.Fnol(policy, ClaimsSlice.DaysAgo(3)));
        var (requested, requestBody) = await money.SendAsync(HttpMethod.Post, "/api/clm/v1/certificates/request", new { requesterPartyId = claim.InsuredPartyId }, Manager, ManagerUser);
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, requestBody?.ToJsonString());
        var id = requestBody.Text("certificate.certificateId");
        (await money.SendAsync(HttpMethod.Post, "/api/clm/v1/certificates/assemble", new { certificateId = id }, Manager, ManagerUser)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (refused, body) = await money.SendAsync(HttpMethod.Post, "/api/clm/v1/certificates/issue", new { certificateId = id }, Manager, ManagerUser);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CLM-ERR-CERT-STATE");
        (await money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{id}' AND event_type = 'ClaimsHistoryCertificateIssued'")).ShouldBe(0);
    }

    private sealed class ProductionEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
