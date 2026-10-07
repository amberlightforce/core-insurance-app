using System.Globalization;
using System.Security.Cryptography;
using CoreIns.Platform.DataProtection.EntityFramework;
using CoreIns.Platform.DataProtection.Keys;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CoreIns.Platform.DataProtection.Tests;

/// <summary>
/// Review F-1e B1 (D-ARC-23): rotation and retirement across replicas on the <b>synchronous</b> write paths (EF Core
/// converter, synchronous blind index) with a manual clock, including a key-store outage.
/// </summary>
public sealed class ReplicaRotationTests
{
    private const string Field = "pty.party_identifier.value";
    private const string AfmIndex = "pty.identifier.AFM";

    private static readonly LegalEntityId Entity = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000a"));

    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-07T10:00:00Z", CultureInfo.InvariantCulture));

    /// <summary>The reviewer's repro: an idle replica must not seal with a demoted version that is then retired.</summary>
    [Fact]
    public async Task Idle_replica_never_writes_with_a_retired_version_on_the_synchronous_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, store) = TwoReplicas();
        await a.Ring.WarmUpAsync(Entity, ct);
        await b.Ring.WarmUpAsync(Entity, ct);

        await a.Ring.RotateAsync(Entity, KeyPurpose.FieldEncryption, ct);
        await a.Ring.RotateAsync(Entity, KeyPurpose.BlindIndex, ct);

        // B idle for 10 minutes; A begins retiring v1 (first scan clean).
        _time.Advance(TimeSpan.FromMinutes(10));
        await a.Ring.BeginRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct);

        // B's EF converter write and synchronous blind index: the 10-minute-old view is reloaded, so v2 is used.
        var envelope = Write(b, "123456783");
        FieldEncryptor.ReadHeader(envelope).KeyVersion.ShouldBe(2);
        BlindIndexer.VersionOf(b.Indexer.Compute(Entity, AfmIndex, "123456783")).ShouldBe(2);

        // Completing the retirement and reading B's row from A and from a fresh replica works.
        _time.Advance(Retirement.Grace(a.Ring));
        await a.Ring.CompleteRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct);
        (await a.Encryptor.DecryptAsync(envelope, Field, cancellationToken: ct)).ShouldBe("123456783");
        var fresh = new FieldEncryptor(new KeyRing(a.Provider, store, _time));
        (await fresh.DecryptAsync(envelope, Field, cancellationToken: ct)).ShouldBe("123456783");
    }

    /// <summary>
    /// A replica may write with the demoted version only within the write-staleness bound; retirement cannot begin
    /// before that bound (+ margin) has passed, and a write racing the first scan is caught by the second.
    /// </summary>
    [Fact]
    public async Task Writes_with_a_demoted_version_are_bounded_and_caught_by_the_two_step_retirement()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, _) = TwoReplicas();
        await a.Ring.WarmUpAsync(Entity, ct);
        await b.Ring.WarmUpAsync(Entity, ct);
        await a.Ring.RotateAsync(Entity, KeyPurpose.FieldEncryption, ct);

        // One minute later B (synchronous path, view younger than half the bound) still seals with v1: allowed, readable.
        _time.Advance(TimeSpan.FromMinutes(1));
        var staleRow = Write(b, "090000045");
        FieldEncryptor.ReadHeader(staleRow).KeyVersion.ShouldBe(1);
        (await a.Encryptor.DecryptAsync(staleRow, Field, cancellationToken: ct)).ShouldBe("090000045");

        // Retirement cannot begin while replicas may still pick v1 for writes.
        (await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.Ring.BeginRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct))).Message.ShouldContain("too recently");

        // Past the bound + margin: the first scan still sees B's stale row → refused until re-encrypted.
        _time.Advance(Retirement.Grace(a.Ring));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.Ring.BeginRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(1), ct));
        await a.Ring.BeginRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct);

        // A write that raced the first scan shows up in the second scan: completion refused, v1 stays readable.
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.Ring.CompleteRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct)); // too early
        _time.Advance(Retirement.Grace(a.Ring));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.Ring.CompleteRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(1), ct));
        (await a.Store.ListAsync(Entity, KeyPurpose.FieldEncryption, ct)).Single(key => key.Version == 1).Status.ShouldBe(DataKeyStatus.Retiring);
        (await a.Encryptor.DecryptAsync(staleRow, Field, cancellationToken: ct)).ShouldBe("090000045");

        // After re-encryption the second scan is clean.
        var reEncrypted = await a.Encryptor.ReEncryptAsync(staleRow, Field, cancellationToken: ct);
        await a.Ring.CompleteRetirementAsync(Entity, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct);
        (await a.Store.ListAsync(Entity, KeyPurpose.FieldEncryption, ct)).Single(key => key.Version == 1).Status.ShouldBe(DataKeyStatus.Retired);
        Read(b, reEncrypted).ShouldBe("090000045");

        // The Active key cannot be retired, and steps cannot be skipped.
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.Ring.BeginRetirementAsync(Entity, KeyPurpose.FieldEncryption, 2, new FixedScan(0), ct));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await a.Ring.CompleteRetirementAsync(Entity, KeyPurpose.FieldEncryption, 2, new FixedScan(0), ct));
    }

    [Fact]
    public async Task Search_candidates_cover_newer_and_retiring_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b, _) = TwoReplicas();
        await a.Ring.WarmUpAsync(Entity, ct);
        await b.Ring.WarmUpAsync(Entity, ct);
        var v1 = b.Indexer.Compute(Entity, AfmIndex, "123456783");

        await a.Ring.RotateAsync(Entity, KeyPurpose.BlindIndex, ct);
        var v2 = await a.Indexer.ComputeAsync(Entity, AfmIndex, "123456783", cancellationToken: ct);

        // B's candidate cache is short: once it expires, A's newer version is included.
        _time.Advance(b.Ring.Options.SearchCandidateCacheDuration + TimeSpan.FromSeconds(1));
        (await b.Indexer.SearchCandidatesAsync(Entity, AfmIndex, "123 456 783", cancellationToken: ct)).ShouldBe([v2, v1]);

        _time.Advance(Retirement.Grace(a.Ring));
        await a.Ring.BeginRetirementAsync(Entity, KeyPurpose.BlindIndex, 1, new FixedScan(0), ct);
        _time.Advance(b.Ring.Options.SearchCandidateCacheDuration + TimeSpan.FromSeconds(1));
        (await b.Indexer.SearchCandidatesAsync(Entity, AfmIndex, "123456783", cancellationToken: ct)).ShouldBe([v2, v1]); // Retiring still searched

        _time.Advance(Retirement.Grace(a.Ring));
        await a.Ring.CompleteRetirementAsync(Entity, KeyPurpose.BlindIndex, 1, new FixedScan(0), ct);
        _time.Advance(b.Ring.Options.SearchCandidateCacheDuration + TimeSpan.FromSeconds(1));
        (await b.Indexer.SearchCandidatesAsync(Entity, AfmIndex, "123456783", cancellationToken: ct)).ShouldBe([v2]);
    }

    /// <summary>Key-store outage: within the bound the cached key is used; beyond it writes fail closed, reads go on.</summary>
    [Fact]
    public async Task Store_outage_fails_writes_closed_beyond_the_bound_and_keeps_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FlakyStore(new InMemoryDataKeyStore());
        var replica = new TestKeys(_time, store);
        await replica.Ring.WarmUpAsync(Entity, ct);
        var row = Write(replica, "123456783");

        store.Down = true;

        // Within half the bound: no I/O at all, writes succeed from the cached view.
        _time.Advance(TimeSpan.FromMinutes(2));
        var readsBefore = store.Reads;
        FieldEncryptor.ReadHeader(Write(replica, "x")).KeyVersion.ShouldBe(1);
        store.Reads.ShouldBe(readsBefore);

        // Beyond the bound: the EF converter write and the synchronous blind index refuse (fail closed) ...
        _time.Advance(replica.Ring.Options.MaxStaleForWrite);
        Should.Throw<KeyRingStaleException>(() => Write(replica, "x"));
        Should.Throw<KeyRingStaleException>(() => replica.Indexer.Compute(Entity, AfmIndex, "x"));
        await Should.ThrowAsync<KeyRingStaleException>(async () => await replica.Encryptor.EncryptAsync(Entity, Field, "x", cancellationToken: ct));

        // ... while decryption of existing rows keeps working from the cached keys.
        Read(replica, row).ShouldBe("123456783");

        // When the store is back, writes resume with a fresh view.
        store.Down = false;
        FieldEncryptor.ReadHeader(Write(replica, "x")).KeyVersion.ShouldBe(1);
    }

    [Fact]
    public async Task A_hanging_store_makes_a_synchronous_write_fail_closed_after_the_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new HangingStore(new InMemoryDataKeyStore());
        var ring = new KeyRing(LocalKeyProvider.CreateEphemeral(), store, _time, new KeyRingOptions { SyncReloadTimeout = TimeSpan.FromMilliseconds(200) });
        await ring.WarmUpAsync(Entity, ct);

        store.Hang = true;
        _time.Advance(ring.Options.MaxStaleForWrite + TimeSpan.FromSeconds(1));
        Should.Throw<KeyRingStaleException>(() => new FieldEncryptor(ring).Encrypt(Entity, Field, "x", null));
    }

    private (TestKeys A, TestKeys B, IDataKeyStore Store) TwoReplicas()
    {
        var a = new TestKeys(_time);
        var b = new TestKeys(_time, a.Store, a.Provider);
        return (a, b, a.Store);
    }

    private static byte[] Write(TestKeys replica, string value)
    {
        var converter = Converter(replica);
        using var scope = AmbientLegalEntity.Enter(Entity);
        return (byte[])converter.ConvertToProvider(value)!;
    }

    private static string Read(TestKeys replica, byte[] envelope)
    {
        var converter = Converter(replica);
        using var scope = AmbientLegalEntity.Enter(Entity);
        return (string)converter.ConvertFromProvider(envelope)!;
    }

    private static ValueConverter<string, byte[]> Converter(TestKeys replica) =>
        EncryptionConverters.ForString(replica.Encryptor, new AmbientLegalEntity(), Field);

    /// <summary>A store whose reads can hang forever.</summary>
    private sealed class HangingStore(IDataKeyStore inner) : IDataKeyStore
    {
        public bool Hang { get; set; }

        public ValueTask<IReadOnlyList<WrappedDataKey>> ListAsync(LegalEntityId legalEntity, KeyPurpose purpose, CancellationToken cancellationToken = default) =>
            Hang ? new ValueTask<IReadOnlyList<WrappedDataKey>>(new TaskCompletionSource<IReadOnlyList<WrappedDataKey>>().Task) : inner.ListAsync(legalEntity, purpose, cancellationToken);

        public ValueTask AddAsync(WrappedDataKey key, CancellationToken cancellationToken = default) => inner.AddAsync(key, cancellationToken);

        public ValueTask UpdateAsync(WrappedDataKey key, CancellationToken cancellationToken = default) => inner.UpdateAsync(key, cancellationToken);
    }
}

