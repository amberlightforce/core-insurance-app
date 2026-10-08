using System.Globalization;
using System.Net;
using CoreIns.IntegrationTests.Policy;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// The Development-only all-roles user (<c>superuser</c> in appsettings.Development.json): one person holding every staff
/// role can drive every module, the best authority grant among the roles applies, and four-eyes still holds per user.
/// The roles are read from the real configuration file so the test follows the dev user list.
/// </summary>
public sealed class SuperUserTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string SuperUser = "dev:superuser";

    private PolicySlice _policy = null!;
    private ClaimsMoney _money = null!;
    private string _roles = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var file = Path.Combine(RepositoryPaths.Root, "src", "CoreIns.Host", "appsettings.Development.json");
        var users = System.Text.Json.Nodes.JsonNode.Parse(
            await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!["DevAuthentication"]!["Users"]!.AsArray();
        var roles = users.Single(u => u!["Id"]!.GetValue<string>() == "superuser")!["Roles"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        roles.ShouldBe(
            ["Staff.Underwriter", "Staff.UnderwritingManager", "Staff.Billing", "Staff.Finance", "Staff.ClaimsHandler", "Staff.ClaimsManager", "Platform.Admin"], ignoreOrder: true);
        _roles = string.Join(',', roles);

        _policy = new PolicySlice(
            database.AppConnectionString, realRatingAndUnderwriting: true, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        await _policy.SeedAsync();
        _money = new ClaimsMoney(_policy.Factory, _policy.Client, database.SuperuserConnectionString);
    }

    public async ValueTask DisposeAsync() => await _policy.DisposeAsync();

    [Fact]
    public async Task The_super_user_creates_a_party_quotes_reads_invoices_takes_a_payment_reads_journals_submits_FNOL_and_opens_the_inbox()
    {
        // PTY: create a party.
        var (created, party) = await _money.SendAsync(HttpMethod.Post, "/api/pty/v1/parties", Party.PartyApi.Person("Μαρία", "Παπαδοπούλου", null), _roles, SuperUser);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, party?.ToJsonString());
        var partyId = party.Text("party.partyId");

        // POL: draft, quote and bind as the super user's roles.
        var (jobId, _, _) = await _policy.DraftAsync(partyId, DateTimeOffset.UtcNow.AddDays(2));
        var (quoted, quote) = await _money.SendAsync(HttpMethod.Post, "/api/pol/v1/jobs/quote", new { jobId, versionNo = 1 }, _roles, SuperUser);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await _policy.BindAsync(jobId, roles: _roles);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var clock = (ShiftableClock)_policy.Factory.Services.GetRequiredService<IClock>();
        clock.Advance(TimeSpan.FromDays(10));
        await _money.DrainAsync();

        // BIL: read the invoice, take the payment.
        var (listed, invoices) = await _money.SendAsync(HttpMethod.Get, $"/api/bil/v1/invoices?policyId={bind.Text("policyId")}", roles: _roles, user: SuperUser);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, invoices?.ToJsonString());
        var invoiceId = invoices!["items"]![0]!["invoice"]!["invoiceId"]!.GetValue<string>();
        var (got, invoice) = await _money.SendAsync(HttpMethod.Get, $"/api/bil/v1/invoices/{invoiceId}", roles: _roles, user: SuperUser);
        got.StatusCode.ShouldBe(HttpStatusCode.OK, invoice?.ToJsonString());
        var (paid, payment) = await _money.SendAsync(HttpMethod.Post, "/api/bil/v1/payments/take", new
        {
            billingAccountId = invoice.Text("invoice.billingAccountId"),
            amount = new { amount = Amount(invoice!["invoice"]!["total"]).ToString(CultureInfo.InvariantCulture), currency = "EUR" },
            method = "BANK_TRANSFER",
            bankReference = "TRF-SUPER-1",
        }, _roles, SuperUser);
        paid.StatusCode.ShouldBe(HttpStatusCode.Created, payment?.ToJsonString());
        await _money.DrainAsync();

        // FIN: read the journals.
        var (journals, page) = await _money.SendAsync(HttpMethod.Get, "/api/fin/v1/journals/query?limit=10", roles: _roles, user: SuperUser);
        journals.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().Count.ShouldBeGreaterThan(0);

        // CLM: FNOL on the bound policy, then the approvals inbox.
        var scripted = new ScriptedPolicy(Guid.Parse(bind.Text("policyId")), bind.Text("policyNumber"), Guid.Parse(partyId), "MOTOR-GR", clock.Now, clock.Now, []);
        var (reported, fnol) = await _money.SendAsync(
            HttpMethod.Post, "/api/clm/v1/fnol/submit", ClaimsSlice.Fnol(scripted, lossAt: clock.Now.Plus(TimeSpan.FromDays(-1)), exposure: false), _roles, SuperUser);
        reported.StatusCode.ShouldBe(HttpStatusCode.OK, fnol?.ToJsonString());
        var (inbox, items) = await _money.SendAsync(HttpMethod.Get, "/api/plt/v1/approval", roles: _roles, user: SuperUser);
        inbox.StatusCode.ShouldBe(HttpStatusCode.OK, items?.ToJsonString());

        // The best grant among the roles applies (the manager's 50,000 ceiling), and there is nobody above to refer to:
        // a reserve over it is refused, never self-approved.
        var (exposed, exposure) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures",
            new { claimId = fnol.Text("claimId"), expectedRecordVersion = 1, kind = "OWN_DAMAGE", coverageCode = "OWN-DAMAGE" }, _roles, SuperUser);
        exposed.StatusCode.ShouldBe(HttpStatusCode.Created, exposure?.ToJsonString());
        var claim = new MoneyClaim(fnol.Text("claimId"), fnol.Text("claimNumber"), exposure.Text("exposure.exposureId"), partyId);
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5300m)], _roles, SuperUser)).Text("status").ShouldBe("APPROVED");
        var (built, build) = await _money.BuildAsync(claim, [Reserve(claim, 50000.01m)], _roles, SuperUser);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, build?.ToJsonString());
        var (denied, deny) = await _money.SubmitAsync(build.Text("setId"), _roles, SuperUser);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden, deny?.ToJsonString());
        deny.Text("code").ShouldBe("CLM-ERR-AUTHORITY");
    }
}
