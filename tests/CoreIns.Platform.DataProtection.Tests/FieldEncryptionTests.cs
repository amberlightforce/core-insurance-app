using System.Security.Cryptography;
using CoreIns.Platform.DataProtection.Keys;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CoreIns.Platform.DataProtection.Tests;

/// <summary>Field-level envelope encryption (D-ARC-14, D-ARC-23, REQ-PTY-060, NFR-PTY-010).</summary>
public sealed class FieldEncryptionTests
{
    private const string Field = "pty.party_identifier.value";

    private static readonly LegalEntityId EntityA = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000a"));
    private static readonly LegalEntityId EntityB = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000b"));

    private readonly TestKeys _keys = new();

    [Fact]
    public async Task Ciphertext_round_trips_and_holds_no_plaintext()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", cancellationToken: ct);

        System.Text.Encoding.UTF8.GetString(envelope).ShouldNotContain("123456783");
        envelope.Length.ShouldBe(FieldEncryptor.Overhead + 9);
        (await _keys.Encryptor.DecryptAsync(envelope, Field, cancellationToken: ct)).ShouldBe("123456783");

        var header = FieldEncryptor.ReadHeader(envelope);
        header.LegalEntity.ShouldBe(EntityA);
        header.KeyVersion.ShouldBe(1);
    }

    [Fact]
    public async Task Encryption_is_randomised()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", cancellationToken: ct);
        var second = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", cancellationToken: ct);
        first.ShouldNotBe(second);
    }

    /// <summary>Property: any string round-trips, with and without a row key (encryption round trip).</summary>
    [Property(MaxTest = 200)]
    public Property Any_string_round_trips() =>
        Prop.ForAll(ArbMap.Default.ArbFor<NonNull<string>>(), Gen.Elements<string?>(null, "row-1", "0192d4a1-0000-7000-8000-000000000001").ToArbitrary(), (value, row) =>
        {
            var envelope = _keys.Encryptor.Encrypt(EntityA, Field, value.Get, row);
            return _keys.Encryptor.Decrypt(envelope, Field, row) == value.Get;
        });

    /// <summary>Property: flipping any bit of an envelope is detected (tamper detection).</summary>
    [Property(MaxTest = 300)]
    public Property Any_bit_flip_is_detected() =>
        Prop.ForAll(ArbMap.Default.ArbFor<NonEmptyString>(), Gen.Choose(0, int.MaxValue).ToArbitrary(), Gen.Choose(0, 7).ToArbitrary(), (value, position, bit) =>
        {
            var envelope = _keys.Encryptor.Encrypt(EntityA, Field, value.Get, null);
            var index = position % envelope.Length;
            envelope[index] ^= (byte)(1 << bit);
            try
            {
                _keys.Encryptor.Decrypt(envelope, Field, null);
                return false.Label($"flip at {index} not detected");
            }
            catch (CryptographicException)
            {
                // FieldDecryptionException (format or tag), or an unknown key version / legal entity in the header.
                return true.ToProperty();
            }
        });

    [Fact]
    public async Task Ciphertext_is_bound_to_its_field_and_legal_entity()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "GR16 0110 1250 0000 0001 2300 695", cancellationToken: ct);

        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(envelope, "bil.payee.iban", cancellationToken: ct));

        // Re-labelling the envelope as another legal entity's fails: B's key is different and the header is authenticated.
        await _keys.Encryptor.EncryptAsync(EntityB, Field, "x", cancellationToken: ct);
        var relabelled = (byte[])envelope.Clone();
        EntityB.Value.TryWriteBytes(relabelled.AsSpan(1, 16), bigEndian: true, out _);
        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(relabelled, Field, cancellationToken: ct));
    }

    /// <summary>Review F-1e m5: with a row key, a ciphertext copied to another row of the same column is detected.</summary>
    [Fact]
    public async Task Row_key_binds_the_ciphertext_to_its_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "090000045", "party-1", ct);

        (await _keys.Encryptor.DecryptAsync(envelope, Field, "party-1", ct)).ShouldBe("090000045");
        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(envelope, Field, "party-2", ct));
        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(envelope, Field, cancellationToken: ct));
    }

    /// <summary>Review F-1e m6: a row of another legal entity is never decrypted under the current one.</summary>
    [Fact]
    public async Task Decrypt_for_legal_entity_refuses_a_row_of_another_legal_entity()
    {
        var envelope = await _keys.Encryptor.EncryptAsync(EntityB, Field, "123456783", cancellationToken: TestContext.Current.CancellationToken);

        _keys.Encryptor.DecryptForLegalEntity(envelope, Field, EntityB, null).ShouldBe("123456783");
        Should.Throw<FieldDecryptionException>(() => _keys.Encryptor.DecryptForLegalEntity(envelope, Field, EntityA, null))
            .Message.ShouldContain("another legal entity");
    }

    [Fact]
    public async Task Key_rotation_keeps_old_data_readable_and_re_encrypts_to_the_new_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-07T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var keys = new TestKeys(time);
        var old = await keys.Encryptor.EncryptAsync(EntityA, Field, "090000045", cancellationToken: ct);

        (await keys.Ring.RotateAsync(EntityA, KeyPurpose.FieldEncryption, ct)).ShouldBe(2);

        (await keys.Encryptor.DecryptAsync(old, Field, cancellationToken: ct)).ShouldBe("090000045");
        (await keys.Encryptor.NeedsReEncryptionAsync(old, ct)).ShouldBeTrue();
        FieldEncryptor.ReadHeader(await keys.Encryptor.EncryptAsync(EntityA, Field, "090000045", cancellationToken: ct)).KeyVersion.ShouldBe(2);

        var reEncrypted = await keys.Encryptor.ReEncryptAsync(old, Field, cancellationToken: ct);
        FieldEncryptor.ReadHeader(reEncrypted).KeyVersion.ShouldBe(2);

        time.Advance(keys.Ring.Options.RefreshInterval + keys.Ring.Options.RetirementMargin + TimeSpan.FromSeconds(1));
        await keys.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct);
        await Should.ThrowAsync<CryptographicException>(async () => await keys.Encryptor.DecryptAsync(old, Field, cancellationToken: ct));
        (await keys.Encryptor.DecryptAsync(reEncrypted, Field, cancellationToken: ct)).ShouldBe("090000045");
    }

    /// <summary>
    /// Review F-1e M3 (D-ARC-23): two replicas share the store; replica B's view is cached. After A rotates, B may still
    /// write with v1 until its view expires, B's searches see A's v2 at once, and retirement of v1 is refused until the
    /// demotion grace has passed and the re-scan finds no v1 rows.
    /// </summary>
    [Fact]
    public async Task Two_replica_rotation_is_safe()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-07T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var replicaA = new TestKeys(time);
        var replicaB = new TestKeys(time, replicaA.Store, replicaA.Provider);

        // Both replicas warm their cached views at v1.
        await replicaA.Ring.WarmUpAsync(EntityA, ct);
        await replicaB.Ring.WarmUpAsync(EntityA, ct);

        // A rotates both purposes.
        await replicaA.Ring.RotateAsync(EntityA, KeyPurpose.FieldEncryption, ct);
        await replicaA.Ring.RotateAsync(EntityA, KeyPurpose.BlindIndex, ct);
        var demotedAt = time.GetUtcNow();

        // B is stale: it still writes with v1 (allowed, v1 stays readable) ...
        var staleWrite = await replicaB.Encryptor.EncryptAsync(EntityA, Field, "123456783", cancellationToken: ct);
        FieldEncryptor.ReadHeader(staleWrite).KeyVersion.ShouldBe(1);
        var staleIndex = replicaB.Indexer.Compute(EntityA, "pty.identifier.AFM", "123456783");
        BlindIndexer.VersionOf(staleIndex).ShouldBe(1);

        // ... but B's search candidates come from the store and include A's newer v2 (and still v1).
        var aIndex = await replicaA.Indexer.ComputeAsync(EntityA, "pty.identifier.AFM", "123456783", cancellationToken: ct);
        BlindIndexer.VersionOf(aIndex).ShouldBe(2);
        var candidates = await replicaB.Indexer.SearchCandidatesAsync(EntityA, "pty.identifier.AFM", "123 456 783", cancellationToken: ct);
        candidates.ShouldBe([aIndex, staleIndex]);

        // A can read what stale B wrote; B can read what A writes (unknown version → store re-read).
        (await replicaA.Encryptor.DecryptAsync(staleWrite, Field, cancellationToken: ct)).ShouldBe("123456783");
        var aWrite = await replicaA.Encryptor.EncryptAsync(EntityA, Field, "090000045", cancellationToken: ct);
        (await replicaB.Encryptor.DecryptAsync(aWrite, Field, cancellationToken: ct)).ShouldBe("090000045");

        // Retirement right after demotion is refused: stale replicas may still write with v1.
        var scan = new FixedScan(0);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await replicaA.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, scan, ct));
        (await replicaA.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single(key => key.Version == 1).DemotedAt.ShouldBe(demotedAt);

        // After the refresh interval B writes with v2.
        time.Advance(replicaB.Ring.Options.RefreshInterval + TimeSpan.FromSeconds(1));
        FieldEncryptor.ReadHeader(await replicaB.Encryptor.EncryptAsync(EntityA, Field, "x", cancellationToken: ct)).KeyVersion.ShouldBe(2);

        // After the grace, the re-scan still finds B's stale row: refused until it is re-encrypted.
        time.Advance(replicaA.Ring.Options.RetirementMargin);
        scan.Rows = 1;
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await replicaA.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, scan, ct));

        scan.Rows = 0;
        await replicaA.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, scan, ct);
        (await replicaA.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single(key => key.Version == 1).Status.ShouldBe(DataKeyStatus.Retired);
    }

    [Fact]
    public async Task Active_key_cannot_be_retired()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keys.Encryptor.EncryptAsync(EntityA, Field, "x", cancellationToken: ct);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _keys.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, new FixedScan(0), ct));
    }

    [Fact]
    public async Task KEK_rotation_rewraps_data_keys_without_touching_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", cancellationToken: ct);
        var before = (await _keys.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single();

        _keys.Provider.CurrentVersion = 2;
        (await _keys.Ring.RewrapAsync(EntityA, ct)).ShouldBe(1); // only the field-encryption key exists

        var after = (await _keys.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single();
        after.KeyEncryptionKeyId.ShouldEndWith(":v2");
        after.KeyEncryptionKeyId.ShouldNotBe(before.KeyEncryptionKeyId);

        // A fresh replica (empty cache) reads the old envelope through the re-wrapped key.
        var replica = new FieldEncryptor(new KeyRing(_keys.Provider, _keys.Store, TimeProvider.System));
        (await replica.DecryptAsync(envelope, Field, cancellationToken: ct)).ShouldBe("123456783");
    }

    /// <summary>Review F-1e M4: a re-wrap that does not change the KEK version fails loudly.</summary>
    [Fact]
    public async Task Rewrap_without_a_KEK_rotation_fails_loudly()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keys.Encryptor.EncryptAsync(EntityA, Field, "x", cancellationToken: ct);
        (await Should.ThrowAsync<InvalidOperationException>(async () => await _keys.Ring.RewrapAsync(EntityA, ct)))
            .Message.ShouldContain("unchanged");
    }

    [Fact]
    public async Task Only_wrapped_keys_are_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keys.Encryptor.EncryptAsync(EntityA, Field, "x", cancellationToken: ct);
        var stored = (await _keys.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single();
        var key = await _keys.Ring.GetActiveAsync(EntityA, KeyPurpose.FieldEncryption, ct);

        stored.WrappedKey.ToArray().ShouldNotBe(key.Material);
        stored.Status.ShouldBe(DataKeyStatus.Active);
    }

    [Fact]
    public async Task Wrapped_keys_are_bound_to_legal_entity_purpose_and_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = new DataKeyContext(EntityA, KeyPurpose.FieldEncryption, 1);
        var wrapped = await _keys.Provider.WrapKeyAsync(context, new byte[32], ct);

        (await _keys.Provider.UnwrapKeyAsync(context, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct)).Length.ShouldBe(32);
        await Should.ThrowAsync<CryptographicException>(async () =>
            await _keys.Provider.UnwrapKeyAsync(context with { LegalEntity = EntityB }, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));
        await Should.ThrowAsync<CryptographicException>(async () =>
            await _keys.Provider.UnwrapKeyAsync(context with { Purpose = KeyPurpose.BlindIndex }, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));
        await Should.ThrowAsync<CryptographicException>(async () =>
            await _keys.Provider.UnwrapKeyAsync(context with { Version = 2 }, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));
    }

    [Fact]
    public async Task Concurrent_first_use_creates_a_single_key_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 16).Select(_ => _keys.Encryptor.EncryptAsync(EntityB, Field, "x", cancellationToken: ct).AsTask());
        await Task.WhenAll(tasks);
        (await _keys.Store.ListAsync(EntityB, KeyPurpose.FieldEncryption, ct)).Count.ShouldBe(1);
    }
}
