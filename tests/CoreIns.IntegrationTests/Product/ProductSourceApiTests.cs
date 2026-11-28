using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.Modules.Product;
using CoreIns.Modules.Product.Domain;
using Npgsql;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product;

/// <summary>BO-PFC-03: version diff, YAML or JSON export and import into a draft change set, over HTTP on a real PostgreSQL.</summary>
public sealed class ProductSourceApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Author = "Platform.ReleaseManager";

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

    private static string HashOf(string json) => ArtefactCompiler.Compile(ArtefactCompiler.Parse(json)).Value.Hash.Value;

    private async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return (long)(await dataSource.CreateCommand(sql).ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<string> ImportBothAsync(string code)
    {
        var (first, firstBody) = await ImportAsync(_client, Seed(code));
        first.StatusCode.ShouldBe(HttpStatusCode.Created, firstBody?.ToJsonString());
        var v11 = JsonNode.Parse(ProductSeeds.MotorPrivateCar11Json())!.AsObject();
        v11["product"]!["code"] = code;
        (await ImportAsync(_client, v11)).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return firstBody.Text("artefactHash");
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> ImportSourceAsync(string source, string? format = null, bool dryRun = false) =>
        PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/product-imports/import" + (dryRun ? "?dryRun=true" : string.Empty), new { source, format }, Author);

    [Fact]
    public async Task diff_of_1_0_and_1_1_lists_term_ratio_refund_methods_and_change_permissions()
    {
        await ImportBothAsync("MOTOR-DF1");
        var (response, body) = await GetAsync(_client, "/api/pfc/v1/product-versions/diff?product=MOTOR-DF1&from=1.0&to=1.1", Author);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());

        var entries = body!["groups"]!.AsArray().SelectMany(g => g!["entries"]!.AsArray()).ToList();
        body.Text("total").ShouldBe(entries.Count.ToString());
        entries.Single(e => e.Text("path") == "/dayCount").Text("after").ShouldContain("TERM_RATIO");
        entries.Single(e => e.Text("path") == "/refundMethods").Text("class").ShouldBe("ADDED");
        entries.Single(e => e.Text("path") == "/changePermissions").Text("class").ShouldBe("ADDED");

        var (same, sameBody) = await GetAsync(_client, "/api/pfc/v1/product-versions/diff?product=MOTOR-DF1&from=1.0&to=1.0", Author);
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
        sameBody.Text("total").ShouldBe("0");

        (await GetAsync(_client, "/api/pfc/v1/product-versions/diff?product=MOTOR-DF1&from=1.0&to=9.9", Author)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await GetAsync(_client, "/api/pfc/v1/product-versions/diff?product=MOTOR-DF1&from=x&to=1.1", Author)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync(_client, "/api/pfc/v1/product-versions/diff?product=MOTOR-DF1&from=1.0&to=1.1", "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task yaml_export_imports_into_a_draft_with_the_same_hash_and_json_works_too()
    {
        var hash = await ImportBothAsync("MOTOR-DF2");
        var (response, exported) = await GetAsync(_client, "/api/pfc/v1/product-versions/export?product=MOTOR-DF2&version=1.0", Author);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, exported?.ToJsonString());
        exported.Text("format").ShouldBe("YAML");
        exported.Text("artefactHash").ShouldBe(hash);

        var (created, body) = await ImportSourceAsync(exported.Text("source"));
        created.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("created").ShouldBe("true");
        body.Text("changeSet.changeSetState.status").ShouldBe("OPEN");
        body.Text("changeSet.changeSetState.baseVersion").ShouldBe("1.1");
        HashOf(body!["changeSet"]!["changeSetState"]!["draft"]!.ToJsonString()).ShouldBe(hash);

        var (jsonExport, jsonBody) = await GetAsync(_client, "/api/pfc/v1/product-versions/export?product=MOTOR-DF2&version=1.1&format=JSON", Author);
        jsonExport.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (viaJson, viaJsonBody) = await ImportSourceAsync(jsonBody.Text("source"), "JSON");
        viaJson.StatusCode.ShouldBe(HttpStatusCode.OK, viaJsonBody?.ToJsonString());
        HashOf(viaJsonBody!["changeSet"]!["changeSetState"]!["draft"]!.ToJsonString()).ShouldBe(jsonBody.Text("artefactHash"));
    }

    [Fact]
    public async Task import_dry_run_stores_nothing_lint_findings_do_not_block_and_no_locked_version_is_created()
    {
        await ImportBothAsync("MOTOR-DF3");
        var (_, exported) = await GetAsync(_client, "/api/pfc/v1/product-versions/export?product=MOTOR-DF3&version=1.0", Author);

        var (dry, dryBody) = await ImportSourceAsync(exported.Text("source"), dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, dryBody?.ToJsonString());
        dryBody.Text("created").ShouldBe("false");
        dryBody.Text("validation.clean").ShouldBe("true");
        (await CountAsync("SELECT count(*) FROM pfc.change_set cs JOIN pfc.product p ON p.product_id = cs.product_id WHERE p.code = 'MOTOR-DF3'")).ShouldBe(0);

        var broken = Seed("MOTOR-DF3");
        broken["elements"]![1]!["parent"] = "NO_SUCH_PARENT";
        var (lint, lintBody) = await ImportSourceAsync(broken.ToJsonString());
        lint.StatusCode.ShouldBe(HttpStatusCode.OK, lintBody?.ToJsonString());
        lintBody.Text("validation.clean").ShouldBe("false");
        lintBody.Text("changeSet.changeSetState.status").ShouldBe("OPEN");
        (await CountAsync("SELECT count(*) FROM pfc.product_version v JOIN pfc.product p ON p.product_id = v.product_id WHERE p.code = 'MOTOR-DF3'")).ShouldBe(2);
    }

    [Fact]
    public async Task import_refuses_bad_yaml_unknown_products_other_legal_entities_and_unauthorised_roles()
    {
        await ImportBothAsync("MOTOR-DF4");
        var (badYaml, badBody) = await ImportSourceAsync("a: [unclosed");
        badYaml.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, badBody?.ToJsonString());
        (await ImportSourceAsync("- just\n- a list")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        (await ImportSourceAsync("a: &x 1\nb: *x")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);

        var (unknownResponse, unknownBody) = await ImportSourceAsync(Seed("MOTOR-DF4X").ToJsonString());
        unknownResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);
        unknownBody!.ToJsonString().ShouldContain("UNKNOWN-PRODUCT");

        var foreign = Seed("MOTOR-DF4");
        foreign["legalEntity"] = "OTHER-LE";
        (await ImportSourceAsync(foreign.ToJsonString())).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent);

        (await ImportSourceAsync(Seed("MOTOR-DF4").ToJsonString())).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (denied, _) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/product-imports/import", new { source = "a: 1" }, "Staff.Billing");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
