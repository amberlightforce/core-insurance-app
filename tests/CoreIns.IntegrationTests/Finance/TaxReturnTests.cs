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

/// <summary>BO-FIN-09: IPT returns computed from the IPT payable postings, reconciled to BIL, reviewed by a second person, frozen, filed and amended as new versions.</summary>
public sealed class TaxReturnTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Path = "/api/fin/v1/tax-returns";
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

    private async Task<Guid> EnsurePeriodAsync(string period, string status)
    {
        await database.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at)
            VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '{period}', 'OPEN', now()) ON CONFLICT (legal_entity_id, period_code) DO NOTHING;
            UPDATE fin.financial_period SET status = '{status}', soft_closed_by = 'close-user', soft_closed_at = now() WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{period}';
            """, TestContext.Current.CancellationToken);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return await ScalarAsync<Guid>(super, $"SELECT period_id FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{period}'");
    }

    /// <summary>One balanced journal in the period: IPT payable GL-2410 (credit raises the liability) against GL-1215, from the given source module.</summary>
    private async Task PostIptAsync(string period, string side, string amount, string sourceModule = "BIL")
    {
        var day = period + "-10";
        var periodId = await EnsurePeriodStateAsync(period);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var ruleSet = await ScalarAsync<Guid>(super, "SELECT rule_set_id FROM fin.posting_rule_set WHERE version_no = 1");
        var journal = Guid.CreateVersion7();
        var number = "JNL" + period[..4] + Random.Shared.NextInt64(9_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        var other = side == "CREDIT" ? "DEBIT" : "CREDIT";
        string Line(int no, string account, string lineSide, string glKey) =>
            $"""
            INSERT INTO fin.journal_line (line_id, journal_id, line_no, legal_entity_id, book, account_code, side, amount, currency, amount_functional, functional_currency, rule_code, business_date, gl_key)
            VALUES (gen_random_uuid(), '{journal}', {no}, '{ApiHostFactory.LegalEntityId}', 'IFRS17', '{account}', '{lineSide}', {amount}, 'EUR', {amount}, 'EUR', 'RAW', DATE '{day}', {glKey});
            """;
        await database.ExecuteAsSuperuserAsync(
            $"""
            BEGIN;
            INSERT INTO fin.journal_entry (journal_id, journal_number, legal_entity_id, legal_entity_code, jurisdiction, book, accounting_date, business_date,
                period_id, source_type, source_module, source_event_type, source_event_ids, source_ref, rule_set_id, rule_set_version, rule_codes,
                functional_currency, correlation_id, posted_at, posted_by)
            VALUES ('{journal}', '{number}', '{ApiHostFactory.LegalEntityId}', 'GR-TEST', 'GR', 'IFRS17', DATE '{day}', DATE '{day}',
                '{periodId}', 'EVENT', '{sourceModule}', 'BillingEntryPosted', ARRAY[]::uuid[], 'tax-return-test', '{ruleSet}', 1, ARRAY['RAW'], 'EUR', 'x', now(), 'test');
            {Line(1, "GL-2410", side, "'TAX-IPT'")}
            {Line(2, "GL-1215", other, "NULL")}
            COMMIT;
            """, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> EnsurePeriodStateAsync(string period)
    {
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '{period}', 'OPEN', now()) ON CONFLICT (legal_entity_id, period_code) DO NOTHING",
            TestContext.Current.CancellationToken);
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return await ScalarAsync<Guid>(super, $"SELECT period_id FROM fin.financial_period WHERE legal_entity_id = '{ApiHostFactory.LegalEntityId}' AND period_code = '{period}'");
    }

    private async Task<JsonNode> RecalculateAsync(string period, string user = "anna")
    {
        var (response, body) = await SendAsync(HttpMethod.Post, Path + "/recalculate", user, new { period });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body!["return"]!;
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> TransitionAsync(string id, string action, string user, string? comment = null) =>
        await SendAsync(HttpMethod.Post, Path + "/transition", user, new { returnId = id, action, comment });

    [Fact]
    public async Task The_return_ties_to_the_IPT_postings_of_the_period_and_waits_for_a_soft_closed_period()
    {
        const string period = "2025-03";
        await PostIptAsync(period, "CREDIT", "100.00");
        await PostIptAsync(period, "CREDIT", "50.00");
        await PostIptAsync(period, "DEBIT", "20.00");

        // The period is still open: no return.
        var (open, openBody) = await SendAsync(HttpMethod.Post, Path + "/recalculate", "anna", new { period });
        open.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, openBody?.ToJsonString());
        openBody!.Text("code").ShouldBe("FIN-ERR-GATES-FAILED");

        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var tax = await RecalculateAsync(period);
        tax["status"]!.GetValue<string>().ShouldBe("DRAFT");
        tax["taxCode"]!.GetValue<string>().ShouldBe("TAX-IPT");
        tax["versionNo"]!.GetValue<int>().ShouldBe(1);
        tax["ledgerAmount"]!.GetValue<string>().ShouldBe("130.00");
        tax["bilAmount"]!.GetValue<string>().ShouldBe("130.00");
        tax["netPayable"]!.GetValue<string>().ShouldBe("130.00");
        tax["entryCount"]!.GetValue<int>().ShouldBe(3);
        tax["reconciliation"]!.GetValue<string>().ShouldBe("MATCHED");
        tax["provisional"]!.GetValue<bool>().ShouldBeTrue();
        tax["legalStatus"]!.GetValue<string>().ShouldBe("PendingOpinion");

        // Recalculating a draft updates it in place; the list and get show it.
        (await RecalculateAsync(period))["returnId"]!.GetValue<string>().ShouldBe(tax["returnId"]!.GetValue<string>());
        var (list, listBody) = await SendAsync(HttpMethod.Get, Path + $"?period={period}", "anna");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, listBody?.ToJsonString());
        listBody!["items"]!.AsArray().Single()!["netPayable"]!.GetValue<string>().ShouldBe("130.00");
    }

    [Fact]
    public async Task A_difference_to_the_BIL_tax_lines_must_be_explained_before_the_return_is_submitted()
    {
        const string period = "2025-04";
        await PostIptAsync(period, "CREDIT", "200.00");
        await PostIptAsync(period, "CREDIT", "15.00", sourceModule: "FIN");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var tax = await RecalculateAsync(period);
        var id = tax["returnId"]!.GetValue<string>();
        tax["reconciliation"]!.GetValue<string>().ShouldBe("UNEXPLAINED");
        tax["reconDifference"]!.GetValue<string>().ShouldBe("15.00");

        var (refused, refusedBody) = await TransitionAsync(id, "SUBMIT", "anna");
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, refusedBody?.ToJsonString());
        refusedBody!.Text("code").ShouldBe("FIN-ERR-RECON-UNEXPLAINED");

        var (explained, explainedBody) = await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna",
            new { returnId = id, amount = "0.01", reason = "15.00 posted by a manual correction (ticket FIN-9)", explainsReconciliation = true });
        explained.StatusCode.ShouldBe(HttpStatusCode.OK, explainedBody?.ToJsonString());
        explainedBody!["return"]!["reconciliation"]!.GetValue<string>().ShouldBe("EXPLAINED");
        (await TransitionAsync(id, "SUBMIT", "anna")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Adjustments_are_audited_and_review_by_a_second_person_freezes_the_return_until_it_is_filed_and_amended()
    {
        const string period = "2025-05";
        await PostIptAsync(period, "CREDIT", "1000.00");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var tax = await RecalculateAsync(period);
        var id = tax["returnId"]!.GetValue<string>();

        // Adjustments: amount rules, then an audited one.
        (await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "1.234", reason = "x" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (adjusted, adjustedBody) = await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "-25.50", reason = "Credit note correction", evidenceRef = "TCK-77" });
        adjusted.StatusCode.ShouldBe(HttpStatusCode.OK, adjustedBody?.ToJsonString());
        var view = adjustedBody!["return"]!;
        view["adjustmentsTotal"]!.GetValue<string>().ShouldBe("-25.50");
        view["netPayable"]!.GetValue<string>().ShouldBe("974.50");
        var adjustment = view["adjustments"]!.AsArray().Single()!;
        adjustment["createdBy"]!.GetValue<string>().ShouldBe("anna");
        adjustment["reason"]!.GetValue<string>().ShouldBe("Credit note correction");
        await using var super = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(super, $"SELECT count(*) FROM plt.audit_event WHERE operation = 'fin.TaxReturn.addAdjustment' AND outcome = 'Succeeded' AND object_id = '{id}'")).ShouldBeGreaterThan(0);

        // A file needs a reviewed return.
        var (early, earlyBody) = await SendAsync(HttpMethod.Post, Path + "/generate-file", "anna", new { returnId = id });
        early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, earlyBody?.ToJsonString());
        earlyBody!.Text("code").ShouldBe("FIN-ERR-RETURN-NOT-APPROVED");

        // Submit freezes the content hash; the preparer and the adjuster cannot decide.
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
        await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync($"UPDATE fin.tax_return SET net_payable = 1 WHERE return_id = '{id}'", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync($"DELETE FROM fin.tax_return_adjustment WHERE return_id = '{id}'", TestContext.Current.CancellationToken));

        // Filing needs the file first.
        var filing = new { returnId = id, filingReference = "MYDATA-2025-05-1", filedOn = "2025-06-20" };
        (await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        var (file, fileBody) = await SendAsync(HttpMethod.Post, Path + "/generate-file", "anna", new { returnId = id });
        file.StatusCode.ShouldBe(HttpStatusCode.OK, fileBody?.ToJsonString());
        var content = fileBody!["file"]!["content"]!.GetValue<string>();
        content.ShouldContain("NET;974.50");
        content.ShouldContain("ADJUSTMENT;-25.50;Credit note correction");
        content.ShouldContain("CONTENT-HASH;" + hash);
        var fileHash = fileBody["file"]!["fileHash"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, Path + "/generate-file", "anna", new { returnId = id })).Body!["file"]!["fileHash"]!.GetValue<string>().ShouldBe(fileHash);

        var (filed, filedBody) = await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing);
        filed.StatusCode.ShouldBe(HttpStatusCode.OK, filedBody?.ToJsonString());
        filedBody!["return"]!["status"]!.GetValue<string>().ShouldBe("FILED");
        filedBody["return"]!["filingReference"]!.GetValue<string>().ShouldBe("MYDATA-2025-05-1");
        (await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, Path + "/record-filing", "anna", filing with { filingReference = "OTHER" })).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Nothing changed in the ledger: the filed return stays the current one.
        (await RecalculateAsync(period))["returnId"]!.GetValue<string>().ShouldBe(id);

        // Payment: not before filing is possible (it is filed), but BIL refuses an unknown payee and nothing is stored.
        var (pay, payBody) = await SendAsync(HttpMethod.Post, Path + "/request-payment", "anna", new { returnId = id, payeePartyId = Guid.NewGuid(), payeeAccountId = Guid.NewGuid() }, roles: "Staff.Finance,Staff.Billing");
        ((int)pay.StatusCode).ShouldBeInRange(400, 499, payBody?.ToJsonString());
        (await ScalarAsync<long>(super, $"SELECT count(*) FROM fin.tax_return WHERE return_id = '{id}' AND disbursement_id IS NOT NULL")).ShouldBe(0);

        // The ledger moves after filing: the amendment is version 2, linked to the original, which is never edited.
        await PostIptAsync(period, "CREDIT", "40.00");
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
    public async Task A_reviewer_can_reject_a_submission_and_the_return_becomes_editable_again()
    {
        const string period = "2025-06";
        await PostIptAsync(period, "CREDIT", "300.00");
        await EnsurePeriodAsync(period, "LOCKED");
        var id = (await RecalculateAsync(period))["returnId"]!.GetValue<string>();
        (await TransitionAsync(id, "SUBMIT", "anna")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(id, "REJECT", "ben")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var (rejected, rejectedBody) = await TransitionAsync(id, "REJECT", "ben", "Figures need a second look");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody!["return"]!["status"]!.GetValue<string>().ShouldBe("DRAFT");
        rejectedBody["return"]!["contentHash"].ShouldBeNull();
        (await SendAsync(HttpMethod.Post, Path + "/add-adjustment", "anna", new { returnId = id, amount = "2.00", reason = "after review" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Payment_waits_for_a_filed_return()
    {
        const string period = "2025-07";
        await PostIptAsync(period, "CREDIT", "80.00");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");
        var id = (await RecalculateAsync(period))["returnId"]!.GetValue<string>();
        var (pay, payBody) = await SendAsync(HttpMethod.Post, Path + "/request-payment", "anna", new { returnId = id, payeePartyId = Guid.NewGuid(), payeeAccountId = Guid.NewGuid() });
        pay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, payBody?.ToJsonString());
        payBody!.Text("code").ShouldBe("FIN-ERR-RETURN-NOT-APPROVED");
    }

    [Fact]
    public async Task Production_refuses_a_return_while_the_IPT_rules_are_not_settled()
    {
        const string period = "2025-08";
        await PostIptAsync(period, "CREDIT", "10.00");
        await EnsurePeriodAsync(period, "SOFT_CLOSED");

        // PendingOpinion (the GR pack today) is refused in Production; a Settled value would pass.
        var refused = await RecalculateAsProductionAsync(period, ConfigurationResolveResponse.ValueItem.LegalStatusValue.PendingOpinion);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.Value.ShouldBe("FIN-ERR-SPI-UNBOUND");
        refused.Error.Detail!.ShouldContain("PendingOpinion");

        var settled = await RecalculateAsProductionAsync(period, ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled);
        settled.IsSuccess.ShouldBeTrue(settled.Error?.ToString());
        settled.Value.Return.Provisional.ShouldBeFalse();
        settled.Value.Return.LegalStatus.ShouldBe("Settled");
    }

    private async Task<CoreIns.SharedKernel.Results.Result<CoreIns.Modules.Finance.Contracts.Api.TaxReturnRecalculateResponse>> RecalculateAsProductionAsync(
        string period, ConfigurationResolveResponse.ValueItem.LegalStatusValue status)
    {
        var market = new FakeMarketConfigurationService();
        market.Setup<ConfigurationResolveResponse>("mkt.Configuration.resolve", _ => new ConfigurationResolveResponse
        {
            ConfigurationHash = ConfigurationHash.Parse(new string('c', 64)),
            MissingKeys = [],
            HasProvisionalValues = status != ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled,
            Values =
            [
                new ConfigurationResolveResponse.ValueItem
                {
                    Key = "tax.ipt.liability_point", Value = System.Text.Json.JsonDocument.Parse("\"DUE\"").RootElement.Clone(), SourceLayer = "L3", ValueVersionId = Guid.NewGuid(), Final = true,
                    LegalStatus = status, LegalSourceRef = "test fixture", Provisional = status != ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled,
                },
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
        var production = new TaxReturnService(
            provider.GetRequiredService<FinanceDbContext>(), context, provider.GetRequiredService<ILegalEntityDirectory>(), provider.GetRequiredService<IClock>(), packs,
            provider.GetRequiredService<IEventPublisher>(), new ProductionEnvironment());
        var session = provider.GetRequiredService<CoreIns.Platform.Persistence.DbSession>();
        await using var transaction = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var result = await production.RecalculateAsync(period, null, TestContext.Current.CancellationToken);
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
