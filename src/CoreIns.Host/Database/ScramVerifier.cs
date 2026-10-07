using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CoreIns.Host.Database;

/// <summary>
/// PostgreSQL SCRAM-SHA-256 password verifiers (RFC 5802 / RFC 7677), computed client-side so a role password never
/// travels to the server or appears in a statement: <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>.
/// PostgreSQL stores such a string as-is when it is given as the password in <c>CREATE/ALTER ROLE</c>.
/// </summary>
internal static class ScramVerifier
{
    /// <summary>PostgreSQL's default <c>scram_iterations</c>.</summary>
    public const int DefaultIterations = 4096;

    private const int SaltLength = 16;
    private const string Prefix = "SCRAM-SHA-256$";

    /// <summary>Computes a verifier with a fresh random salt.</summary>
    public static string Create(string password, int iterations = DefaultIterations) =>
        Create(password, RandomNumberGenerator.GetBytes(SaltLength), iterations);

    /// <summary>Computes a verifier for a given salt (deterministic; used by tests).</summary>
    public static string Create(string password, byte[] salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 4096);
        EnsureSaslPrepIdentity(password);

        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);

        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    /// <summary>True when the value already is a SCRAM-SHA-256 verifier.</summary>
    public static bool IsVerifier(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// PostgreSQL normalises passwords with SASLprep before hashing. For printable ASCII that is the identity, so the
    /// verifier matches exactly; anything else is rejected instead of risking a verifier the server will not accept.
    /// </summary>
    private static void EnsureSaslPrepIdentity(string password)
    {
        if (password.Length == 0 || password.Any(c => c is < (char)0x20 or > (char)0x7E))
        {
            throw new InvalidOperationException("Database role passwords must be non-empty printable ASCII.");
        }
    }
}
