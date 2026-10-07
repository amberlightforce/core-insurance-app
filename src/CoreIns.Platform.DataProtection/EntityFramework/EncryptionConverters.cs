using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CoreIns.Platform.DataProtection.EntityFramework;

/// <summary>
/// EF Core helpers for field-encrypted columns (REQ-PTY-060): the property stays a <see cref="string"/> in the model and
/// is stored as a <c>bytea</c> envelope (<see cref="FieldEncryptor"/>). Encryption is randomised, so the column cannot
/// be compared or indexed in SQL: equality search goes through a separate blind-index column
/// (<see cref="BlindIndexer"/>), populated by the owning module when it writes the value.
/// </summary>
public static class EncryptionConverters
{
    /// <summary>Converter for a string column encrypted under the current legal entity's Active key.</summary>
    /// <param name="encryptor">Field encryptor (singleton).</param>
    /// <param name="legalEntity">Supplies the legal entity of the unit of work (<see cref="AmbientLegalEntity"/>).</param>
    /// <param name="fieldContext">Stable field context bound into the ciphertext (for example <c>pty.party_identifier.value</c>).</param>
    public static ValueConverter<string, byte[]> ForString(FieldEncryptor encryptor, ICurrentLegalEntity legalEntity, string fieldContext)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        ArgumentNullException.ThrowIfNull(legalEntity);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldContext);

        return new ValueConverter<string, byte[]>(
            plaintext => encryptor.Encrypt(legalEntity.Current, fieldContext, plaintext),
            envelope => encryptor.Decrypt(envelope, fieldContext));
    }

    /// <summary>Configures <paramref name="property"/> as a field-encrypted column (see <see cref="ForString"/>).</summary>
    public static PropertyBuilder<string> IsFieldEncrypted(
        this PropertyBuilder<string> property, FieldEncryptor encryptor, ICurrentLegalEntity legalEntity, string fieldContext)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.HasConversion(ForString(encryptor, legalEntity, fieldContext));
    }
}
