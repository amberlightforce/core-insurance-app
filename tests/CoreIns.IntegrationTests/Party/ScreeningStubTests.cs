using System.Net;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>D-SL2-05: the PTY sanctions-screening stub answers Clear with its marker outside Production (Production: CoreIns.Host.Tests).</summary>
public sealed class ScreeningStubTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string ClaimsHandler = "Staff.ClaimsHandler";

    [Fact]
    public async Task REQ_PTY_006_stub_screens_an_ad_hoc_payee_as_Clear_with_the_stub_marker_and_no_personal_data_in_the_audit()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var callerId = Guid.NewGuid().ToString();
        var body = new
        {
            adHocPayee = new { name = "Συνθετικός Δικαιούχος Τεστ", birthDate = "1971-03-04", country = "GR" },
            callerRef = new { module = "CLM", type = "Payment", id = callerId },
        };

        var (response, result) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/screening/screen", body, ClaimsHandler);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, result?.ToJsonString());
        result.Text("result").ShouldBe("Clear");
        result.Text("paymentBlock").ShouldBe("false");
        result.Text("listVersions.0").ShouldBe(PartyScreening.StubListVersion);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var audit = dataSource.CreateCommand(
            $"SELECT count(*), coalesce(string_agg(changes::text || coalesce(reason, ''), ''), '') FROM plt.audit_event WHERE operation = 'pty.Screening.screen' AND object_id = '{callerId}'");
        await using var reader = await audit.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        reader.GetInt64(0).ShouldBe(1);
        reader.GetString(1).ShouldNotContain("Δικαιούχος");
        reader.GetString(1).ShouldNotContain("1971");
    }

    [Fact]
    public async Task Both_or_neither_payee_forms_is_PTY_ERR_SCREEN_INPUT_and_the_permission_is_required()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var callerRef = new { module = "BIL", type = "Disbursement", id = Guid.NewGuid().ToString() };

        var (neither, neitherBody) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/screening/screen", new { callerRef }, ClaimsHandler);
        neither.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        neitherBody.Text("code").ShouldBe("PTY-ERR-SCREEN-INPUT");

        var (forbidden, _) = await SendAsync(client, HttpMethod.Post, "/api/pty/v1/screening/screen", new { callerRef, partyId = Guid.NewGuid() }, Underwriter);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // In process (BIL disbursement, CLM payment): same stub through the pipeline.
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("bil.disbursement");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        var screening = scope.ServiceProvider.GetRequiredService<IPartyScreeningService>();
        var clear = await screening.ScreenAsync(
            new ScreeningScreenRequest { PartyId = new PartyId(Guid.NewGuid()), CallerRef = new ObjectRef(ModuleCode.BIL, "Disbursement", Guid.NewGuid().ToString()) },
            CommandOptions.New(),
            TestContext.Current.CancellationToken);
        clear.Result.ShouldBe(ScreeningScreenResponse.ResultValue.Clear);
        clear.ListVersions.ShouldBe([PartyScreening.StubListVersion]);
    }
}
