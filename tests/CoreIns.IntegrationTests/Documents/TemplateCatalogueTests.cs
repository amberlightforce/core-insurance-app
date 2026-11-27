using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-05: the persisted document type, template and clause catalogue and the binding that resolves a version.</summary>
public sealed class TemplateCatalogueTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private Task InsertTemplateAsync(string type, int version, string status, string validFrom) => ExecAsync(
        "INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en) "
        + "VALUES (@id, @type, @version, @status, @from::date, 't', 's', 't', 'b')",
        ("id", Guid.CreateVersion7()), ("type", type), ("version", version), ("status", status), ("from", validFrom));

    private static async Task<string> ResolveVersionAsync(HttpClient client, string type, string validAt)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, $"/api/doc/v1/bindings/resolve?validAt={validAt}", new { documentType = type }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("version");
    }

    [Fact]
    public async Task The_seeded_catalogue_matches_the_four_original_templates()
    {
        using var client = _factory.CreateClient();
        var (typesResponse, types) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/document-types", roles: Admin);
        typesResponse.StatusCode.ShouldBe(HttpStatusCode.OK, types?.ToJsonString());
        var items = types!["items"]!.AsArray();
        items.Select(i => i.Text("documentType")).ShouldBe(["DT-CANCELLATION-NOTICE", "DT-NONPAY-NOTICE", "DT-NONRENEWAL-NOTICE", "DT-REFUND-ADVICE", "DT-WITHDRAWAL-ACK"]);
        var nonPay = items.Single(i => i.Text("documentType") == "DT-NONPAY-NOTICE");
        nonPay.Text("defaultMedium").ShouldBe("REGISTERED_POST");
        nonPay.Text("titleEn").ShouldBe("Notice of non-payment of premium");
        nonPay!["requiredFields"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldBe(["customerName", "policyNo", "amountDue", "dueDate"]);
        items.Single(i => i.Text("documentType") == "DT-REFUND-ADVICE").Text("defaultMedium").ShouldBe("EMAIL");

        var (_, templates) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/templates?filters=DT-WITHDRAWAL-ACK", roles: Admin);
        templates!["items"]!.AsArray().Count.ShouldBe(1);
        templates.Text("items.0.status").ShouldBe("APPROVED");
        templates.Text("items.0.version").ShouldBe("1");
        templates.Text("items.0.titleEl").ShouldBe("Επιβεβαίωση υπαναχώρησης");
    }

    [Fact]
    public async Task Resolution_follows_the_effective_date_and_never_picks_an_unapproved_version()
    {
        using var client = _factory.CreateClient();
        await InsertTemplateAsync("DT-REFUND-ADVICE", 2, "APPROVED", "2099-01-01");
        await InsertTemplateAsync("DT-REFUND-ADVICE", 3, "DRAFT", "2000-01-01");

        (await ResolveVersionAsync(client, "DT-REFUND-ADVICE", "2026-06-01")).ShouldBe("1");
        (await ResolveVersionAsync(client, "DT-REFUND-ADVICE", "2099-02-01")).ShouldBe("2"); // the DRAFT v3 is never picked

        var (missing, _) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/bindings/resolve", new { documentType = "DT-UNKNOWN" }, roles: Admin);
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_scoped_template_wins_only_for_its_scope()
    {
        using var client = _factory.CreateClient();
        await ExecAsync(
            "INSERT INTO doc.template (template_id, document_type, version, status, valid_from, product_code, title_el, body_el, title_en, body_en) "
            + "VALUES (@id, 'DT-CANCELLATION-NOTICE', 2, 'APPROVED', '2000-01-01', 'MOTOR', 't', 's', 't', 'b')", ("id", Guid.CreateVersion7()));

        var (_, motor) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/bindings/resolve", new { documentType = "DT-CANCELLATION-NOTICE", productCode = "MOTOR" }, roles: Admin);
        motor.Text("version").ShouldBe("2");
        var (_, other) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/bindings/resolve", new { documentType = "DT-CANCELLATION-NOTICE", productCode = "HOME" }, roles: Admin);
        other.Text("version").ShouldBe("1");
    }

    [Fact]
    public async Task A_request_stores_the_template_version_it_rendered_from()
    {
        using var client = _factory.CreateClient();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", new
        {
            documentType = "DT-WITHDRAWAL-ACK",
            subject = new { module = "POL", type = "Policy", id = Guid.NewGuid().ToString() },
            recipients = new[] { new { role = "ADDRESSEE", partyId = Guid.CreateVersion7() } },
            deliveryInstruction = new { mode = "EXPLICIT", channel = (string?)null },
            payloadSupplement = new { requestNo = Guid.NewGuid().ToString(), placeholders = new { customerName = "Μαρία", policyNo = "POL1", withdrawalDate = "2026-10-02" } },
        }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var version = await ScalarAsync("SELECT template_version FROM doc.document WHERE document_id = @id", ("id", Guid.Parse(body.Text("documentId"))));
        version.ShouldBe(1);
    }

    [Fact]
    public async Task A_clause_is_read_by_code_or_id_and_an_unknown_one_is_404()
    {
        using var client = _factory.CreateClient();
        var id = Guid.CreateVersion7();
        await ExecAsync(
            "INSERT INTO doc.clause (clause_id, code, status, title_el, text_el, title_en, text_en) VALUES (@id, 'CL-TEST-1', 'APPROVED', 'Τίτλος', 'Κείμενο', 'Title', 'Text')", ("id", id));

        var (byCode, body) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/clauses/CL-TEST-1", roles: Admin);
        byCode.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("textEn").ShouldBe("Text");
        (await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/clauses/{id}", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Get, "/api/doc/v1/clauses/NOPE", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
