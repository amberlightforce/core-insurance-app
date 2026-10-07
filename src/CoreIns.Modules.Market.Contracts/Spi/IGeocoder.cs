namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>
/// SPI 30 <c>Geocoder</c> (R-17, R-26, REQ-MKT-305; PRD-17 §9.4.31; spi.md §30): geocoding and versioned hazard keys.
/// Binding axis RISK_LOCATION. <c>geocode</c> S external through the integration hub, K on the address digest, 2 s →
/// <see cref="GeocodeStatus.NotAvailable"/>; <c>hazardKeys</c> S pure over pack hazard tables, 20 ms.
/// Core default: geocode NotAvailable; hazardKeys empty. Errors: VALIDATION (unparseable address), RULE_MISSING (no
/// scheme for the date), UNAVAILABLE, TIMEOUT. Types only in F-1e. Coordinates are decimal degrees (decimal, never
/// floating point, ADR §2 rule 2) and precise geocodes are P2 (PRD-01 Address).
/// </summary>
public interface IGeocoder
{
    /// <summary><c>geocode(address) → {lat, lon, precision, source, sourceVersion}</c>.</summary>
    Task<GeocodeResult> GeocodeAsync(PostalAddress address, CancellationToken cancellationToken = default);

    /// <summary><c>hazardKeys(location, schemes[]?, date) → [{schemeCode, schemeVersion, zone, score?, source}]</c>.</summary>
    ValueTask<IReadOnlyList<HazardKey>> HazardKeysAsync(
        GeoLocation location, IReadOnlyList<string>? schemes, DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary><c>schemes(jurisdiction, date) → [{schemeCode, versions[], status}]</c>.</summary>
    ValueTask<IReadOnlyList<HazardScheme>> SchemesAsync(
        string jurisdiction, DateOnly asOf, CancellationToken cancellationToken = default);
}

/// <summary>A point in decimal degrees (WGS 84).</summary>
public readonly record struct GeoLocation(decimal Latitude, decimal Longitude);

/// <summary>Geocode precision (PRD-17 §9.4.31).</summary>
public enum GeocodePrecision
{
    Rooftop,
    Street,
    Postcode,
    Municipality,
}

/// <summary>Whether a geocode was produced.</summary>
public enum GeocodeStatus
{
    Found,
    NotAvailable,
}

/// <summary>Result of <c>geocode</c>.</summary>
public sealed record GeocodeResult(
    GeocodeStatus Status, GeoLocation? Location, GeocodePrecision? Precision, string? Source, string? SourceVersion);

/// <summary>A versioned hazard key.</summary>
public sealed record HazardKey(string SchemeCode, string SchemeVersion, string Zone, decimal? Score, string Source);

/// <summary>Status of a hazard scheme version set.</summary>
public enum HazardSchemeStatus
{
    Active,
    Parallel,
    Retired,
}

/// <summary>A hazard scheme with its versions.</summary>
public sealed record HazardScheme(string SchemeCode, IReadOnlyList<string> Versions, HazardSchemeStatus Status);
