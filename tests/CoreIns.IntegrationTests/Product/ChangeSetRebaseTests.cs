using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.Modules.Product;
using Npgsql;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product;

/// <summary>BO-PFC-02: revert one logged edit and rebase a draft onto a newer Locked version, over HTTP on a real PostgreSQL.</summary>
public sealed class ChangeSetRebaseTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private async Task<string> CreateDraftAsync(string code, bool importV11)
    {
        (await ImportAsync(_client, Seed(code))).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        if (importV11)
        {
            var v11 = JsonNode.Parse(ProductSeeds.MotorPrivateCar11Json())!.AsObject();
            v11["product"]!["code"] = code;
            var (imported, body) = await ImportAsync(_client, v11);
            imported.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        }

        var (response, created) = await PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets", new { productCode = code, baseVersion = "1.0" }, Author);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, created?.ToJsonString());
        return created.Text("changeSetState.changeSetId");
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string action, object body) =>
        PartyApi.SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/change-sets/" + action, body, Author);

    [Fact]
    public async Task revert_undoes_a_replace_and_an_add_logs_the_undo_and_refuses_when_a_later_edit_touched_the_path()
    {
        var id = await CreateDraftAsync("MOTOR-RB1", importV11: false);
        var (e1, b1) = await PostAsync("edit", new
        {
            changeSetId = id,
            recordVersion = 1,
            edits = new object[]
            {
                new { op = "replace", path = "/elements/1/maximum", value = 3 },
                new { op = "add", path = "/dayCountNote", value = "draft note" },
            },
        });
        e1.StatusCode.ShouldBe(HttpStatusCode.OK, b1?.ToJsonString());
        b1.Text("changeSetState.history.0.op").ShouldBe("replace");
        var replaceId = b1.Text("changeSetState.history.0.editId");
        var addId = b1.Text("changeSetState.history.1.editId");

        var (r1, rb1) = await PostAsync("revert", new { changeSetId = id, recordVersion = 2, editId = replaceId });
        r1.StatusCode.ShouldBe(HttpStatusCode.OK, rb1?.ToJsonString());
        rb1.Text("changeSetState.recordVersion").ShouldBe("3");
        rb1.Text("changeSetState.editCount").ShouldBe("3");
        rb1.Text("changeSetState.history.2.op").ShouldBe("replace");
        rb1.Text("changeSetState.draft.elements.1.maximum").ShouldNotBe("3");

        var (r2, rb2) = await PostAsync("revert", new { changeSetId = id, recordVersion = 3, editId = addId });
        r2.StatusCode.ShouldBe(HttpStatusCode.OK, rb2?.ToJsonString());
        rb2.Text("changeSetState.history.3.op").ShouldBe("remove");
        rb2!["changeSetState"]!["draft"]!.AsObject().ContainsKey("dayCountNote").ShouldBeFalse();

        // Something else changed the value since: undoing the old replace is stale, then (with a fresh version) a conflict.
        var (e2, _) = await PostAsync("edit", new { changeSetId = id, recordVersion = 4, edits = new object[] { new { op = "replace", path = "/elements/1/maximum", value = 4 } } });
        e2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (stale, staleBody) = await PostAsync("revert", new { changeSetId = id, recordVersion = 4, editId = replaceId });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("PFC-ERR-STALE");
        var (conflict, conflictBody) = await PostAsync("revert", new { changeSetId = id, recordVersion = 5, editId = replaceId });
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        conflictBody.Text("code").ShouldBe("PFC-ERR-CONFLICT");

        var (unknown, unknownBody) = await PostAsync("revert", new { changeSetId = id, recordVersion = 5, editId = Guid.NewGuid() });
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownBody.Text("code").ShouldBe("PFC-ERR-INVALID-EDIT");
    }

    [Fact]
    public async Task rebase_onto_1_1_keeps_the_draft_edit_takes_the_new_terms_and_moves_the_base()
    {
        var id = await CreateDraftAsync("MOTOR-RB2", importV11: true);
        var (edit, _) = await PostAsync("edit", new { changeSetId = id, recordVersion = 1, edits = new object[] { new { op = "replace", path = "/elements/1/maximum", value = 3 } } });
        edit.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await PostAsync("rebase", new { changeSetId = id, recordVersion = 2 });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("changeSetState.baseVersion").ShouldBe("1.1");
        body.Text("changeSetState.recordVersion").ShouldBe("3");
        body.Text("changeSetState.draft.dayCount").ShouldBe("TERM_RATIO");
        body.Text("changeSetState.draft.elements.1.maximum").ShouldBe("3");
        body.Text("changeSetState.draft.refundMethods.0.source").ShouldBe("Policyholder");
        body.Text("changeSetState.draft.version").ShouldBe("1.1");
    }

    [Fact]
    public async Task rebase_with_a_change_on_the_same_path_as_1_1_is_a_conflict_that_lists_the_path_and_changes_nothing()
    {
        var id = await CreateDraftAsync("MOTOR-RB3", importV11: true);
        var (edit, _) = await PostAsync("edit", new { changeSetId = id, recordVersion = 1, edits = new object[] { new { op = "replace", path = "/dayCount", value = "ACT/360" } } });
        edit.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await PostAsync("rebase", new { changeSetId = id, recordVersion = 2, targetVersion = "1.1" });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("PFC-ERR-CONFLICT");
        body!.ToJsonString().ShouldContain("/dayCount");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var state = (string)(await dataSource.CreateCommand($"SELECT record_version || '/' || (draft_json::jsonb ->> 'dayCount') FROM pfc.change_set WHERE change_set_id = '{id}'").ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        state.ShouldBe("2/ACT/360");

        var (missing, missingBody) = await PostAsync("rebase", new { changeSetId = id, recordVersion = 2, targetVersion = "9.9" });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missingBody.Text("code").ShouldBe("PFC-ERR-NO-VERSION");
    }
}
