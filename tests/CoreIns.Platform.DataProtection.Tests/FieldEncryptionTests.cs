using System.Security.Cryptography;
using CoreIns.Platform.DataProtection.Keys;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CoreIns.Platform.DataProtection.Tests;

/// <summary>Field-level envelope encryption (D-ARC-14, REQ-PTY-060, NFR-PTY-010).</summary>
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
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", ct);

        System.Text.Encoding.UTF8.GetString(envelope).ShouldNotContain("123456783");
        envelope.Length.ShouldBe(FieldEncryptor.Overhead + 9);
        (await _keys.Encryptor.DecryptAsync(envelope, Field, ct)).ShouldBe("123456783");

        var header = FieldEncryptor.ReadHeader(envelope);
        header.LegalEntity.ShouldBe(EntityA);
        header.KeyVersion.ShouldBe(1);
    }

    [Fact]
    public async Task Encryption_is_randomised()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", ct);
        var second = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", ct);
        first.ShouldNotBe(second);
    }

    /// <summary>Property: any string round-trips (encryption round trip).</summary>
    [Property(MaxTest = 200)]
    public Property Any_string_round_trips() =>
        Prop.ForAll(ArbMap.Default.ArbFor<NonNull<string>>(), value =>
        {
            var envelope = _keys.Encryptor.Encrypt(EntityA, Field, value.Get);
            return _keys.Encryptor.Decrypt(envelope, Field) == value.Get;
        });

    /// <summary>Property: flipping any bit of an envelope is detected (tamper detection).</summary>
    [Property(MaxTest = 300)]
    public Property Any_bit_flip_is_detected() =>
        Prop.ForAll(ArbMap.Default.ArbFor<NonEmptyString>(), Gen.Choose(0, int.MaxValue).ToArbitrary(), Gen.Choose(0, 7).ToArbitrary(), (value, position, bit) =>
        {
            var envelope = _keys.Encryptor.Encrypt(EntityA, Field, value.Get);
            var index = position % envelope.Length;
            envelope[index] ^= (byte)(1 << bit);
            try
            {
                _keys.Encryptor.Decrypt(envelope, Field);
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
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "GR16 0110 1250 0000 0001 2300 695", ct);

        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(envelope, "bil.payee.iban", ct));

        // Re-labelling the envelope as another legal entity's fails: B's key is different and the header is authenticated.
        await _keys.Encryptor.EncryptAsync(EntityB, Field, "x", ct);
        var relabelled = (byte[])envelope.Clone();
        EntityB.Value.TryWriteBytes(relabelled.AsSpan(1, 16), bigEndian: true, out _);
        await Should.ThrowAsync<FieldDecryptionException>(async () => await _keys.Encryptor.DecryptAsync(relabelled, Field, ct));
    }

    [Fact]
    public async Task Key_rotation_keeps_old_data_readable_and_re_encrypts_to_the_new_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var old = await _keys.Encryptor.EncryptAsync(EntityA, Field, "090000045", ct);

        var version = await _keys.Ring.RotateAsync(EntityA, KeyPurpose.FieldEncryption, ct);
        version.ShouldBe(2);

        // Old data is still readable (no downtime) and flagged for the re-encryption job.
        (await _keys.Encryptor.DecryptAsync(old, Field, ct)).ShouldBe("090000045");
        (await _keys.Encryptor.NeedsReEncryptionAsync(old, ct)).ShouldBeTrue();

        // New data uses version 2.
        var fresh = await _keys.Encryptor.EncryptAsync(EntityA, Field, "090000045", ct);
        FieldEncryptor.ReadHeader(fresh).KeyVersion.ShouldBe(2);

        var reEncrypted = await _keys.Encryptor.ReEncryptAsync(old, Field, ct);
        FieldEncryptor.ReadHeader(reEncrypted).KeyVersion.ShouldBe(2);
        (await _keys.Encryptor.NeedsReEncryptionAsync(reEncrypted, ct)).ShouldBeFalse();

        // After re-encryption the old version is retired and no longer opens anything.
        await _keys.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, ct);
        await Should.ThrowAsync<CryptographicException>(async () => await _keys.Encryptor.DecryptAsync(old, Field, ct));
        (await _keys.Encryptor.DecryptAsync(reEncrypted, Field, ct)).ShouldBe("090000045");
    }

    [Fact]
    public async Task Active_key_cannot_be_retired()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keys.Encryptor.EncryptAsync(EntityA, Field, "x", ct);
        await Should.ThrowAsync<InvalidOperationException>(async () => await _keys.Ring.RetireAsync(EntityA, KeyPurpose.FieldEncryption, 1, ct));
    }

    [Fact]
    public async Task KEK_rotation_rewraps_data_keys_without_touching_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var envelope = await _keys.Encryptor.EncryptAsync(EntityA, Field, "123456783", ct);
        var before = (await _keys.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single();

        _keys.Provider.CurrentVersion = 2;
        (await _keys.Ring.RewrapAsync(EntityA, ct)).ShouldBe(1);

        var after = (await _keys.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single();
        after.KeyEncryptionKeyId.ShouldEndWith(":v2");
        after.KeyEncryptionKeyId.ShouldNotBe(before.KeyEncryptionKeyId);

        // A fresh replica (empty cache) reads the old envelope through the re-wrapped key.
        var replica = new FieldEncryptor(new KeyRing(_keys.Provider, _keys.Store, TimeProvider.System));
        (await replica.DecryptAsync(envelope, Field, ct)).ShouldBe("123456783");
    }

    [Fact]
    public async Task Only_wrapped_keys_are_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keys.Encryptor.EncryptAsync(EntityA, Field, "x", ct);
        var stored = (await _keys.Store.ListAsync(EntityA, KeyPurpose.FieldEncryption, ct)).Single();
        var key = await _keys.Ring.GetActiveAsync(EntityA, KeyPurpose.FieldEncryption, ct);

        stored.WrappedKey.ToArray().ShouldNotBe(key.Material);
        stored.Status.ShouldBe(DataKeyStatus.Active);
    }

    [Fact]
    public async Task Unwrapping_with_another_legal_entity_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var wrapped = await _keys.Provider.WrapKeyAsync(EntityA, new byte[32], ct);
        await Should.ThrowAsync<CryptographicException>(async () =>
            await _keys.Provider.UnwrapKeyAsync(EntityB, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));
    }

    [Fact]
    public async Task Concurrent_first_use_creates_a_single_key_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 16).Select(_ => _keys.Encryptor.EncryptAsync(EntityB, Field, "x", ct).AsTask());
        await Task.WhenAll(tasks);
        (await _keys.Store.ListAsync(EntityB, KeyPurpose.FieldEncryption, ct)).Count.ShouldBe(1);
    }
}

/// <summary>A local key provider, an in-memory store and the services over them.</summary>
internal sealed class TestKeys
{
    public TestKeys()
    {
        Ring = new KeyRing(Provider, Store, TimeProvider.System);
        Encryptor = new FieldEncryptor(Ring);
        Indexer = new BlindIndexer(Ring);
    }

    public LocalKeyProvider Provider { get; } = LocalKeyProvider.CreateEphemeral();

    public InMemoryDataKeyStore Store { get; } = new();

    public KeyRing Ring { get; }

    public FieldEncryptor Encryptor { get; }

    public BlindIndexer Indexer { get; }
}
