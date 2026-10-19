using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Bil;
using CoreIns.IntegrationTests.Claims;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Dsar;

/// <summary>
/// SL6-DSAR-PART-B: the BIL, FIN, CLM and RI participants on a real database. Seeded subject: export finds exactly what was
/// seeded; erase returns one outcome per record class (restrict-only classes RESTRICTED with UNSET_F404, empty classes
/// NOT_HELD, no silent skip) and a second erase is idempotent (one restriction row per subject and class).
/// </summary>
public sealed class DsarPartBTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DsarContext Context() => new(Guid.NewGuid(), "dpo-requester", "dpo-approver");

    private static IDsarParticipant Participant(IServiceProvider services, string module) =>
        services.GetServices<IDsarParticipant>().Single(p => p.Module == module);

    [Fact]
    public async Task BIL_export_finds_the_seeded_payee_account_and_erase_restricts_it_idempotently()
    {
        await using var slice = new DisbursementSlice(database);
        var party = PartyId.New();
        await slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var participant = Participant(slice.Factory.Services, "BIL");
        var subject = SubjectRef.ForParty(party);

        var export = await participant.ExportAsync(subject, Context(), Ct);
        export.Outcomes.Single(o => o.RecordClass == "PAYEE_ACCOUNT").RecordCount.ShouldBe(1);
        export.Outcomes.Single(o => o.RecordClass == DsarRetentionPolicy.Invoice).Kind.ShouldBe(DsarOutcomeKind.NotHeld);
        JsonNode.Parse(export.ExportPayload!)!["records"]!["PAYEE_ACCOUNT"]!.AsArray().Count.ShouldBe(1);

        var first = await participant.EraseAsync(subject, Context(), Ct);
        var second = await participant.EraseAsync(subject, Context(), Ct);
        first.Outcomes.Count.ShouldBe(3);
        var payee = first.Outcomes.Single(o => o.RecordClass == "PAYEE_ACCOUNT");
        payee.Kind.ShouldBe(DsarOutcomeKind.Restricted);
        payee.RetentionEnd.ShouldBe(DsarRetention.UnsetF404);
        payee.LegalBasis.ShouldNotBeNullOrWhiteSpace();
        second.Outcomes.Select(o => (o.RecordClass, o.Kind)).ShouldBe(first.Outcomes.Select(o => (o.RecordClass, o.Kind)));
        (await slice.ScalarAsync<long>($"SELECT count(*) FROM bil.dsar_restriction WHERE subject_party_id = '{party.Value}'")).ShouldBe(1);
    }

    [Fact]
    public async Task CLM_export_finds_the_seeded_claim_and_erase_restricts_the_claim_file_idempotently()
    {
        await using var slice = new ClaimsSlice(database.AppConnectionString);
        var policy = slice.Policy();
        var (response, body) = await slice.SubmitAsync(ClaimsSlice.Fnol(policy));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        var claimNumber = body.Text("claimNumber");
        var subject = SubjectRef.ForParty(new PartyId(policy.InsuredPartyId));
        var participant = Participant(slice.Factory.Services, "CLM");

        var export = await participant.ExportAsync(subject, Context(), Ct);
        export.Outcomes.Single().RecordCount.ShouldBeGreaterThanOrEqualTo(1);
        export.ExportPayload!.ShouldContain(claimNumber);

        var first = await participant.EraseAsync(subject, Context(), Ct);
        var second = await participant.EraseAsync(subject, Context(), Ct);
        first.Outcomes.Single().Kind.ShouldBe(DsarOutcomeKind.Restricted);
        first.Outcomes.Single().RecordClass.ShouldBe(DsarRetentionPolicy.ClaimFile);
        first.Outcomes.Single().RetentionEnd.ShouldBe(DsarRetention.UnsetF404);
        second.Outcomes.Single().Kind.ShouldBe(DsarOutcomeKind.Restricted);
        (await database.ScalarAsync<long>($"SELECT count(*) FROM clm.dsar_restriction WHERE subject_party_id = '{policy.InsuredPartyId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task All_four_participants_are_registered_and_report_not_held_for_an_unknown_subject_without_skipping()
    {
        await using var slice = new ClaimsSlice(database.AppConnectionString);
        var registry = new DsarParticipantRegistry(slice.Factory.Services.GetServices<IDsarParticipant>());
        registry.Missing(["BIL", "FIN", "CLM", "RI"]).ShouldBeEmpty();
        var subject = SubjectRef.ForParty(PartyId.New());

        foreach (var module in new[] { "BIL", "FIN", "CLM", "RI" })
        {
            var participant = registry.Find(module)!;
            var export = await participant.ExportAsync(subject, Context(), Ct);
            var erase = await participant.EraseAsync(subject, Context(), Ct);
            var again = await participant.EraseAsync(subject, Context(), Ct);
            export.Outcomes.ShouldNotBeEmpty();
            export.HasNotBuilt.ShouldBeFalse();
            erase.Outcomes.ShouldNotBeEmpty();
            erase.Outcomes.ShouldAllBe(o => o.Kind == DsarOutcomeKind.NotHeld);
            again.Outcomes.Count.ShouldBe(erase.Outcomes.Count);
        }
    }
}