/// <summary>Review F-1e N1/N2: the associated data and the blind-index message are injective encodings.</summary>
public sealed class EncodingAmbiguityTests
{
    private static readonly LegalEntityId Entity = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000a"));
    private readonly TestKeys _keys = new();

    [Fact]
    public async Task Field_context_and_row_key_cannot_be_re_split()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = await _keys.Encryptor.EncryptAsync(Entity, "pty.ab", "secret", "c", ct);
        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(envelope, "pty.a", "bc", ct));
        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(envelope, "pty.abc", null, ct));
    }

    [Fact]
    public async Task Control_characters_are_refused_in_the_field_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<ArgumentException>(async () => await _keys.Encryptor.EncryptAsync(Entity, "pty.a\u001Fb", "secret", "c", ct));
    }

    [Fact]
    public void Index_name_and_value_cannot_be_re_split()
    {
        static string Identity(string value) => value;
        _keys.Indexer.Compute(Entity, "pty.ab", "C", Identity).ShouldNotBe(_keys.Indexer.Compute(Entity, "pty.a", "BC", Identity));
        _keys.Indexer.Compute(Entity, "pty.a\u001Fb", "C", Identity).ShouldNotBe(_keys.Indexer.Compute(Entity, "pty.a", "B\u001FC", Identity));
    }

    [Fact]
    public async Task Key_Vault_kid_on_another_host_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var vault = new FakeKeyVault();
        var provider = new AzureKeyVaultKeyProvider(FakeKeyVault.VaultUri, vault, TimeProvider.System);
        var context = new DataKeyContext(Entity, KeyPurpose.FieldEncryption, 1);
        var wrapped = await provider.WrapKeyAsync(context, new byte[32], ct);

        var otherHost = wrapped.KeyEncryptionKeyId.Replace("fake-vault.vault.azure.net", "attacker.vault.azure.net", StringComparison.Ordinal);
        var plainHttp = wrapped.KeyEncryptionKeyId.Replace("https://", "http://", StringComparison.Ordinal);
        await Should.ThrowAsync<CryptographicException>(async () => await provider.UnwrapKeyAsync(context, otherHost, wrapped.WrappedKey, ct));
        await Should.ThrowAsync<CryptographicException>(async () => await provider.UnwrapKeyAsync(context, plainHttp, wrapped.WrappedKey, ct));
        (await provider.UnwrapKeyAsync(context, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct)).Length.ShouldBe(32);
    }
}
