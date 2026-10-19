using System.Text.Json.Nodes;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>SL6-DSAR-PART-C: the CHN, DOC, CMP and MIG participants find the seeded subject, restrict (never erase) regulated classes and are idempotent.</summary>
public sealed class DsarPartCParticipantTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private IDsarParticipant Participant(string module) =>
        _factory.Services.GetServices<IDsarParticipant>().Single(p => p.Module == module);

    private static DsarContext Case => new(Guid.NewGuid(), "tester", "dpo");

    private async Task SeedAsync<TContext>(Action<TContext, IClock> seed)
        where TContext : DbContext
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        seed(db, scope.ServiceProvider.GetRequiredService<IClock>());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertDsarAsync(string module, SubjectRef subject, IReadOnlyDictionary<string, int> seeded)
    {
        var participant = Participant(module);
        var ct = TestContext.Current.CancellationToken;

        // Export: found == seeded, and an unrelated subject holds nothing.
        var export = await participant.ExportAsync(subject, Case, ct);
        var payload = JsonNode.Parse(export.ExportPayload!)!.AsObject();
        foreach (var (recordClass, count) in seeded)
        {
            payload[recordClass]!.AsArray().Count.ShouldBe(count, recordClass);
            export.Outcomes.Single(o => o.RecordClass == recordClass).RecordCount.ShouldBe(count);
        }

        var stranger = await participant.ExportAsync(SubjectRef.ForParty(PartyId.New()), Case, ct);
        stranger.Outcomes.ShouldAllBe(o => o.Kind == DsarOutcomeKind.NotHeld);

        // Erase: regulated classes are restricted with UNSET_F404, nothing is skipped silently, and a second run is identical.
        var first = await participant.EraseAsync(subject, Case, ct);
        var second = await participant.EraseAsync(subject, Case, ct);
        first.Outcomes.Select(o => o.RecordClass).OrderBy(c => c).ShouldBe(seeded.Keys.OrderBy(c => c));
        first.Outcomes.ShouldAllBe(o => o.Kind == DsarOutcomeKind.Restricted && o.RetentionEnd == DsarRetention.UnsetF404 && !string.IsNullOrEmpty(o.LegalBasis));
        second.Outcomes.Select(o => (o.RecordClass, o.Kind, o.RecordCount)).ShouldBe(first.Outcomes.Select(o => (o.RecordClass, o.Kind, o.RecordCount)));
        first.HasNotBuilt.ShouldBeFalse();

        // The data itself is still exported after the erase request (restrict-only, nothing destroyed).
        var after = await participant.ExportAsync(subject, Case, ct);
        after.Outcomes.Sum(o => o.RecordCount).ShouldBe(seeded.Values.Sum());
    }

    [Fact]
    public async Task Documents_participant_exports_and_restricts_documents_deliveries_and_proof()
    {
        var party = PartyId.New();
        var entity = new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId));
        var documentId = new DocumentId(Guid.CreateVersion7());
        var deliveryId = new DeliveryId(Guid.CreateVersion7());
        await SeedAsync<DocumentsDbContext>((db, clock) =>
        {
            db.Documents.Add(new DocumentRow
            {
                DocumentId = documentId, LegalEntityId = entity, DocumentNumber = "D-" + Guid.NewGuid(), TemplateKey = "DT-NONPAY-NOTICE", RequestNo = Guid.NewGuid().ToString(),
                SubjectModule = "BIL", SubjectType = "DelinquencyCase", SubjectId = "1", RecipientPartyId = party, Status = "Rendered", Medium = "REGISTERED_POST",
                ArchiveId = Guid.NewGuid(), ContentHash = new string('a', 64), CreatedAt = clock.Now, CreatedBy = "test",
            });
            db.Deliveries.Add(new DeliveryRow
            {
                DeliveryId = deliveryId, DocumentId = documentId, RecipientPartyId = party, Channel = "stub-post", Medium = "REGISTERED_POST", Status = "Sent",
                CreatedAt = clock.Now, UpdatedAt = clock.Now,
            });
            db.Evidence.Add(new EvidenceRow
            {
                EvidenceId = Guid.NewGuid(), DeliveryId = deliveryId, DocumentId = documentId, Provider = "stub-post", ProviderEventId = Guid.NewGuid().ToString(),
                EventType = "sent", Outcome = "APPLIED", ReceivedAt = clock.Now,
            });
        });

        await AssertDsarAsync("DOC", SubjectRef.ForParty(party), new Dictionary<string, int>
        {
            [DsarRetentionPolicy.PolicyDocument] = 1, ["DELIVERY"] = 1, ["DELIVERY_PROOF"] = 1,
        });
    }

    [Fact]
    public async Task Compliance_participant_restricts_fiscal_documents()
    {
        var party = PartyId.New();
        await SeedAsync<ComplianceDbContext>((db, clock) => db.FiscalDocuments.Add(new FiscalDocumentRow
        {
            FiscalDocumentId = new FiscalDocumentId(Guid.CreateVersion7()), LegalEntityId = new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId)), Jurisdiction = "GR",
            SourceType = "INVOICE", SourceId = Guid.NewGuid().ToString(), Role = "ISSUE", Revision = 0, Status = "PENDING", DocumentType = "1.1",
            IssueDate = new BusinessDate(2026, 10, 1), CounterpartyPartyId = party, Total = 120m, Currency = "EUR", Channel = "stub", Stub = true,
            CreatedAt = clock.Now, CreatedBy = "test", RecordVersion = 1,
        }));

        await AssertDsarAsync("CMP", SubjectRef.ForParty(party), new Dictionary<string, int> { [DsarRetentionPolicy.FiscalDocument] = 1 });
    }

    [Fact]
    public async Task Channels_participant_finds_withdrawals_by_policy_and_restricts_them()
    {
        var party = PartyId.New();
        var policy = new PolicyId(Guid.CreateVersion7());
        await SeedAsync<ChannelsDbContext>((db, clock) => db.WithdrawalRequests.Add(new WithdrawalRequestRow
        {
            WithdrawalRequestId = Guid.CreateVersion7(), RequestNumber = "WDR-" + Guid.NewGuid().ToString("N")[..10], LegalEntityId = new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId)),
            Jurisdiction = "GR", PolicyId = policy, DeclarantName = "Test Declarant", DeclarantEmail = "declarant@example.test", Channel = "WEB_DIRECT", Status = "DECLARED",
            DeclaredAt = clock.Now, DeclaredBy = "test", ConcludedAt = clock.Now, WindowDays = 14, DeadlineAt = clock.Now, ChannelRef = "WDR-" + Guid.NewGuid().ToString("N")[..10], RecordVersion = 1,
        }));
        var subject = new SubjectRef(party, new Dictionary<string, IReadOnlyList<string>> { ["policyId"] = [policy.Value.ToString()] });

        await AssertDsarAsync("CHN", subject, new Dictionary<string, int> { ["WITHDRAWAL_REQUEST"] = 1 });
    }

    [Fact]
    public async Task Migration_participant_exports_and_restricts_the_archive()
    {
        var party = PartyId.New();
        var batch = Guid.CreateVersion7();
        await SeedAsync<MigrationDbContext>((db, clock) =>
        {
            db.Xrefs.Add(new XrefRow
            {
                XrefId = Guid.CreateVersion7(), LegalEntityId = new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId)), Source = "LEGACY", ObjectType = "PARTY",
                LegacyKey = "L-" + Guid.NewGuid(), CoreId = party.Value, BatchId = batch, CreatedAt = clock.Now,
            });
            db.Items.Add(new BatchItemRow
            {
                ItemId = Guid.CreateVersion7(), BatchId = batch, Seq = 1, ModuleName = "PTY", ObjectType = "PARTY", LegacyId = "L-1", Payload = "{}", Outcome = "MIGRATED", CoreId = party.Value,
            });
        });

        await AssertDsarAsync("MIG", SubjectRef.ForParty(party), new Dictionary<string, int> { ["MIGRATION_XREF"] = 1, ["MIGRATION_ARCHIVE"] = 1 });
    }
}
