using System.Net;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.SharedKernel.Identifiers;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>NX-HD-08c: server-side disbursement filters, delinquency list filters, the tax-levy reads and the migrated-records lists.</summary>
public sealed class BillingReadApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private DisbursementSlice _slice = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task The_disbursement_list_filters_on_the_server_and_rejects_malformed_codes()
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var request = DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, 75m);
        await _slice.RequestAsync(request);
        var state = await _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE payee_account_id = '{account.PayeeAccountId}'");

        var (all, mine) = await _slice.GetAsync($"/api/bil/v1/disbursements?payeeAccountId={account.PayeeAccountId}&state={state}&sourceType=CLM_CLAIM_PAYMENT");
        all.StatusCode.ShouldBe(HttpStatusCode.OK, mine?.ToJsonString());
        mine!["items"]!.AsArray().Count.ShouldBe(1);

        var (_, otherState) = await _slice.GetAsync($"/api/bil/v1/disbursements?payeeAccountId={account.PayeeAccountId}&state=NO_SUCH_STATE");
        otherState!["items"]!.AsArray().Count.ShouldBe(0);
        var (_, otherPayee) = await _slice.GetAsync($"/api/bil/v1/disbursements?payeeAccountId={Guid.NewGuid()}");
        otherPayee!["items"]!.AsArray().Count.ShouldBe(0);
        var (_, past) = await _slice.GetAsync("/api/bil/v1/disbursements?requestedFrom=2000-01-01&requestedTo=2000-01-31");
        past!["items"]!.AsArray().Count.ShouldBe(0);

        foreach (var bad in new[] { "state=bad%20state", "sourceType=lower", "requestedFrom=yesterday", "requestedFrom=2030-02-01&requestedTo=2030-01-01" })
        {
            (await _slice.GetAsync("/api/bil/v1/disbursements?" + bad)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad);
        }
    }

    [Fact]
    public async Task The_delinquency_list_accepts_policy_and_trigger_filters_and_rejects_malformed_codes()
    {
        var (ok, body) = await _slice.GetAsync($"/api/bil/v1/delinquencies?policyId={Guid.NewGuid()}&trigger=OVERDUE_INVOICE&state=OPEN");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["items"]!.AsArray().Count.ShouldBe(0);
        (await _slice.GetAsync("/api/bil/v1/delinquencies?state=open;drop")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_tax_levy_reads_serve_finance_and_refuse_other_roles_and_bad_periods()
    {
        foreach (var path in new[] { "periods", "detail" })
        {
            var (ok, body) = await _slice.GetAsync($"/api/bil/v1/tax-levy/{path}", roles: "Staff.Finance");
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            (await _slice.GetAsync($"/api/bil/v1/tax-levy/{path}?period=2026-10", roles: "Staff.Finance")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await _slice.GetAsync($"/api/bil/v1/tax-levy/{path}?period=2026-13", roles: "Staff.Finance")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await _slice.GetAsync($"/api/bil/v1/tax-levy/{path}", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task The_migrated_record_lists_are_served_to_billing_staff_only()
    {
        foreach (var path in new[] { "receivables", "mandates" })
        {
            var (ok, body) = await _slice.GetAsync($"/api/bil/v1/migrated-records/{path}?search=INV%25&limit=500");
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            body!["items"]!.AsArray().Count.ShouldBe(0);
            (await _slice.GetAsync($"/api/bil/v1/migrated-records/{path}", roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }
}
