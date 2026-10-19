using System.Net;
using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy;

/// <summary>
/// SL5-POL-ROLLBACK: <c>mkt.PackRolledBack</c> → exception queue + stale open quotes (REQ-POL-354 subset, D-SL5-10), over HTTP on
/// PostgreSQL 17. The consumer is idempotent, writes nothing on terms/charges, the queue is listable/reviewable.
/// </summary>
public sealed class PackRollbackTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private PolicySlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset InTwoDays => DateTimeOffset.UtcNow.AddDays(2);

    public async ValueTask InitializeAsync()
    {
        _slice = new PolicySlice(database.AppConnectionString);
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task REQ_POL_354_rollback_lists_bound_transactions_marks_open_quotes_stale_and_touches_nothing_else()
    {
        var party = await _slice.CreatePartyAsync();
        var (boundJob, _, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(boundJob)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.BindAsync(boundJob)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (openJob, _, _) = await _slice.DraftAsync(party, InTwoDays);
        var (quoted, _) = await _slice.QuoteAsync(openJob);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var hash = await ScalarAsync<string>(dataSource, $"SELECT configuration_hash FROM pol.policy_transaction WHERE job_id = '{boundJob}'");
        var chargesBefore = await ScalarAsync<long>(dataSource, "SELECT count(*) FROM pol.charge_line");
        var segmentsBefore = await ScalarAsync<long>(dataSource, "SELECT count(*) FROM pol.segment");
        var boundTransaction = await ScalarAsync<Guid>(dataSource, $"SELECT transaction_id FROM pol.policy_transaction WHERE job_id = '{boundJob}'");
        var activation = Guid.CreateVersion7();

        await DeliverAsync(activation, hash);
        await DeliverAsync(activation, hash); // redelivery: no second exception, no failure

        // One exception per affected bound transaction (the fixture DB is shared, so other tests' policies under the same hash also appear).
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.pack_rollback_exception e JOIN pol.policy_transaction t USING (transaction_id) WHERE e.activation_id = '{activation}' AND t.job_id = '{boundJob}'")).ShouldBe(1);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.pack_rollback_exception WHERE activation_id = '{activation}'")).ShouldBe(
            await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.policy_transaction WHERE configuration_hash = '{hash}'"));
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM pol.charge_line")).ShouldBe(chargesBefore);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM pol.segment")).ShouldBe(segmentsBefore);

        // The open quote is stale and its bind is refused.
        var (_, job) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/jobs/{openJob}");
        job.Text("job.versions.0.staleReason").ShouldBe("PACK_ROLLBACK");
        (await _slice.BindAsync(openJob)).Body.Text("code").ShouldBe("POL-ERR-QUOTE-STALE");

        // The queue lists the bound transaction with its pinned hash and product version.
        var (listed, page) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/pack-rollback-exceptions?status=OPEN&activationId={activation}", roles: Manager);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var item = page!["items"]!.AsArray().Single(i => i.Text("transactionId") == boundTransaction.ToString());
        item.Text("configurationHash").ShouldBe(hash);
        item.Text("transactionKind").ShouldBe("ISSUANCE");
        item.Text("wrkActivity").ShouldBe("NOT_CREATED_WRK_NOT_BUILT");
        item.Text("status").ShouldBe("OPEN");
        item.Text("fromVersion").ShouldBe("0.2.0");
        var id = item.Text("exceptionId");
        item.Text("transactionId").ShouldBe(boundTransaction.ToString());
        (await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/pack-rollback-exceptions/{id}", roles: Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Review: only the manager; once.
        var body = new { exceptionId = id, outcome = "CORRECTION_REQUIRED", reason = "Premium must be re-checked under the restored pack." };
        (await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/pack-rollback-exceptions/review", body)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (reviewed, view) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/pack-rollback-exceptions/review", body, roles: Manager);
        reviewed.StatusCode.ShouldBe(HttpStatusCode.OK, view?.ToJsonString());
        view.Text("status").ShouldBe("REVIEWED");
        view.Text("review.outcome").ShouldBe("CORRECTION_REQUIRED");
        (await SendAsync(_slice.Client, HttpMethod.Post, "/api/pol/v1/pack-rollback-exceptions/review", body, roles: Manager)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        var (reviewedList, afterReview) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/pack-rollback-exceptions?status=REVIEWED&activationId={activation}", roles: Manager);
        reviewedList.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterReview!["items"]!.AsArray().Single().Text("exceptionId").ShouldBe(id);
    }

    [Fact]
    public async Task REQ_POL_354_a_rollback_with_unrelated_hashes_changes_nothing()
    {
        var party = await _slice.CreatePartyAsync();
        var (job, _, _) = await _slice.DraftAsync(party, InTwoDays);
        (await _slice.QuoteAsync(job)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _slice.BindAsync(job)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var activation = Guid.CreateVersion7();

        await DeliverAsync(activation, new string('e', 64));

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pol.pack_rollback_exception WHERE activation_id = '{activation}'")).ShouldBe(0);
        (await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/pack-rollback-exceptions/{Guid.NewGuid()}", roles: Manager)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task DeliverAsync(Guid activation, string hash)
    {
        await using var scope = _slice.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var session = services.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(Ct);
        var start = Instant.FromUtcDateTime(DateTime.UtcNow.AddDays(-3));
        var window = new InstantRange(start, Instant.FromUtcDateTime(DateTime.UtcNow));
        var payload = new PackRolledBackV1
        {
            PackId = Guid.CreateVersion7(), FromVersion = "0.2.0", ToVersion = "0.1.0", AffectedWindow = window, Window = window,
            HashesInWindow = [Sha256Hash.Parse(hash)], HashesIssued = [Sha256Hash.Parse(hash)], Reason = "rollback after defect found in treatment rules",
            Pack = "GR", LegalEntity = "GR-TEST", ActivationId = activation, ResultingHash = Sha256Hash.Parse(new string('b', 64)),
        };
        var envelope = services.GetRequiredService<IEventPublisher>().Publish(new OutgoingEvent(
            EventDescriptor.From(PackRolledBackV1.Descriptor), "Stamp", activation.ToString(), payload,
            BusinessKeys.Empty.With("stampId", payload.ResultingHash.Value).With("packId", payload.PackId.ToString())));
        var handler = services.GetRequiredService<EventHandlerRegistry>().Find("POL.PackRolledBack.RaiseExceptions")!;
        await handler.Invoke(services, envelope, Ct);
        await transaction.CommitAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
