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

        await Retirement.RetireAsync(keys.Ring, time, EntityA, KeyPurpose.FieldEncryption, 1, ct);
        await Should.ThrowAsync<CryptographicException>(async () => await keys.Encryptor.DecryptAsync(old, Field, cancellationToken: ct));
        (await keys.Encryptor.DecryptAsync(reEncrypted, Field, cancellationToken: ct)).ShouldBe("090000045");
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
