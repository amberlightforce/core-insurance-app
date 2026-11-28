using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Work;

/// <summary>
/// BO-WRK-04: the system-generated CMP and DOC patterns (queues CMP-OPS, DOC-OPS, FIN-OPS). Escalated complaints and rejected fiscal documents are
/// raised by event handlers; the missing-rule, delivery-conflict and DSAR-task-failed patterns are raised by their owners through the create
/// contract. Every raiser leaves one open activity per source key.
/// </summary>
public sealed class BackOfficePatternTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);
    private HttpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _client ??= _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task An_escalated_complaint_raises_one_open_activity_in_the_compliance_queue()
    {
        var complaint = ComplaintId.New();
        var id = complaint.Value.ToString("D");
        var payload = new ComplaintEscalatedV1 { ComplaintId = complaint, AdrBody = "ADR" };
        await DeliverAsync("WRK.ComplaintEscalated.RaiseFollowUp", ComplaintEscalatedV1.Descriptor, "Complaint", id, payload);
        await DeliverAsync("WRK.ComplaintEscalated.RaiseFollowUp", ComplaintEscalatedV1.Descriptor, "Complaint", id, payload);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM wrk.activity WHERE pattern_code = 'WRK-CMP-COMPLAINT-ESCALATED' AND primary_id = '{id}' AND state = 'OPEN'")).ShouldBe(1);
        (await ScalarAsync<string>(dataSource, $"SELECT queue_code FROM wrk.activity WHERE pattern_code = 'WRK-CMP-COMPLAINT-ESCALATED' AND primary_id = '{id}'")).ShouldBe("CMP-OPS");
    }

    [Fact]
    public async Task A_rejected_fiscal_document_raises_one_open_activity_in_the_finance_queue()
    {
        var fiscal = Guid.NewGuid().ToString("D");
        var payload = new FiscalDocRejectedV1 { SourceType = "Invoice", SourceId = Guid.NewGuid().ToString("D"), CauseGroup = "VALIDATION", Codes = ["E101"] };
        await DeliverAsync("WRK.FiscalDocRejected.RaiseReview", FiscalDocRejectedV1.Descriptor, "FiscalDocument", fiscal, payload);
        await DeliverAsync("WRK.FiscalDocRejected.RaiseReview", FiscalDocRejectedV1.Descriptor, "FiscalDocument", fiscal, payload);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM wrk.activity WHERE pattern_code = 'WRK-CMP-FISCAL-REJECTED' AND primary_id = '{fiscal}' AND state = 'OPEN'")).ShouldBe(1);
        (await ScalarAsync<string>(dataSource, $"SELECT queue_code FROM wrk.activity WHERE pattern_code = 'WRK-CMP-FISCAL-REJECTED' AND primary_id = '{fiscal}'")).ShouldBe("FIN-OPS");
    }

    [Theory]
    [InlineData("WRK-CMP-MISSING-RULE", "CMP", "CMP-OPS")]
    [InlineData("WRK-CMP-DSAR-TASK-FAILED", "CMP", "CMP-OPS")]
    [InlineData("WRK-DOC-DELIVERY-CONFLICT", "DOC", "DOC-OPS")]
    public async Task A_contract_raised_pattern_keeps_one_open_activity_per_source_key(string pattern, string module, string queue)
    {
        var source = Guid.NewGuid().ToString();
        var first = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", Body(pattern, module, source));
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body?.ToJsonString());
        first.Body.Text("assignment.assigneeId").ShouldBe(queue);

        var duplicate = await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", Body(pattern, module, source));
        duplicate.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        duplicate.Body.Text("code").ShouldBe("WRK-ERR-DUPLICATE");

        // Another source is its own activity; a module that does not own the pattern may not raise it.
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", Body(pattern, module, Guid.NewGuid().ToString()))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = module == "CMP" ? "DOC" : "CMP";
        (await SendAsync(HttpMethod.Post, "/api/wrk/v1/activities", Body(pattern, other, source))).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static object Body(string pattern, string module, string id) => new
    {
        patternCode = pattern,
        raisingModule = module,
        causeRef = "test",
        primaryObject = new { module, type = "Case", id },
    };

    private async Task DeliverAsync<T>(string handlerName, EventContract descriptor, string aggregateType, string aggregateId, T payload)
        where T : class
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var session = services.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(Ct);
        var envelope = services.GetRequiredService<IEventPublisher>().Publish(new OutgoingEvent(
            EventDescriptor.From(descriptor), aggregateType, aggregateId, payload, BusinessKeys.Empty.With(descriptor.RequiredBusinessKeys[0], aggregateId)));
        var handler = services.GetRequiredService<EventHandlerRegistry>().Find(handlerName)!;
        await handler.Invoke(services, envelope, Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Admin);
        request.Headers.Add(TestAuthHandler.UserHeader, "test-user");
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
