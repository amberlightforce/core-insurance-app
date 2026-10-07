using CoreIns.Platform.DataProtection.EntityFramework;
using CoreIns.Platform.DataProtection.Keys;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform.DataProtection.Tests;

/// <summary>Blind indexes (REQ-PTY-060), the P0–P3 catalogue (REQ-PTY-168) and the EF Core converter.</summary>
public sealed class BlindIndexAndClassificationTests
{
    private const string AfmIndex = "pty.identifier.AFM";

    private static readonly LegalEntityId EntityA = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000a"));
    private static readonly LegalEntityId EntityB = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000b"));

    private readonly TestKeys _keys = new();

    [Fact]
    public async Task Display_variants_index_identically_and_plain_values_never_appear()
    {
        var ct = TestContext.Current.CancellationToken;
        var plain = await _keys.Indexer.ComputeAsync(EntityA, AfmIndex, "123456783", cancellationToken: ct);
        var grouped = await _keys.Indexer.ComputeAsync(EntityA, AfmIndex, "123 456 783", cancellationToken: ct);

        grouped.ShouldBe(plain);
        plain.ShouldStartWith("v1:");
        plain.ShouldNotContain("123456783");
        BlindIndexer.VersionOf(plain).ShouldBe(1);

        var iban = await _keys.Indexer.ComputeAsync(EntityA, "bil.payee.iban", "gr16 0110 1250 0000 0001 2300 695", BlindIndexNormalisers.Iban, ct);
        (await _keys.Indexer.ComputeAsync(EntityA, "bil.payee.iban", "GR1601101250000000012300695", BlindIndexNormalisers.Iban, ct)).ShouldBe(iban);
    }

    [Fact]
    public async Task Indexes_differ_per_index_name_and_per_legal_entity()
    {
        var ct = TestContext.Current.CancellationToken;
        var afm = await _keys.Indexer.ComputeAsync(EntityA, AfmIndex, "123456783", cancellationToken: ct);
        (await _keys.Indexer.ComputeAsync(EntityA, "pty.identifier.VAT", "123456783", cancellationToken: ct)).ShouldNotBe(afm);
        (await _keys.Indexer.ComputeAsync(EntityB, AfmIndex, "123456783", cancellationToken: ct)).ShouldNotBe(afm);
    }

    /// <summary>Property: the blind index is deterministic per key version (two replicas sharing the ring agree).</summary>
    [Property(MaxTest = 200)]
    public Property Blind_index_is_deterministic_per_key_version() =>
        Prop.ForAll(ArbMap.Default.ArbFor<NonNull<string>>(), value =>
        {
            var replica = new BlindIndexer(new KeyRing(_keys.Provider, _keys.Store, TimeProvider.System));
            var first = _keys.Indexer.Compute(EntityA, AfmIndex, value.Get);
            return first == _keys.Indexer.Compute(EntityA, AfmIndex, value.Get) && first == replica.Compute(EntityA, AfmIndex, value.Get);
        });

    [Fact]
    public async Task Rotation_keeps_search_working_through_candidates_of_every_readable_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = await _keys.Indexer.ComputeAsync(EntityA, AfmIndex, "090000045", cancellationToken: ct);

        await _keys.Ring.RotateAsync(EntityA, KeyPurpose.BlindIndex, ct);

        var rewritten = await _keys.Indexer.ComputeAsync(EntityA, AfmIndex, "090000045", cancellationToken: ct);
        BlindIndexer.VersionOf(rewritten).ShouldBe(2);
        rewritten.ShouldNotBe(stored);

        var candidates = await _keys.Indexer.SearchCandidatesAsync(EntityA, AfmIndex, "090 000 045", cancellationToken: ct);
        candidates.ShouldBe([rewritten, stored]); // Active first; rows not yet re-indexed are still found

        await _keys.Ring.RetireAsync(EntityA, KeyPurpose.BlindIndex, 1, ct);
        (await _keys.Indexer.SearchCandidatesAsync(EntityA, AfmIndex, "090000045", cancellationToken: ct)).ShouldBe([rewritten]);
    }

    [Fact]
    public void Classification_catalogue_reads_the_attributes()
    {
        DataClassCatalogue.Of(typeof(SampleIdentifier))["Value"].Classification.ShouldBe(DataClass.P2);
        DataClassCatalogue.Of(typeof(SampleIdentifier))["Value"].Encrypted.ShouldBeTrue();
        DataClassCatalogue.PropertiesAtOrAbove(typeof(SampleIdentifier), DataClass.P2).ShouldBe(["FraudScore", "Value"]);
        DataClassCatalogue.Unclassified(typeof(SampleIdentifier)).ShouldBe(["Unlabelled"]);
    }

    [Fact]
    public void EF_Core_converter_encrypts_for_the_current_legal_entity_and_decrypts()
    {
        var converter = EncryptionConverters.ForString(_keys.Encryptor, new AmbientLegalEntity(), "pty.party_identifier.value");

        byte[] envelope;
        using (AmbientLegalEntity.Enter(EntityA))
        {
            envelope = (byte[])converter.ConvertToProvider("123456783")!;
        }

        FieldEncryptor.ReadHeader(envelope).LegalEntity.ShouldBe(EntityA);
        converter.ConvertFromProvider(envelope).ShouldBe("123456783");
        Should.Throw<InvalidOperationException>(() => converter.ConvertToProvider("x"));
    }

    [Fact]
    public void Service_registration_needs_a_key_provider_and_store_from_the_host()
    {
        var services = new ServiceCollection()
            .AddSingleton<IKeyProvider>(_keys.Provider)
            .AddSingleton<IDataKeyStore>(_keys.Store)
            .AddFieldLevelProtection();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FieldEncryptor>().ShouldNotBeNull();
        provider.GetRequiredService<BlindIndexer>().ShouldNotBeNull();

        using var missing = new ServiceCollection().AddFieldLevelProtection().BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => missing.GetRequiredService<FieldEncryptor>());
    }

    private sealed class SampleIdentifier
    {
        [DataClass(DataClass.P0)]
        public string Scheme { get; init; } = string.Empty;

        [DataClass(DataClass.P2, Encrypted = true)]
        public string Value { get; init; } = string.Empty;

        [DataClass(DataClass.P3)]
        public decimal FraudScore { get; init; }

        public string Unlabelled { get; init; } = string.Empty;
    }
}
