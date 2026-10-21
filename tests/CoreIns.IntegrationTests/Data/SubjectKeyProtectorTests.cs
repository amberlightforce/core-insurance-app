using System.Security.Cryptography;
using CoreIns.Modules.Data.Domain;
using CoreIns.Platform.DataProtection.Keys;

namespace CoreIns.IntegrationTests.Data;

/// <summary>NX-HD-13: subject keys are wrapped under the KEK; legacy raw 32-byte rows stay readable (no database needed).</summary>
public sealed class SubjectKeyProtectorTests
{
    private static SubjectKeyProtector Protector(LocalKeyProvider? provider = null) => new(provider ?? LocalKeyProvider.CreateEphemeral());

    [Fact]
    public async Task Wrap_then_unwrap_round_trips_and_the_stored_form_is_not_the_raw_key()
    {
        var protector = Protector();
        var key = SubjectCrypto.NewKey();
        var stored = await protector.WrapAsync("sub-1", key, TestContext.Current.CancellationToken);

        stored.Length.ShouldBeGreaterThan(32);
        SubjectKeyProtector.IsLegacy(stored).ShouldBeFalse();
        stored.AsSpan().IndexOf(key).ShouldBe(-1);
        (await protector.UnwrapAsync("sub-1", stored, TestContext.Current.CancellationToken)).ShouldBe(key);
    }

    [Fact]
    public async Task Legacy_raw_key_is_readable_as_is()
    {
        var protector = Protector();
        var key = SubjectCrypto.NewKey();
        SubjectKeyProtector.IsLegacy(key).ShouldBeTrue();
        (await protector.UnwrapAsync("sub-1", key, TestContext.Current.CancellationToken)).ShouldBe(key);
    }

    [Fact]
    public async Task A_wrapped_key_cannot_be_replayed_for_another_subject_or_tampered_with()
    {
        var protector = Protector();
        var stored = await protector.WrapAsync("sub-1", SubjectCrypto.NewKey(), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<CryptographicException>(async () => await protector.UnwrapAsync("sub-2", stored, TestContext.Current.CancellationToken));
        var tampered = (byte[])stored.Clone();
        tampered[^1] ^= 0xFF;
        await Should.ThrowAsync<CryptographicException>(async () => await protector.UnwrapAsync("sub-1", tampered, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<CryptographicException>(async () => await protector.UnwrapAsync("sub-1", [1, 2, 3, 4, 5, 6, 7], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_key_wrapped_under_a_rotated_kek_version_still_unwraps()
    {
        var master = RandomNumberGenerator.GetBytes(32);
        var provider = new LocalKeyProvider(master, 1);
        var key = SubjectCrypto.NewKey();
        var stored = await Protector(provider).WrapAsync("sub-1", key, TestContext.Current.CancellationToken);
        provider.CurrentVersion = 2;
        (await Protector(provider).UnwrapAsync("sub-1", stored, TestContext.Current.CancellationToken)).ShouldBe(key);
    }

    [Fact]
    public async Task A_different_master_key_fails_closed()
    {
        var stored = await Protector().WrapAsync("sub-1", SubjectCrypto.NewKey(), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<CryptographicException>(async () => await Protector().UnwrapAsync("sub-1", stored, TestContext.Current.CancellationToken));
    }
}
