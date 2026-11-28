using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace CoreIns.IntegrationTests.Work;

/// <summary>
/// BO-WRK-07: notes on business objects: append-only history, visibility, soft delete, edit window, accent-insensitive search and CSV export.
/// </summary>
public sealed class NotesTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Uw = "Staff.Underwriter";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Admin = "Platform.Admin";
    private const string Notes = "/api/wrk/v1/notes";

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
    public async Task A_note_is_created_listed_and_read_back_and_an_edit_never_loses_the_earlier_text()
    {
        var obj = NewObject();
        var created = await CreateAsync(obj, "First text", user: "ann");
        created.Response.StatusCode.ShouldBe(HttpStatusCode.OK, created.Body?.ToJsonString());
        var id = created.Body!["note"]!["id"]!.GetValue<string>();
        created.Body["note"]!["version"]!.GetValue<int>().ShouldBe(1);

        var edited = await SendAsync(HttpMethod.Post, Notes + "/edit", new { id, body = "Second text" }, Uw, "ann");
        edited.Response.StatusCode.ShouldBe(HttpStatusCode.OK, edited.Body?.ToJsonString());
        edited.Body!["note"]!["body"]!.GetValue<string>().ShouldBe("Second text");
        edited.Body["note"]!["edited"]!.GetValue<bool>().ShouldBeTrue();

        // An unchanged body adds no version.
        (await SendAsync(HttpMethod.Post, Notes + "/edit", new { id, body = "Second text" }, Uw, "ann")).Body!["note"]!["version"]!.GetValue<int>().ShouldBe(2);
        await SendAsync(HttpMethod.Post, Notes + "/edit", new { id, body = "Third text" }, Uw, "ann");

        var history = await SendAsync(HttpMethod.Get, $"{Notes}/history?id={id}", null, Uw, "bob");
        history.Response.StatusCode.ShouldBe(HttpStatusCode.OK, history.Body?.ToJsonString());
        history.Body!["items"]!.AsArray().Select(i => i!["note"]!["body"]!.GetValue<string>()).ShouldBe(["First text", "Second text", "Third text"]);

        var listed = await SendAsync(HttpMethod.Get, $"{Notes}?objectRef={obj}", null, Uw, "bob");
        listed.Response.StatusCode.ShouldBe(HttpStatusCode.OK, listed.Body?.ToJsonString());
        listed.Body!["items"]!.AsArray().Count.ShouldBe(1);
        (await SendAsync(HttpMethod.Get, $"{Notes}/{id}", null, Uw, "bob")).Body!["note"]!["body"]!.GetValue<string>().ShouldBe("Third text");

        // A stale copy is refused; only the author edits.
        (await SendAsync(HttpMethod.Post, Notes + "/edit", new { id, body = "Fourth", recordVersion = 1 }, Uw, "ann")).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, Notes + "/edit", new { id, body = "Hijack" }, Uw, "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stored_versions_cannot_be_updated_or_deleted_even_by_the_database_owner()
    {
        var created = await CreateAsync(NewObject(), "Immutable", user: "ann");
        var id = created.Body!["note"]!["id"]!.GetValue<string>();
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await Should.ThrowAsync<PostgresException>(async () =>
            await dataSource.CreateCommand($"UPDATE wrk.note_version SET body = 'tampered' WHERE note_id = '{id}'").ExecuteNonQueryAsync(Ct));
        await Should.ThrowAsync<PostgresException>(async () =>
            await dataSource.CreateCommand($"DELETE FROM wrk.note_version WHERE note_id = '{id}'").ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Visibility_limits_who_sees_a_note()
    {
        var obj = NewObject();
        var staff = Id(await CreateAsync(obj, "for everyone", "STAFF", "ann"));
        var managers = Id(await CreateAsync(obj, "for managers", "MANAGERS", "ann"));
        var secret = Id(await CreateAsync(obj, "only me", "PRIVATE", "ann"));

        (await IdsAsync(obj, Uw, "bob")).ShouldBe([staff]);
        (await IdsAsync(obj, Manager, "mia")).ShouldBe([managers, staff], ignoreOrder: true);
        (await IdsAsync(obj, Uw, "ann")).ShouldBe([secret, managers, staff], ignoreOrder: true);

        // A note that is hidden from the asker does not exist for them (get, history).
        (await SendAsync(HttpMethod.Get, $"{Notes}/{secret}", null, Admin, "root")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"{Notes}/{managers}", null, Uw, "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"{Notes}/history?id={secret}", null, Uw, "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The author may change the visibility; the note then follows the new level.
        (await SendAsync(HttpMethod.Post, Notes + "/edit", new { id = secret, visibility = "STAFF" }, Uw, "ann")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdsAsync(obj, Uw, "bob")).ShouldContain(secret);

        (await CreateAsync(obj, "x", "EVERYBODY", "ann")).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await CreateAsync("nonsense", "x", "STAFF", "ann")).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await CreateAsync(obj, "  ", "STAFF", "ann")).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_ignores_case_accents_and_final_sigma_and_respects_visibility()
    {
        var obj = NewObject();
        var other = NewObject();
        await CreateAsync(obj, "Ο κ. Παπαδόπουλος ζήτησε ΑΝΑΒΟΛΗ πληρωμής", "STAFF", "ann");
        await CreateAsync(other, "Ο Παπαδόπουλος τηλεφώνησε ξανά", "STAFF", "ann");
        await CreateAsync(obj, "Ιδιωτική σημείωση: Παπαδόπουλος", "PRIVATE", "ann");

        foreach (var q in new[] { "παπαδοπουλος", "ΠΑΠΑΔΟΠΟΥΛΟΣ", "αναβολη", "Αναβολή πληρωμης" })
        {
            var hits = await SearchAsync(q, obj, Uw, "bob");
            hits.Count.ShouldBe(1, $"q={q}");
        }

        (await SearchAsync("Παπαδόπουλος", obj, Uw, "ann")).Count.ShouldBe(2); // the author also sees the private one
        (await SearchAsync("Παπαδόπουλος", null, Uw, "bob")).Count.ShouldBeGreaterThanOrEqualTo(2); // across objects
        (await SearchAsync("δεν υπάρχει", obj, Uw, "bob")).ShouldBeEmpty();
        (await SendAsync(HttpMethod.Get, $"{Notes}/search", null, Uw, "bob")).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_is_soft_hides_the_note_and_keeps_it_for_managers_and_the_edit_window_is_enforced()
    {
        var obj = NewObject();
        var mine = Id(await CreateAsync(obj, "to delete", "STAFF", "ann"));
        var deleted = await SendAsync(HttpMethod.Post, Notes + "/delete", new { id = mine }, Uw, "ann");
        deleted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, deleted.Body?.ToJsonString());
        deleted.Body!["note"]!["deleted"]!.GetValue<bool>().ShouldBeTrue();
        (await IdsAsync(obj, Uw, "ann")).ShouldBeEmpty();
        (await SendAsync(HttpMethod.Get, $"{Notes}/{mine}", null, Uw, "ann")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var seen = await SendAsync(HttpMethod.Get, $"{Notes}/{mine}", null, Manager, "mia");
        seen.Body!["note"]!["body"]!.GetValue<string>().ShouldBe("to delete");
        (await SendAsync(HttpMethod.Get, $"{Notes}/history?id={mine}", null, Manager, "mia")).Body!["items"]!.AsArray().Count.ShouldBe(1);

        // Someone else's note: staff may not, a manager needs a reason.
        var theirs = Id(await CreateAsync(obj, "not yours", "STAFF", "ann"));
        (await SendAsync(HttpMethod.Post, Notes + "/delete", new { id = theirs }, Uw, "bob")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, Notes + "/delete", new { id = theirs }, Manager, "mia")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, Notes + "/delete", new { id = theirs, reason = "wrong object" }, Manager, "mia")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // After 24 hours the author can neither edit nor delete; a manager still can delete.
        var old = Id(await CreateAsync(obj, "old note", "STAFF", "ann"));
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await dataSource.CreateCommand($"UPDATE wrk.note SET created_at = now() - interval '2 days' WHERE note_id = '{old}'").ExecuteNonQueryAsync(Ct);
        var edit = await SendAsync(HttpMethod.Post, Notes + "/edit", new { id = old, body = "late change" }, Uw, "ann");
        edit.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        edit.Body!["code"]!.GetValue<string>().ShouldBe("WRK-ERR-EDIT-WINDOW");
        (await SendAsync(HttpMethod.Post, Notes + "/delete", new { id = old }, Uw, "ann")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, Notes + "/delete", new { id = old, reason = "housekeeping" }, Manager, "mia")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Export_gives_a_csv_to_managers_only_and_neutralises_formulas()
    {
        var obj = NewObject();
        await CreateAsync(obj, "plain, with \"quotes\"", "STAFF", "ann");
        await CreateAsync(obj, "=HYPERLINK(\"http://evil\")", "STAFF", "ann");
        await CreateAsync(obj, "private text", "PRIVATE", "ann");

        (await SendAsync(HttpMethod.Post, Notes + "/export", new { objectRef = obj }, Uw, "ann")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var export = await SendAsync(HttpMethod.Post, Notes + "/export", new { objectRef = obj }, Manager, "mia");
        export.Response.StatusCode.ShouldBe(HttpStatusCode.OK, export.Body?.ToJsonString());
        var note = export.Body!["note"]!;
        note["contentType"]!.GetValue<string>().ShouldBe("text/csv");
        note["rows"]!.GetValue<int>().ShouldBe(2); // the private note is not the manager's to read
        var csv = note["content"]!.GetValue<string>();
        csv.ShouldStartWith("noteId,object,visibility,author,createdAt,updatedAt,versions,body");
        csv.ShouldContain("\"plain, with \"\"quotes\"\"\"");
        csv.ShouldContain("\"'=HYPERLINK(");
        csv.ShouldNotContain("private text");
        (await SendAsync(HttpMethod.Post, Notes + "/export", new { }, Manager, "mia")).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    private static string NewObject() => $"POL/Policy/{Guid.NewGuid()}";

    private static string Id((HttpResponseMessage Response, JsonNode? Body) created)
    {
        created.Response.StatusCode.ShouldBe(HttpStatusCode.OK, created.Body?.ToJsonString());
        return created.Body!["note"]!["id"]!.GetValue<string>();
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> CreateAsync(string objectRef, string body, string visibility = "STAFF", string user = "ann") =>
        SendAsync(HttpMethod.Post, Notes, new { objectRef, body, visibility }, Uw, user);

    private async Task<List<string>> IdsAsync(string objectRef, string roles, string user)
    {
        var listed = await SendAsync(HttpMethod.Get, $"{Notes}?objectRef={objectRef}", null, roles, user);
        listed.Response.StatusCode.ShouldBe(HttpStatusCode.OK, listed.Body?.ToJsonString());
        return [.. listed.Body!["items"]!.AsArray().Select(i => i!["note"]!["id"]!.GetValue<string>())];
    }

    private async Task<List<string>> SearchAsync(string q, string? objectRef, string roles, string user)
    {
        var path = $"{Notes}/search?q={Uri.EscapeDataString(q)}" + (objectRef is null ? string.Empty : $"&objectRef={objectRef}");
        var found = await SendAsync(HttpMethod.Get, path, null, roles, user);
        found.Response.StatusCode.ShouldBe(HttpStatusCode.OK, found.Body?.ToJsonString());
        return [.. found.Body!["items"]!.AsArray().Select(i => i!["note"]!["id"]!.GetValue<string>())];
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body, string roles, string user)
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

        var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
