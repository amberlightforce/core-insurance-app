using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-04: archive verify, renditions, retention class, completeness report and the evidence-pack export.</summary>
public sealed class ArchiveTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString, settings: new Dictionary<string, string?>
    {
        ["Documents:RetentionClasses:0"] = "TEST-CLASS-A",
        ["Documents:EvidencePackMaxDocuments"] = "3",
    });

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<(string DocumentId, string ArchiveId, string Subject)> RequestAsync(HttpClient client, string marker, Guid party, string suffix = "a")
    {
        var subject = $"case-{marker}-{suffix}";
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", new
        {
            documentType = "DT-REFUND-ADVICE",
            subject = new { module = "BIL", type = "DelinquencyCase", id = subject },
            recipients = new[] { new { role = "ADDRESSEE", partyId = party } },
            deliveryInstruction = new { mode = "EXPLICIT" },
            payloadSupplement = new { requestNo = $"AR-{marker}-{suffix}", placeholders = new { customerName = "Μαρία", policyNo = "POL000000123", refundAmount = "55.00 EUR" } },
        }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body.Text("documentId");
        var (_, doc) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{id}", roles: Admin);
        return (id, doc.Text("archiveId"), subject);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<(string Id, byte[] Bytes)> ExportAsync(HttpClient client, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (response, json) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/evidence-packs/export", body, roles: Admin);
        response.StatusCode.ShouldBe(expected, json?.ToJsonString());
        if (expected != HttpStatusCode.OK)
        {
            return (string.Empty, []);
        }

        var archiveId = json.Text("archiveId");
        var bytes = await DownloadAsync(client, archiveId);
        Sha(bytes).ShouldBe(json.Text("hash"));
        return (archiveId, bytes);
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string archiveId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/doc/v1/archives/{archiveId}/content", UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Admin);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    [Fact]
    public async Task Verify_says_OK_then_TAMPERED_after_the_bytes_change()
    {
        using var client = _factory.CreateClient();
        var (_, archiveId, _) = await RequestAsync(client, Guid.NewGuid().ToString("N")[..8], Guid.CreateVersion7());

        var (ok, okBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/verify", new { archiveId }, roles: Billing);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
        okBody.Text("result").ShouldBe("OK");

        await SqlAsync("UPDATE doc.archive SET content = '\\x4d555441544544'::bytea WHERE archive_id = @id", ("id", Guid.Parse(archiveId)));
        var (_, bad) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/verify", new { archiveId }, roles: Admin);
        bad.Text("result").ShouldBe("TAMPERED");
        bad.Text("computedHash").ShouldNotBe(bad.Text("hash"));

        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/verify", new { archiveId = Guid.NewGuid() }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/verify", new { archiveId }, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rendition_is_listed_with_its_document_and_downloads_with_the_stored_hash()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var (documentId, archiveId, subject) = await RequestAsync(client, marker, Guid.CreateVersion7());

        var pdf = "%PDF-1.7 fake"u8.ToArray();
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/add-rendition", new
        {
            parentArchiveId = archiveId, binaryStream = Convert.ToBase64String(pdf), declaredHash = Sha(pdf), contentType = "application/pdf", renditionKind = "PDF",
        }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var renditionId = body.Text("archiveId");

        // A rendition of a rendition attaches to the archive entry itself.
        var (_, second) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/add-rendition", new
        {
            parentArchiveId = renditionId, binaryStream = Base64("x"), contentType = "text/plain", renditionKind = "TXT",
        }, roles: Admin);

        var (_, list) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/list-for-object?objectRef=BIL/DelinquencyCase/{subject}", roles: Billing);
        var items = list!["items"]!.AsArray();
        items.Count.ShouldBe(3);
        items.All(i => i!.Text("documentId") == documentId).ShouldBeTrue();
        var listed = items.Single(i => i!.Text("archiveId") == renditionId);
        listed!.Text("renditionKind").ShouldBe("PDF");
        listed.Text("parentArchiveId").ShouldBe(archiveId);
        items.Single(i => i!.Text("archiveId") == second.Text("archiveId"))!.Text("parentArchiveId").ShouldBe(archiveId);

        (await DownloadAsync(client, renditionId)).ShouldBe(pdf);

        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/add-rendition", new
        {
            parentArchiveId = archiveId, binaryStream = Base64("y"), declaredHash = Sha(pdf), contentType = "text/plain", renditionKind = "TXT",
        }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/add-rendition", new
        {
            parentArchiveId = Guid.NewGuid(), binaryStream = Base64("y"), contentType = "text/plain", renditionKind = "TXT",
        }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retention_class_must_come_from_the_market_pack_list_and_the_change_is_audited()
    {
        using var client = _factory.CreateClient();
        var (_, archiveId, _) = await RequestAsync(client, Guid.NewGuid().ToString("N")[..8], Guid.CreateVersion7());
        var (_, rendition) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/add-rendition", new
        {
            parentArchiveId = archiveId, binaryStream = Base64("r"), contentType = "text/plain", renditionKind = "TXT",
        }, roles: Admin);

        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/set-retention-class", new { archiveId, retentionClass = "INVENTED-10Y" }, roles: Admin))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (ok, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/set-retention-class", new { archiveId, retentionClass = "TEST-CLASS-A" }, roles: Admin);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        (await CountAsync($"SELECT count(*) FROM doc.archive WHERE retention_class = 'TEST-CLASS-A' AND (archive_id = '{archiveId}' OR archive_id = '{rendition.Text("archiveId")}')")).ShouldBe(2);
        (await CountAsync("SELECT count(*) FROM plt.audit_event WHERE operation = 'doc.Archive.setRetentionClass' AND outcome = 'Succeeded'")).ShouldBeGreaterThanOrEqualTo(1);

        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/set-retention-class", new { archiveId = Guid.NewGuid(), retentionClass = "TEST-CLASS-A" }, roles: Admin))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Completeness_reports_missing_mutated_and_mismatched_archives_and_orphans_without_changing_anything()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var good = await RequestAsync(client, marker, party, "good");
        var mutated = await RequestAsync(client, marker, party, "mut");
        var missing = await RequestAsync(client, marker, party, "miss");
        var differs = await RequestAsync(client, marker, party, "diff");

        await SqlAsync("UPDATE doc.archive SET content = '\\x00'::bytea WHERE archive_id = @id", ("id", Guid.Parse(mutated.ArchiveId)));
        await SqlAsync("DELETE FROM doc.archive WHERE archive_id = @id", ("id", Guid.Parse(missing.ArchiveId)));
        await SqlAsync("UPDATE doc.document SET content_hash = repeat('a', 64) WHERE document_id = @id", ("id", Guid.Parse(differs.DocumentId)));
        var (_, stored) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/store", new { binaryStream = Base64($"orphan-{marker}") }, roles: Admin);
        var orphanId = stored.Text("archiveId");
        await ExportAsync(client, new { packId = good.DocumentId });
        var before = await CountAsync("SELECT count(*) FROM doc.archive");

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var (response, report) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/completeness?from={today}&to={today}", roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, report?.ToJsonString());
        report.Text("truncated").ShouldBe("false");
        var period = report!["periods"]!.AsArray().Single()!;
        period.Text("period").ShouldBe(DateTime.UtcNow.ToString("yyyy-MM"));

        var gaps = period["gaps"]!.AsArray().ToDictionary(g => g!.Text("documentId"), g => g!.Text("kind"));
        gaps[mutated.DocumentId].ShouldBe("HASH_MISMATCH");
        gaps[missing.DocumentId].ShouldBe("MISSING_ARCHIVE");
        gaps[differs.DocumentId].ShouldBe("DOCUMENT_HASH_DIFFERS");
        gaps.ContainsKey(good.DocumentId).ShouldBeFalse();
        period["ok"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        period["issued"]!.GetValue<int>().ShouldBe(period["ok"]!.GetValue<int>() + gaps.Count);
        var orphans = period["orphanArchiveIds"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();
        orphans.ShouldContain(orphanId);
        orphans.Count.ShouldBe(1); // the evidence pack and the letters' archives are not orphans

        (await CountAsync("SELECT count(*) FROM doc.archive")).ShouldBe(before);

        (await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/completeness?from={today}&to=2020-01-01", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(client, HttpMethod.Get, "/api/doc/v1/archives/completeness", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/completeness?from={today}&to={today}", roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Evidence_pack_is_deterministic_and_its_manifest_hashes_match_the_files()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var (documentId, archiveId, subject) = await RequestAsync(client, marker, Guid.CreateVersion7());
        var number = (await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{documentId}", roles: Admin)).Body.Text("documentNumber");
        await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/add-rendition", new
        {
            parentArchiveId = archiveId, binaryStream = Base64("pdf"), contentType = "application/pdf", renditionKind = "PDF",
        }, roles: Admin);

        var (firstId, first) = await ExportAsync(client, new { subject = $"BIL/DelinquencyCase/{subject}" });
        var (secondId, second) = await ExportAsync(client, new { subject = $"BIL/DelinquencyCase/{subject}" });
        secondId.ShouldNotBe(firstId);
        Sha(second).ShouldBe(Sha(first));

        var files = Unzip(first);
        files.Keys.ShouldContain("manifest.json");
        files.Keys.ShouldContain($"documents/{number}/document.json");
        files.Keys.ShouldContain($"documents/{number}/evidence.json");
        files.Keys.Count(k => k.Contains("/artefacts/", StringComparison.Ordinal)).ShouldBe(2);
        var manifest = JsonNode.Parse(files["manifest.json"])!;
        var listed = manifest["files"]!.AsArray();
        listed.Count.ShouldBe(files.Count - 1);
        foreach (var file in listed)
        {
            Sha(files[file!.Text("path")]).ShouldBe(file.Text("sha256"));
        }

        var entry = manifest["documents"]![0]!;
        entry.Text("included").ShouldBe("true");
        entry["artefacts"]!.AsArray().All(a => a!.Text("verified") == "true").ShouldBeTrue();
        files[$"documents/{number}/document.json"].Length.ShouldBeGreaterThan(0);
        JsonNode.Parse(files[$"documents/{number}/document.json"])!["deliveries"]!.AsArray().Count.ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM plt.audit_event WHERE operation = 'doc.EvidencePack.export' AND outcome = 'Succeeded'")).ShouldBeGreaterThanOrEqualTo(2);

        // A tampered artefact is flagged in the manifest, not hidden.
        await SqlAsync("UPDATE doc.archive SET content = '\\x00'::bytea WHERE archive_id = @id", ("id", Guid.Parse(archiveId)));
        var (_, tampered) = await ExportAsync(client, new { packId = documentId });
        var tamperedManifest = JsonNode.Parse(Unzip(tampered)["manifest.json"])!;
        tamperedManifest["documents"]![0]!["artefacts"]!.AsArray().Any(a => a!.Text("verified") == "false").ShouldBeTrue();
    }

    [Fact]
    public async Task Evidence_pack_needs_a_scope_stays_within_the_legal_entity_and_is_size_capped()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var mine = await RequestAsync(client, marker, party, "mine");

        await ExportAsync(client, new { }, HttpStatusCode.UnprocessableEntity);
        await ExportAsync(client, new { subject = "nonsense" }, HttpStatusCode.UnprocessableEntity);
        await ExportAsync(client, new { packId = Guid.NewGuid() }, HttpStatusCode.NotFound);

        var foreign = Guid.CreateVersion7();
        await SqlAsync(
            "INSERT INTO doc.document (document_id, legal_entity_id, document_number, template_key, template_version, request_no, subject_module, subject_type, subject_id, "
            + "recipient_party_id, language, status, medium, archive_id, content_hash, created_at, created_by, rendered_at) "
            + "SELECT @id, gen_random_uuid(), document_number || '-X', template_key, template_version, @req, subject_module, subject_type, subject_id, "
            + "recipient_party_id, language, status, medium, archive_id, content_hash, created_at, created_by, rendered_at FROM doc.document WHERE document_id = @mine",
            ("id", foreign), ("req", $"LE-{marker}-foreign"), ("mine", Guid.Parse(mine.DocumentId)));
        await ExportAsync(client, new { packId = foreign }, HttpStatusCode.NotFound);
        var (_, scoped) = await ExportAsync(client, new { subject = $"BIL/DelinquencyCase/{mine.Subject}" });
        JsonNode.Parse(Unzip(scoped)["manifest.json"])!["documents"]!.AsArray().Count.ShouldBe(1);

        // More than EvidencePackMaxDocuments (3 in this host) is refused.
        for (var i = 0; i < 3; i++)
        {
            await RequestAsync(client, marker, party, $"x{i}");
        }

        await ExportAsync(client, new { partyId = party }, HttpStatusCode.UnprocessableEntity);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/evidence-packs/export", new { packId = mine.DocumentId }, roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Evidence_pack_leaves_out_documents_of_a_restricted_recipient_unless_a_legal_claim_is_stated()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var (documentId, _, _) = await RequestAsync(client, marker, party);
        await SqlAsync(
            "INSERT INTO doc.dsar_restriction (restriction_id, party_id, record_class, reason, legal_basis, retention_end, restricted_at) VALUES (gen_random_uuid(), @p, 'POLICY_DOCUMENT', 'test', 'test', 'UNSET_F404', now())",
            ("p", party));

        var (_, restricted) = await ExportAsync(client, new { packId = documentId });
        var files = Unzip(restricted);
        files.Keys.ShouldBe(["manifest.json"]);
        var entry = JsonNode.Parse(files["manifest.json"])!["documents"]![0]!;
        entry.Text("included").ShouldBe("false");
        entry.Text("excludedReason").ShouldBe("DSAR_RESTRICTED");

        var (_, claim) = await ExportAsync(client, new { packId = documentId, legalClaimReason = "Defence of a legal claim, case 42" });
        Unzip(claim).Keys.Count.ShouldBeGreaterThan(1);
        (await CountAsync("SELECT count(*) FROM doc.archive WHERE metadata ->> 'legalClaimReason' = 'Defence of a legal claim, case 42'")).ShouldBe(1);
    }
}
