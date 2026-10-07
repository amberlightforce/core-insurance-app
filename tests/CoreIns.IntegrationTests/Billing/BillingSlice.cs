using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>A bound policy as BIL sees it.</summary>
internal sealed record BoundPolicy(string PolicyId, string TermId, string TransactionId, string PartyId, decimal Total, int ChargeCount)
{
    /// <summary>POL's frozen charge lines: charge id → (charge type, amount).</summary>
    public IReadOnlyDictionary<string, (string ChargeType, decimal Amount)> Charges { get; init; } = new Dictionary<string, (string, decimal)>();
}

/// <summary>
/// The BIL slice host: the real Host and database with every module real (PTY, PFC MOTOR-GR, MKT, RAT's illustrative
/// tariff, UW, POL, BIL, CMP with the myDATA stub channel). A test binds a policy through POL's API and then runs the
/// outbox by hand: either <see cref="DrainAsync"/> (the worker's dispatch) or one handler on chosen envelopes in a chosen
/// order (<see cref="InvokeAsync"/>), to prove BIL's intake is idempotent and order-independent (D-ARC-26). Amounts are
/// illustrative test data (D-SLC-04): tests assert consistency, not tariff values.
/// </summary>
internal sealed class BillingSlice : IAsyncDisposable
{
    public BillingSlice(PostgresFixture database)
    {
        Database = database;
        Policy = new PolicySlice(database.AppConnectionString, realRatingAndUnderwriting: true);
        DataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
    }

    public PostgresFixture Database { get; }

    public PolicySlice Policy { get; }

    public HttpClient Client => Policy.Client;

    public NpgsqlDataSource DataSource { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task SeedAsync() => Policy.SeedAsync();

    /// <summary>Quote and bind a motor policy for a new party (ANNUAL plan); the outbox is not dispatched.</summary>
    public async Task<BoundPolicy> BindAsync()
    {
        var party = await Policy.CreatePartyAsync();
        var (jobId, _, _) = await Policy.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));
        var (quoted, quote) = await Policy.QuoteAsync(jobId);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (bound, bind) = await Policy.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        bind.Text("state").ShouldBe("BOUND", bind?.ToJsonString());
        var charges = bind!["chargeDeltas"]!.AsArray();
        var total = charges.Sum(c => decimal.Parse(c!["amount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        return new BoundPolicy(bind.Text("policyId"), bind.Text("termId"), bind.Text("transactionId"), party, total, charges.Count)
        {
            Charges = charges.ToDictionary(
                c => c!["chargeId"]!.GetValue<string>(),
                c => (c!["chargeType"]!.GetValue<string>(), decimal.Parse(c["amount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture))),
        };
    }

    /// <summary>Dispatches every outbox message (events published by handlers included) as the worker would.</summary>
    public async Task DrainAsync() => await Policy.Factory.Services.GetRequiredService<OutboxProcessor>().DrainAsync(Ct);

    /// <summary>The outbox envelopes of an aggregate (in sequence order), optionally of one event type.</summary>
    public async Task<List<EventEnvelope>> EnvelopesAsync(string aggregateId, string? eventType = null)
    {
        var result = new List<EventEnvelope>();
        await using var command = DataSource.CreateCommand(
            $"SELECT {EnvelopeColumnsReader.Columns} FROM plt.outbox_message WHERE aggregate_id = @aggregate"
            + (eventType is null ? string.Empty : " AND event_type = @type") + " ORDER BY aggregate_sequence");
        command.Parameters.AddWithValue("aggregate", aggregateId);
        if (eventType is not null)
        {
            command.Parameters.AddWithValue("type", eventType);
        }

        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add(EnvelopeColumnsReader.Read(reader));
        }

        return result;
    }

    /// <summary>Runs one registered handler on one envelope (replay = redelivery that bypasses the processed marker).</summary>
    public async Task<bool> InvokeAsync(string handlerName, EventEnvelope envelope, bool replay = false)
    {
        var services = Policy.Factory.Services;
        var registration = services.GetRequiredService<EventHandlerRegistry>().Find(handlerName) ?? throw new InvalidOperationException(handlerName);
        return await new HandlerInvoker(services.GetRequiredService<IServiceScopeFactory>()).InvokeAsync(registration, envelope, replay, services.GetRequiredService<IClock>(), Ct);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(Ct);
        return value switch
        {
            null or DBNull => default!,
            T typed => typed,
            _ => (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture),
        };
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> GetAsync(string path, string roles = Billing) =>
        SendAsync(Client, HttpMethod.Get, path, roles: roles);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles = Billing, Guid? key = null) =>
        SendAsync(Client, HttpMethod.Post, path, body, roles: roles, key: key);

    /// <summary>The single invoice of a policy (bil.Invoice.list by policy).</summary>
    public async Task<JsonNode> InvoiceOfAsync(string policyId)
    {
        var (response, page) = await GetAsync($"/api/bil/v1/invoices?policyId={policyId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var items = page!["items"]!.AsArray();
        items.Count.ShouldBe(1, page.ToJsonString());
        var (got, invoice) = await GetAsync($"/api/bil/v1/invoices/{items[0]!["invoice"]!["invoiceId"]!.GetValue<string>()}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, invoice?.ToJsonString());
        return invoice!;
    }

    public static decimal Amount(JsonNode? money) => decimal.Parse(money!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Policy.DisposeAsync();
    }
}
