using System.Globalization;
using System.Net;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>NX-EC-08: one bank statement line is one receipt, independent of the 7-day idempotency record.</summary>
public sealed class BankReferenceTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private BillingSlice _slice = null!;

    public async ValueTask InitializeAsync()
    {
        _slice = new BillingSlice(database);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    [Fact]
    public async Task A_second_receipt_for_the_same_bank_line_is_a_clean_409_and_never_a_second_receipt()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var account = (await _slice.InvoiceOfAsync(policy.PolicyId)).Text("invoice.billingAccountId");
        var reference = "BANK-" + Guid.NewGuid().ToString("N")[..10];
        var body = new { billingAccountId = account, amount = Money(policy.Total), method = "BANK_TRANSFER", bankReference = reference, autoAllocate = false };

        // Two fresh idempotency keys, as after the 7-day expiry.
        var (first, one) = await _slice.PostAsync("/api/bil/v1/payments/take", body);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, one?.ToJsonString());
        var (second, two) = await _slice.PostAsync("/api/bil/v1/payments/take", body);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, two?.ToJsonString());
        two.Text("code").ShouldBe("BIL-ERR-DUPLICATE");

        // Racing requests with the same line: exactly one receipt.
        var raced = "BANK-" + Guid.NewGuid().ToString("N")[..10];
        var racing = new { billingAccountId = account, amount = Money(5m), method = "BANK_TRANSFER", bankReference = raced, autoAllocate = false };
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => _slice.PostAsync("/api/bil/v1/payments/take", racing))));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.Created).ShouldBe(1, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.Conflict).ShouldBe(2);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.receipt WHERE bank_reference IN ('{reference}', '{raced}')")).ShouldBe(2);

        // Cashier receipts carry no bank statement line: the same text there is not a duplicate.
        var (cashier, _) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = account, amount = Money(1m), method = "CASHIER", bankReference = reference, autoAllocate = false });
        cashier.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
