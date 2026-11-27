using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using Npgsql;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product;

/// <summary>BO-PFC-01: the draft and change-set store over HTTP on a real PostgreSQL (create from a Locked version, edit with lint, abandon).</summary>
public sealed class ChangeSetTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private async Task<(string ChangeSetId, JsonNode Body)> CreateAsync(string code)
    {
        (await ImportAsync(_client, Seed(code))).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (response, body) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets", new { productCode = code, baseVersion = "1.0" }, Author);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        return (body.Text("changeSetState.changeSetId"), body!);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> EditAsync(string id, int recordVersion, params object[] edits) =>
        PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets/edit", new { changeSetId = id, recordVersion, edits }, Author);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return (T)(await dataSource.CreateCommand(sql).ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task create_copies_the_locked_version_into_an_open_draft_that_lints_clean_and_leaves_the_version_untouched()
    {
        var (id, body) = await CreateAsync("MOTOR-CS1");
        body.Text("changeSetState.status").ShouldBe("OPEN");
        body.Text("changeSetState.recordVersion").ShouldBe("1");
        body.Text("changeSetState.baseVersion").ShouldBe("1.0");
        body.Text("changeSetState.draft.product.code").ShouldBe("MOTOR-CS1");
        body.Text("validation.clean").ShouldBe("true");

        // The draft is byte-identical to the base artefact; the Locked version is unchanged.
        (await ScalarAsync<bool>($"SELECT c.draft_json::jsonb = a.canonical_json::jsonb FROM pfc.change_set c JOIN pfc.product_version v ON v.product_version_id = c.base_version_id JOIN pfc.artifact a ON a.artefact_hash = v.artefact_hash WHERE c.change_set_id = '{id}'"))
            .ShouldBeTrue();

        var (edit, _) = await EditAsync(id, 1, new { op = "replace", path = "/elements/1/maximum", value = 3 });
        edit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync<string>("SELECT v.status || '/' || v.record_version FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = 'MOTOR-CS1'"))
            .ShouldBe("LOCKED/1");
        (await ScalarAsync<bool>("SELECT a.canonical_json::jsonb @> '{\"elements\":[{\"code\":\"policyLine\"}]}'::jsonb FROM pfc.artifact a JOIN pfc.product_version v USING (artefact_hash) JOIN pfc.product p USING (product_id) WHERE p.code = 'MOTOR-CS1'"))
            .ShouldBeTrue();

        // Only a Locked version can be copied; an unknown product is refused.
        var (missing, missingBody) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets", new { productCode = "MOTOR-CS1", baseVersion = "9.9" }, Author);
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missingBody.Text("code").ShouldBe("PFC-ERR-NO-VERSION");
        var (unknown, unknownBody) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets", new { productCode = "NO-SUCH", baseVersion = "1.0" }, Author);
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownBody.Text("code").ShouldBe("PFC-ERR-UNKNOWN-PRODUCT");
    }

    [Fact]
    public async Task edit_with_an_invalid_parent_is_saved_and_returns_the_lint_finding_and_logs_the_operation()
    {
        var (id, _) = await CreateAsync("MOTOR-CS2");

        var (response, body) = await EditAsync(id, 1, new { op = "replace", path = "/elements/1/parent", value = "noSuchElement" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("changeSetState.recordVersion").ShouldBe("2");
        body.Text("changeSetState.editCount").ShouldBe("1");
        body.Text("changeSetState.draft.elements.1.parent").ShouldBe("noSuchElement");
        body.Text("validation.clean").ShouldBe("false");
        body.Text("validation.findings.0.code").ShouldBe("PFC-LINT-ELEMENT-PARENT");
        body.Text("validation.findings.0.path").ShouldBe("elements[1].parent");

        (await ScalarAsync<string>($"SELECT op || ' ' || path || ' ' || old_json::text || ' ' || new_json::text FROM pfc.change_set_edit WHERE change_set_id = '{id}'"))
            .ShouldBe("replace /elements/1/parent \"policyLine\" \"noSuchElement\"");

        // Fixing it brings the draft clean again; a bad path is refused and changes nothing.
        var (fixedUp, fixedBody) = await EditAsync(id, 2, new { op = "replace", path = "/elements/1/parent", value = "policyLine" });
        fixedUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixedBody.Text("validation.clean").ShouldBe("true");
        var (bad, badBody) = await EditAsync(id, 3, new { op = "remove", path = "/elements/99/parent" });
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badBody.Text("code").ShouldBe("PFC-ERR-INVALID-EDIT");
        (await ScalarAsync<long>($"SELECT record_version::bigint FROM pfc.change_set WHERE change_set_id = '{id}'")).ShouldBe(3);
    }

    [Fact]
    public async Task a_second_edit_on_the_same_record_version_is_stale()
    {
        var (id, _) = await CreateAsync("MOTOR-CS3");
        var (first, _) = await EditAsync(id, 1, new { op = "replace", path = "/elements/1/maximum", value = 2 });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (second, secondBody) = await EditAsync(id, 1, new { op = "replace", path = "/elements/1/maximum", value = 4 });
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        secondBody.Text("code").ShouldBe("PFC-ERR-STALE");
        (await ScalarAsync<long>($"SELECT count(*) FROM pfc.change_set_edit WHERE change_set_id = '{id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task abandon_closes_the_change_set_and_an_abandoned_one_cannot_be_edited()
    {
        var (id, _) = await CreateAsync("MOTOR-CS4");
        var (abandoned, body) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets/abandon", new { changeSetId = id, recordVersion = 1 }, Author);
        abandoned.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("changeSetState.status").ShouldBe("ABANDONED");

        var (edit, editBody) = await EditAsync(id, 2, new { op = "replace", path = "/elements/1/maximum", value = 2 });
        edit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        editBody.Text("code").ShouldBe("PFC-ERR-CHANGE-SET-STATE");
    }

    [Fact]
    public async Task another_legal_entitys_change_set_is_unknown_and_a_role_without_the_permission_is_refused()
    {
        var (id, _) = await CreateAsync("MOTOR-CS5");
        await using (var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await dataSource.CreateCommand($"UPDATE pfc.change_set SET legal_entity_id = gen_random_uuid() WHERE change_set_id = '{id}'")
                .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (other, otherBody) = await EditAsync(id, 1, new { op = "replace", path = "/elements/1/maximum", value = 2 });
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        otherBody.Text("code").ShouldBe("PFC-ERR-CHANGE-SET-NOT-FOUND");

        var (forbidden, _) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets", new { productCode = "MOTOR-CS5", baseVersion = "1.0" }, Staff);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
