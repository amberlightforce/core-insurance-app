using System.Text.Json;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Dsar;

/// <summary>SL6-DSAR-PART-A: PTY, POL, UW and RAT participants against a real database: export finds the seeded subject, erase reports every class, a second erase is idempotent.</summary>
public sealed class DsarPartATests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PTY_export_finds_the_seeded_party_and_erase_anonymises_only_contact_preferences()
    {
        var party = await SeedPartyAsync();
        await using var host = new ApiHostFactory(database.AppConnectionString);
        var pty = Participant(host, "PTY");
        var subject = SubjectRef.ForParty(new PartyId(party));
        var context = new DsarContext(Guid.NewGuid(), "tester", "dpo");

        var export = await pty.ExportAsync(subject, context, Ct);
        using var json = JsonDocument.Parse(export.ExportPayload!);
        json.RootElement.GetProperty("party").GetArrayLength().ShouldBe(1);
        json.RootElement.GetProperty("contactPoints").GetArrayLength().ShouldBe(2);
        Count(export, "CONTACT_POINT").ShouldBe(1);

        var erase = await pty.EraseAsync(subject, context, Ct);
        erase.Outcomes.Select(o => o.RecordClass).ToArray().ShouldBe(["PARTY_RECORD", "ADDRESS", "CONTACT_POINT_PRIMARY", "CONTACT_POINT", "INTERMEDIARY"]);
        erase.Outcomes.Single(o => o.RecordClass == "CONTACT_POINT").Kind.ShouldBe(DsarOutcomeKind.Anonymised);
        var master = erase.Outcomes.Single(o => o.RecordClass == "PARTY_RECORD");
        master.Kind.ShouldBe(DsarOutcomeKind.Restricted);
        master.RetentionEnd.ShouldBe(DsarRetention.UnsetF404);
        erase.Outcomes.Single(o => o.RecordClass == "ADDRESS").Kind.ShouldBe(DsarOutcomeKind.NotHeld);

        var again = await pty.EraseAsync(subject, context, Ct);
        again.Outcomes.Select(o => (o.RecordClass, o.Kind)).ToArray().ShouldBe(erase.Outcomes.Select(o => (o.RecordClass, o.Kind)).ToArray());

        (await ScalarAsync($"SELECT value FROM pty.party_contact_point WHERE party_id = '{party}' AND NOT is_primary")).ShouldBe("[erased]");
        (await ScalarAsync($"SELECT value FROM pty.party_contact_point WHERE party_id = '{party}' AND is_primary")).ShouldBe("primary@example.test");
        (await ScalarAsync($"SELECT count(*) FROM pty.dsar_restriction WHERE party_id = '{party}'")).ShouldBe("2"); // PARTY_RECORD + CONTACT_POINT_PRIMARY
    }

    [Fact]
    public async Task POL_export_finds_seeded_records_and_erase_clears_only_unbound_quote_drafts()
    {
        var party = Guid.CreateVersion7();
        var draftJob = await SeedJobAsync(party, bound: false);
        await SeedJobAsync(party, bound: true);
        await using var host = new ApiHostFactory(database.AppConnectionString);
        var pol = Participant(host, "POL");
        var subject = SubjectRef.ForParty(new PartyId(party));
        var context = new DsarContext(Guid.NewGuid(), "tester", "dpo");

        var export = await pol.ExportAsync(subject, context, Ct);
        using var json = JsonDocument.Parse(export.ExportPayload!);
        json.RootElement.GetProperty("jobs").GetArrayLength().ShouldBe(2);
        json.RootElement.GetProperty("quoteVersions").GetArrayLength().ShouldBe(2);
        Count(export, "QUOTE_DRAFT").ShouldBe(1);

        var erase = await pol.EraseAsync(subject, context, Ct);
        erase.Outcomes.Select(o => o.RecordClass).ToArray().ShouldBe(["QUOTE_DRAFT", "POLICY_DOCUMENT", "FISCAL_DOCUMENT"]);
        erase.Outcomes.Single(o => o.RecordClass == "QUOTE_DRAFT").Kind.ShouldBe(DsarOutcomeKind.Anonymised);
        var policyDocs = erase.Outcomes.Single(o => o.RecordClass == "POLICY_DOCUMENT");
        policyDocs.Kind.ShouldBe(DsarOutcomeKind.Restricted);
        policyDocs.RetentionEnd.ShouldBe(DsarRetention.UnsetF404);
        erase.Outcomes.Single(o => o.RecordClass == "FISCAL_DOCUMENT").Kind.ShouldBe(DsarOutcomeKind.NotHeld);

        var again = await pol.EraseAsync(subject, context, Ct);
        again.Outcomes.Select(o => (o.RecordClass, o.Kind)).ToArray().ShouldBe(erase.Outcomes.Select(o => (o.RecordClass, o.Kind)).ToArray());

        (await ScalarAsync($"SELECT risk_tree::text FROM pol.quote_version WHERE job_id = '{draftJob}'")).ShouldBe("{}");
        (await ScalarAsync($"SELECT count(*) FROM pol.quote_version q JOIN pol.job j USING (job_id) WHERE j.policyholder_party_id = '{party}' AND q.risk_tree <> '{{}}'::jsonb")).ShouldBe("1");
    }

    [Fact]
    public async Task UW_and_RAT_report_every_class_and_never_skip_silently()
    {
        await using var host = new ApiHostFactory(database.AppConnectionString);
        var subject = SubjectRef.ForParty(new PartyId(Guid.CreateVersion7()));
        var context = new DsarContext(Guid.NewGuid(), "tester", "dpo");
        foreach (var code in new[] { "UW", "RAT" })
        {
            var participant = Participant(host, code);
            (await participant.ExportAsync(subject, context, Ct)).Outcomes.ShouldNotBeEmpty();
            var erase = await participant.EraseAsync(subject, context, Ct);
            erase.Outcomes.ShouldNotBeEmpty();
            erase.Outcomes.ShouldAllBe(o => o.Kind == DsarOutcomeKind.NotHeld && o.Detail != null);
        }
    }

    private static IDsarParticipant Participant(ApiHostFactory host, string module) =>
        host.Services.GetServices<IDsarParticipant>().Single(p => p.Module == module);

    private static int Count(DsarModuleResult result, string recordClass) => result.Outcomes.Single(o => o.RecordClass == recordClass).RecordCount;

    private async Task<Guid> SeedPartyAsync()
    {
        var party = Guid.CreateVersion7();
        var le = ApiHostFactory.LegalEntityId;
        await ExecuteAsync($"""
            INSERT INTO pty.party (party_id, legal_entity_id, jurisdiction, party_number, party_type, status, preferred_language, record_version, created_at, created_by, updated_at)
            VALUES ('{party}', '{le}', 'GR', 'P{party:N}', 'PERSON', 'ACTIVE', 'el', 1, now(), 'test', now());
            INSERT INTO pty.party_contact_point (contact_point_id, party_id, legal_entity_id, valid_from, recorded_from, created_by, type, value, purpose, is_primary, verification_status, source)
            VALUES ('{Guid.CreateVersion7()}', '{party}', '{le}', current_date, now(), 'test', 'EMAIL', 'primary@example.test', 'PERSONAL', true, 'VERIFIED', 'test'),
                   ('{Guid.CreateVersion7()}', '{party}', '{le}', current_date, now(), 'test', 'MOBILE', '+306900000000', 'PERSONAL', false, 'UNVERIFIED', 'test');
            """);
        return party;
    }

    private async Task<Guid> SeedJobAsync(Guid party, bool bound)
    {
        var job = Guid.CreateVersion7();
        var bind = bound ? $"'{Guid.CreateVersion7()}'" : "NULL";
        await ExecuteAsync($"""
            INSERT INTO pol.job (job_id, legal_entity_id, jurisdiction, job_number, job_type, state, referred, policy_id, policyholder_party_id, product_code,
                product_version, artefact_hash, resolution_hash, resolution_manifest, channel, quote_type, effective_at, expiration_at, currency,
                current_version_no, record_version, created_at, created_by, updated_at, bound_transaction_id)
            VALUES ('{job}', '{ApiHostFactory.LegalEntityId}', 'GR', 'DSAR-{job:N}', 'SUBMISSION', '{(bound ? "BOUND" : "DRAFT")}', false, '{Guid.CreateVersion7()}', '{party}',
                'P', '1.0', 'a', 'r', jsonb_build_object(), 'STAFF', 'FULL', now(), now() + interval '1 year', 'EUR', 1, 1, now(), 'test', now(), {bind});
            INSERT INTO pol.quote_version (quote_id, job_id, legal_entity_id, version_no, state, draft_version, risk_tree, record_version, created_at, updated_at)
            VALUES ('{Guid.CreateVersion7()}', '{job}', '{ApiHostFactory.LegalEntityId}', 1, 'DRAFT', 0, jsonb_build_object('insured', 'x'), 1, now(), now());
            """);
        return job;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand(sql);
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
