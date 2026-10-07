namespace CoreIns.Modules.Market.Contracts.ReferenceData;

/// <summary>
/// Postcode → locality reference data of a country (REQ-PTY-076: postcode–locality consistency and locality /
/// municipality autofill). Data contract only (D-ARC-22): the list itself is not in the PRDs, so it is loaded by
/// W2-PTY from an authoritative source; nothing is invented here. Address formatters use it when bound and behave as
/// without it (no autofill, no consistency check) when it is not.
/// </summary>
public interface IPostcodeDirectory
{
    /// <summary>Version of the loaded list (recorded with autofilled values).</summary>
    string Version { get; }

    /// <summary>Localities served by <paramref name="postcode"/> in <paramref name="country"/>; empty when unknown.</summary>
    /// <param name="country">ISO 3166-1 alpha-2 country.</param>
    /// <param name="postcode">Normalised postcode (no spaces).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<PostcodeLocality>> LookupAsync(string country, string postcode, CancellationToken cancellationToken = default);
}

/// <summary>One postcode → locality entry (native script; PRD-01 Address structured fields).</summary>
public sealed record PostcodeLocality(
    string Postcode,
    string Locality,
    string? Municipality = null,
    string? RegionalUnit = null,
    string? Region = null);
