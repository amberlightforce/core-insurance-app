using System.Globalization;
using System.Security.Cryptography;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.Platform.DataProtection.Tests;

/// <summary>Key Vault provider over a fake vault (review F-1e M4, m5) and the start-up warm-up (m6).</summary>
public sealed class KeyVaultProviderTests
{
    private const string Field = "pty.party_identifier.value";

    private static readonly LegalEntityId Entity = new(Guid.Parse("0192d4a1-0000-7000-8000-00000000000a"));

    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-07T10:00:00Z", CultureInfo.InvariantCulture));
    private readonly FakeKeyVault _vault = new();

    private static string KeyName => $"kek-{Entity}";

    [Fact]
    public async Task Each_wrap_records_the_versioned_kid_of_the_current_KEK_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new AzureKeyVaultKeyProvider(_vault, _time);
        var context = new DataKeyContext(Entity, KeyPurpose.FieldEncryption, 1);

        var first = await provider.WrapKeyAsync(context, new byte[32], ct);
        first.KeyEncryptionKeyId.ShouldEndWith($"/keys/{KeyName}/v0001");
        (await provider.UnwrapKeyAsync(context, first.KeyEncryptionKeyId, first.WrappedKey, ct)).ShouldBe(new byte[32]);

        // Within the cache window the resolved version is reused (one Key Vault "get" for two wraps).
        await provider.WrapKeyAsync(context, new byte[32], ct);
        _vault.GetCurrentCalls.ShouldBe(1);

        // After a rotation in Key Vault, the cache window expiring is enough for new wraps to use the new version.
        _vault.Rotate(KeyName);
        _time.Advance(provider.CurrentVersionCacheDuration + TimeSpan.FromSeconds(1));
        (await provider.WrapKeyAsync(context, new byte[32], ct)).KeyEncryptionKeyId.ShouldEndWith("/v0002");

        // The old version still unwraps what it wrapped.
        (await provider.UnwrapKeyAsync(context, first.KeyEncryptionKeyId, first.WrappedKey, ct)).Length.ShouldBe(32);
    }

    [Fact]
    public async Task Rewrap_after_a_Key_Vault_rotation_moves_every_data_key_to_the_new_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new AzureKeyVaultKeyProvider(_vault, _time);
        var store = new InMemoryDataKeyStore();
        var ring = new KeyRing(provider, store, _time);
        var encryptor = new FieldEncryptor(ring);
        var envelope = await encryptor.EncryptAsync(Entity, Field, "123456783", cancellationToken: ct);
        await new BlindIndexer(ring).ComputeAsync(Entity, "pty.identifier.AFM", "123456783", cancellationToken: ct);

        // Rotated in Key Vault a moment ago: the provider's cached current version is still v1, but RewrapAsync refreshes.
        _vault.Rotate(KeyName);
        (await ring.RewrapAsync(Entity, ct)).ShouldBe(2);

        foreach (var purpose in Enum.GetValues<KeyPurpose>())
        {
            (await store.ListAsync(Entity, purpose, ct)).Single().KeyEncryptionKeyId.ShouldEndWith("/v0002");
        }

        var freshReplica = new FieldEncryptor(new KeyRing(provider, store, _time));
        (await freshReplica.DecryptAsync(envelope, Field, cancellationToken: ct)).ShouldBe("123456783");

        // Without another rotation, a second re-wrap changes nothing and fails loudly.
        await Should.ThrowAsync<InvalidOperationException>(async () => await ring.RewrapAsync(Entity, ct));
    }

    [Fact]
    public async Task Wrapped_keys_are_bound_to_their_purpose_version_and_legal_entity()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new AzureKeyVaultKeyProvider(_vault, _time);
        var context = new DataKeyContext(Entity, KeyPurpose.FieldEncryption, 1);
        var wrapped = await provider.WrapKeyAsync(context, RandomNumberGenerator.GetBytes(32), ct);

        await Should.ThrowAsync<CryptographicException>(async () =>
            await provider.UnwrapKeyAsync(context with { Purpose = KeyPurpose.BlindIndex }, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));
        await Should.ThrowAsync<CryptographicException>(async () =>
            await provider.UnwrapKeyAsync(context with { Version = 2 }, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));

        // A kid of another legal entity's key, or a versionless kid, is refused before calling Key Vault.
        var other = new LegalEntityId(Guid.Parse("0192d4a1-0000-7000-8000-00000000000b"));
        await Should.ThrowAsync<CryptographicException>(async () =>
            await provider.UnwrapKeyAsync(context with { LegalEntity = other }, wrapped.KeyEncryptionKeyId, wrapped.WrappedKey, ct));
        await Should.ThrowAsync<CryptographicException>(async () =>
            await provider.UnwrapKeyAsync(context, $"https://fake-vault.vault.azure.net/keys/{KeyName}", wrapped.WrappedKey, ct));
    }

    [Fact]
    public async Task Warm_up_service_loads_every_catalogued_legal_entity_so_converters_do_no_key_vault_io()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new AzureKeyVaultKeyProvider(_vault, _time);
        var store = new InMemoryDataKeyStore();
        var ring = new KeyRing(provider, store, _time);

        await new KeyRingWarmUpService(ring, [new Catalogue([Entity])]).StartAsync(ct);
        var callsAfterWarmUp = _vault.WrappedWith.Count;
        callsAfterWarmUp.ShouldBe(2); // one data key per purpose created and wrapped

        // Synchronous request-thread paths now hit memory only, even once the cached view is stale.
        _time.Advance(ring.Options.RefreshInterval + TimeSpan.FromMinutes(1));
        var encryptor = new FieldEncryptor(ring);
        var envelope = encryptor.Encrypt(Entity, Field, "x", null);
        encryptor.Decrypt(envelope, Field, null).ShouldBe("x");
        new BlindIndexer(ring).Compute(Entity, "pty.identifier.AFM", "x").ShouldStartWith("v1:");
        _vault.WrappedWith.Count.ShouldBe(callsAfterWarmUp);
    }

    private sealed class Catalogue(IReadOnlyList<LegalEntityId> entities) : ILegalEntityCatalogue
    {
        public ValueTask<IReadOnlyList<LegalEntityId>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(entities);
    }
}
