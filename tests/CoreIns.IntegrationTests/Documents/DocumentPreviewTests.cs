using System.Net;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-06: <c>doc.Document.preview</c> composes from the catalogue and writes nothing.</summary>
public sealed class DocumentPreviewTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task<long> CountAsync(string table)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT count(*) FROM doc.{table}");
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static object Request(object payload, string? templateId = null) => new
    {
        documentType = "DT-REFUND-ADVICE", language = "el", templateId, payload,
    };

    [Fact]
    public async Task Preview_renders_the_approved_template_and_persists_nothing()
    {
        using var client = _factory.CreateClient();
        var documents = await CountAsync("document");
        var archives = await CountAsync("archive");

        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/preview",
            Request(new { customerName = "Μαρία", policyNo = "POL-9", refundAmount = "50" }), roles: Admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("templateStatus").ShouldBe("APPROVED");
        body.Text("templateVersion").ShouldBe("1");
        body.Text("html").ShouldContain("το ποσό 50,00\u00A0€ για το ασφαλιστήριο POL-9");
        (await CountAsync("document")).ShouldBe(documents);
        (await CountAsync("archive")).ShouldBe(archives);
    }

    [Fact]
    public async Task Preview_refuses_an_undeclared_payload_field()
    {
        using var client = _factory.CreateClient();

        var (response, _) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/preview",
            Request(new { customerName = "Μαρία", policyNo = "POL-9", refundAmount = "50", iban = "GR16" }), roles: Admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Preview_can_render_a_draft_version_by_id_but_resolution_never_picks_it()
    {
        using var client = _factory.CreateClient();
        var draftId = Guid.CreateVersion7();
        await using (var source = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var command = source.CreateCommand(
                "INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en) "
                + "VALUES (@id, 'DT-REFUND-ADVICE', 7, 'DRAFT', DATE '2000-01-01', 'draft', '<p>DRAFT {{policyNo}}</p>', 'draft', '<p>DRAFT {{policyNo}}</p>')");
            command.Parameters.AddWithValue("id", draftId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var payload = new { customerName = "A", policyNo = "POL-7", refundAmount = "1" };
        var (byId, draft) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/preview", Request(payload, draftId.ToString()), roles: Admin);
        byId.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        draft.Text("templateStatus").ShouldBe("DRAFT");
        draft.Text("html").ShouldContain("<p>DRAFT POL-7</p>");

        var (_, resolved) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/preview", Request(payload), roles: Admin);
        resolved.Text("templateStatus").ShouldBe("APPROVED");
    }
}
