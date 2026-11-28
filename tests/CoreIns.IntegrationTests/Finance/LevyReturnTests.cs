using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Services;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Market;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>
/// BO-FIN-10: levy returns per levy type (authority) computed from the levy payable postings (credit levy rows net), reconciled to BIL, reviewed by a
/// second person, frozen, filed and amended as new versions; levy rows that are not Settled are refused in Production (fail closed).
/// </summary>
public sealed class LevyReturnTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Ph = "GR-AUXF-PH";
    private const string Ins = "GR-AUXF-INS";
    private const string Path = "/api/fin/v1/levy-returns";
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

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string user, object? body = null, string roles = FinanceRole)
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

    private async Task EnsurePeriodAsync(string period, string status)
    {
        await database.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at)
            VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '{period}', 'OPEN', now()) ON CONFLICT (legal_entity_id, period_code) DO NOTHING;
            UPDATE fin.financial_period SET status = '{status}', soft_closed_by = 'close-user', soft_closed_at = now() WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{period}';
            """, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> PeriodIdAsync(string period)
    {
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '{period}', 'OPEN', now()) ON CONFLICT (legal_entity_id, period_code) DO NOTHING",
            TestContext.Current.CancellationToken);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return await ScalarAsync<Guid>(super, $"SELECT period_id FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{period}'");
    }

    /// <summary>One balanced journal in the period: levy payable GL-2420 (credit raises the liability) of one levy type (the charge type) against GL-1215, from the given source module.</summary>
    private async Task PostLevyAsync(string period, string levyType, string side, string amount, string sourceModule = "BIL")
    {
        var day = period + "-10";
        var periodId = await PeriodIdAsync(period);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var ruleSet = await ScalarAsync<Guid>(super, "SELECT rule_set_id FROM fin.posting_rule_set WHERE version_no = 1");
        var journal = Guid.CreateVersion7();
        var number = "JNL" + period[..4] + Random.Shared.NextInt64(9_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        var other = side == "CREDIT" ? "DEBIT" : "CREDIT";
        string Line(int no, string account, string lineSide, string chargeType, string category) =>
            $"""
            INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional, functional_currency, rule_code, business_date, charge_type, charge_category)
            VALUES (gen_random_uuid(), '{journal}', {no}, '{ApiHostFactory.LegalEntityId}', 'IFRS17', '{account}', '{lineSide}', {amount}, 'EUR', {amount}, 'EUR', 'RAW', DATE '{day}', {chargeType}, {category});
            """;
        await database.ExecuteAsSuperuserAsync(
            $"""
            BEGIN;
            INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version, rule_codes,
                functional_currency, correlation_id, posted_at, posted_by)
            VALUES ('{journal}', '{number}', '{ApiHostFactory.LegalEntityId}', 'GR-TEST', 'GR', 'IFRS17', DATE '{day}', DATE '{day}',
                '{periodId}', 'EVENT', '{sourceModule}', 'BillingEntryPosted', ARRAY[]::uuid[], 'levy-return-test', '{ruleSet}', 1, ARRAY['RAW'], 'EUR', 'x', now(), 'test');
            {Line(1, "GL-2420", side, $"'{levyType}'", "'LEVY'")}
            {Line(2, "GL-1215", other, "NULL", "NULL")}
            COMMIT;
            """, TestContext.Current.CancellationToken);
    }

    private async Task<JsonNode> RecalculateAsync(string period, string levyType = Ph, string user = "anna")
    {
        var (response, body) = await SendAsync(HttpMethod.Post, Path + "/recalculate", user, new { period, levyType });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["return"]!;
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> TransitionAsync(string id, string action, string user, string? comment = null) =>
        await SendAsync(HttpMethod.Post, Path + "/transition", user, new { returnId = id, action, comment });

    [Fact]
    public async Task The_return_splits_per_levy_type_and_credit_levy_rows_net()
    {
        const string period = "2026-01";
        await PostLevyAsync(period, Ph, "CREDIT", "100.00");
        await PostLevyAsync(period, Ph, "CREDIT", "50.00");
        await PostLevyAsync(period, Ph, "DEBIT", "30.00"); // NX-HD-06: the levy credit of a distance-withdrawal void
        await PostLevyAsync(period, Ins, "CREDIT", "40.00");

        // The period is still open: no return.
        var (open, openBody) = await SendAsync(HttpMethod.Post, Path + "/recalculate", "anna", new { period, levyType = Ph });
        open.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, openBody?.ToJsonString());
        openBody!.Text("code").ShouldBe("FIN-ERR-GATES-FAILED");

        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var ph = await RecalculateAsync(period, Ph);
        ph["status"]!.GetValue<string>().ShouldBe("DRAFT");
        ph["levyType"]!.GetValue<string>().ShouldBe(Ph);
        ph["ledgerAmount"]!.GetValue<string>().ShouldBe("120.00");
        ph["bilAmount"]!.GetValue<string>().ShouldBe("120.00");
        ph["netPayable"]!.GetValue<string>().ShouldBe("120.00");
        ph["entryCount"]!.GetValue<int>().ShouldBe(3);
        ph["reconciliation"]!.GetValue<string>().ShouldBe("MATCHED");
        ph["provisional"]!.GetValue<bool>().ShouldBeTrue();
        ph["legalStatus"]!.GetValue<string>().ShouldBe("PendingOpinion");

        var ins = await RecalculateAsync(period, Ins);
        ins["netPayable"]!.GetValue<string>().ShouldBe("40.00");
        ins["returnId"]!.GetValue<string>().ShouldNotBe(ph["returnId"]!.GetValue<string>());

        // A levy type that no pack row backs is Unverified (and refused in Production).
        await PostLevyAsync(period, "OTHER-LEVY", "CREDIT", "7.00");
        (await RecalculateAsync(period, "OTHER-LEVY"))["legalStatus"]!.GetValue<string>().ShouldBe("Unverified");

        // Recalculating a draft updates it in place; the list filters by levy type.
        (await RecalculateAsync(period, Ph))["returnId"]!.GetValue<string>().ShouldBe(ph["returnId"]!.GetValue<string>());
        var (list, listBody) = await SendAsync(HttpMethod.Get, Path + $"?period={period}&levyType={Ins}", "anna");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, listBody?.ToJsonString());
        listBody!["items"]!.AsArray().Single()!["netPayable"]!.GetValue<string>().ShouldBe("40.00");
        (await SendAsync(HttpMethod.Get, Path + $"?period={period}", "anna")).Body!["items"]!.AsArray().Count.ShouldBe(3);

        // The levy type is required and checked.
        (await SendAsync(HttpMethod.Post, Path + "/recalculate", "anna", new { period })).Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableContent);
        (await SendAsync(HttpMethod.Post, Path + "/recalculate", "anna", new { period, levyType = "bad type" })).Response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableContent);
    }

    [Fact]
    public async Task A_difference_to_the_BIL_levy_lines_must_be_explained_before_the_return_is_submitted()
    {
        const string period = "2026-02";
        await PostLevyAsync(period, Ph, "CREDIT", "200.00");
        await PostLevyAsync(period, Ph, "CREDIT", "15.00", sourceModule: "FIN");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var levy = await RecalculateAsync(period);
        var id = levy["returnId"]!.GetValue<string>();
        levy["reconciliation"]!.GetValue<string>().ShouldBe("UNEXPLAINED");
        levy["reconDifference"]!.GetValue<string>().ShouldBe("15.00");

        var (refused, refusedBody) = await TransitionAsync(id, "SUBMIT", "anna");
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, refusedBody?.ToJsonString());
        refusedBody!.Text("code").ShouldBe("FIN-ERR-RECON-UNEXPLAINED");

        var (explained, explainedBody) = await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna",
            new { returnId = id, amount = "0.01", reason = "15.00 posted by a manual correction (ticket FIN-10)", explainsReconciliation = true });
        explained.StatusCode.ShouldBe(HttpStatusCode.OK, explainedBody?.ToJsonString());
        explainedBody!["return"]!["reconciliation"]!.GetValue<string>().ShouldBe("EXPLAINED");
        (await TransitionAsync(id, "SUBMIT", "anna")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Adjustments_are_audited_and_review_by_a_second_person_freezes_the_return_until_it_is_filed_and_amended()
    {
        const string period = "2026-03";
        await PostLevyAsync(period, Ph, "CREDIT", "1000.00");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var id = (await RecalculateAsync(period))["returnId"]!.GetValue<string>();

        (await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "1.234", reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (adjusted, adjustedBody) = await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "-25.50", reason = "Credit levy correction", evidenceRef = "TCK-88" });
        adjusted.StatusCode.ShouldBe(HttpStatusCode.OK, adjustedBody?.ToJsonString());
        var view = adjustedBody!["return"]!;
        view["adjustmentsTotal"]!.GetValue<string>().ShouldBe("-25.50");
        view["netPayable"]!.GetValue<string>().ShouldBe("974.50");
        view["adjustments"]!.AsArray().Single()!["createdBy"]!.GetValue<string>().ShouldBe("anna");
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(super, $"SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.LevyReturn.addAdjustment' AND outcome = 'Succeeded' AND object_id = '{id}'")).ShouldBeGreaterThan(0);

        // A file needs a reviewed return.
        var (early, earlyBody) = await SendAsync(HttpMethod.Post, Path + "/generate-file", "anna", new { returnId = id });
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, earlyBody?.ToJsonString());
        earlyBody!.Text("code").ShouldBe("FIN-ERR-RETURN-NOT-APPROVED");

        // Submit freezes the content hash; the preparer cannot decide.
        var (submitted, submittedBody) = await TransitionAsync(id, "SUBMIT", "anna");
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submittedBody?.ToJsonString());
        var hash = submittedBody!["return"]!["contentHash"]!.GetValue<string>();
        hash.Length.ShouldBe(64);
        (await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "1.00", reason = "late" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var (self, selfBody) = await TransitionAsync(id, "APPROVE", "anna");
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, selfBody?.ToJsonString());
        selfBody!.Text("code").ShouldBe("FIN-ERR-SOD");

        var (approved, approvedBody) = await TransitionAsync(id, "APPROVE", "ben");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, approvedBody?.ToJsonString());
        approvedBody!["return"]!["status"]!.GetValue<string>().ShouldBe("REVIEWED");
        approvedBody["return"]!["reviewedBy"]!.GetValue<string>().ShouldBe("ben");
        approvedBody["return"]!["contentHash"]!.GetValue<string>().ShouldBe(hash);

        // Reviewed: no adjustment, and the database refuses a change of the figures.
        (await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "1.00", reason = "late" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync($"UPDATE fin.levy_return SET net_payable = 1 WHERE return_id = '{id}'", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync($"DELETE FROM fin.levy_return_adjustment WHERE return_id = '{id}'", TestContext.Current.CancellationToken));

        // Filing needs the file first.
        var filing = new { returnId = id, filingReference = "AUXF-2026-03-1", filedOn = "2026-04-20" };
        (await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        var (file, fileBody) = await SendAsync(HttpMethod.Post, Path + "/generate-file", "anna", new { returnId = id });
        file.StatusCode.ShouldBe(HttpStatusCode.OK, fileBody?.ToJsonString());
        var content = fileBody!["file"]!["content"]!.GetValue<string>();
        content.ShouldContain("NET;974.50");
        content.ShouldContain("ADJUSTMENT;-25.50;Credit levy correction");
        content.ShouldContain("CONTENT-HASH;" + hash);
        fileBody["file"]!["fileName"]!.GetValue<string>().ShouldStartWith("LEVY-");
        var fileHash = fileBody["file"]!["fileHash"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, Path + "/generate-file", "anna", new { returnId = id })).Body!["file"]!["fileHash"]!.GetValue<string>().ShouldBe(fileHash);

        var (filed, filedBody) = await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing);
        filed.StatusCode.ShouldBe(HttpStatusCode.OK, filedBody?.ToJsonString());
        filedBody!["return"]!["status"]!.GetValue<string>().ShouldBe("FILED");
        (await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing with { filingReference = "OTHER" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Nothing changed in the ledger: the filed return stays the current one.
        (await RecalculateAsync(period))["returnId"]!.GetValue<string>().ShouldBe(id);

        // BIL refuses an unknown payee and nothing is stored.
        var (pay, payBody) = await SendAsync(HttpMethod.Post, Path + "/request-payment", "anna", new { returnId = id, payeePartyId = Guid.NewGuid(), payeeAccountId = Guid.NewGuid() }, roles: "Staff.Finance,Staff.Billing");
        ((int)pay.StatusCode).ShouldBeInRange(400, 499, payBody?.ToJsonString());
        (await ScalarAsync<long>(super, $"SELECT count(*) FROM fin.levy_return WHERE return_id = '{id}' AND disbursement_id IS NOT NULL")).ShouldBe(0);

        // The ledger moves after filing: the amendment is version 2, linked to the original, which is never edited.
        await PostLevyAsync(period, Ph, "CREDIT", "40.00");
        var amended = await RecalculateAsync(period);
        amended["versionNo"]!.GetValue<int>().ShouldBe(2);
        amended["status"]!.GetValue<string>().ShouldBe("DRAFT");
        amended["supersedesReturnId"]!.GetValue<string>().ShouldBe(id);
        amended["ledgerAmount"]!.GetValue<string>().ShouldBe("1040.00");
        amended["adjustmentsTotal"]!.GetValue<string>().ShouldBe("-25.50");
        var (original, originalBody) = await SendAsync(HttpMethod.Get, Path + "/" + id, "anna");
        original.StatusCode.ShouldBe(HttpStatusCode.OK);
        originalBody!["return"]!["status"]!.GetValue<string>().ShouldBe("FILED");
        originalBody["return"]!["ledgerAmount"]!.GetValue<string>().ShouldBe("1000.00");
        originalBody["return"]!["supersededByReturnId"]!.GetValue<string>().ShouldBe(amended["returnId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reviewer_can_reject_a_submission_and_payment_waits_for_a_filed_return()
    {
        const string period = "2026-04";
        await PostLevyAsync(period, Ph, "CREDIT", "300.00");
        await EnsurePeriodAsync(period, "LOCKED");
        var id = (await RecalculateAsync(period))["returnId"]!.GetValue<string>();
        (await TransitionAsync(id, "SUBMIT", "anna")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(id, "REJECT", "ben")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, rejectedBody) = await TransitionAsync(id, "REJECT", "ben", "Figures need a second look");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody!["return"]!["status"]!.GetValue<string>().ShouldBe("DRAFT");
        rejectedBody["return"]!["contentHash"].ShouldBeNull();
        (await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "2.00", reason = "after review" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (pay, payBody) = await SendAsync(HttpMethod.Post, Path + "/request-payment", "anna", new { returnId = id, payeePartyId = Guid.NewGuid(), payeeAccountId = Guid.NewGuid() });
        pay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, payBody?.ToJsonString());
        payBody!.Text("code").ShouldBe("FIN-ERR-RETURN-NOT-APPROVED");
    }

    [Fact]
    public async Task Production_refuses_a_return_while_the_levy_rows_are_not_settled()
    {
        const string period = "2026-05";
        await PostLevyAsync(period, Ph, "CREDIT", "10.00");
        await PostLevyAsync(period, "OTHER-LEVY", "CREDIT", "10.00");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");

        // PendingOpinion (the GR pack today) is refused in Production; Settled rows would pass.
        var refused = await RecalculateAsProductionAsync(period, Ph, ConfigurationResolveResponse.ValueItem.LegalStatusValue.PendingOpinion);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.Value.ShouldBe("FIN-ERR-SPI-UNBOUND");
        refused.Error.Detail!.ShouldContain("PendingOpinion");

        var settled = await RecalculateAsProductionAsync(period, Ph, ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled);
        settled.IsSuccess.ShouldBeTrue(settled.Error?.ToString());
        settled.Value.Return.Provisional.ShouldBeFalse();
        settled.Value.Return.LegalStatus.ShouldBe("Settled");

        // A levy that no pack row backs is Unverified: refused even when the pack says Settled (fail closed).
        var unknown = await RecalculateAsProductionAsync(period, "OTHER-LEVY", ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled);
        unknown.IsSuccess.ShouldBeFalse();
        unknown.Error!.Detail!.ShouldContain("Unverified");
    }

    private async Task<CoreIns.SharedKernel.Results.Result<CoreIns.Modules.Finance.Contracts.Api.LevyReturnRecalculateResponse>> RecalculateAsProductionAsync(
        string period, string levyType, ConfigurationResolveResponse.ValueItem.LegalStatusValue status)
    {
        var market = new FakeMarketConfigurationService();
        market.Setup<ConfigurationResolveResponse>("mkt.Configuration.resolve", call => new ConfigurationResolveResponse
        {
            ConfigurationHash = ConfigurationHash.Parse(new string('c', 64)),
            MissingKeys = [],
            HasProvisionalValues = status != ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled,
            Values =
            [
                .. (((ConfigurationResolveRequest)call.Arguments[0]!).Keys ?? []).Select(key => new ConfigurationResolveResponse.ValueItem
                {
                    Key = key, Value = System.Text.Json.JsonDocument.Parse("\"0.06\"").RootElement.Clone(), SourceLayer = "L3", ValueVersionId = Guid.NewGuid(), Final = true,
                    LegalStatus = status, LegalSourceRef = "test fixture", Provisional = status != ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled,
                }),
            ],
        });
        var packs = new ServiceCollection().AddSingleton<IMarketConfigurationService>(market).BuildServiceProvider();
        await using var scope = _factory.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var context = provider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("anna");
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var production = new LevyReturnService(
            provider.GetRequiredService<FinanceDbContext>(), context, provider.GetRequiredService<ILegalEntityDirectory>(), provider.GetRequiredService<IClock>(), packs,
            provider.GetRequiredService<IEventPublisher>(), new ProductionEnvironment());
        var session = provider.GetRequiredService<CoreIns.Platform.Persistence.DbSession>();
        await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var result = await production.RecalculateAsync(period, levyType, TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "CoreIns";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
