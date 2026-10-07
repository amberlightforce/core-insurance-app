using CoreIns.SharedKernel;

namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>Value type of a configuration key (REQ-MKT-035). Decimals travel as strings, never as JSON numbers.</summary>
public enum ConfigValueType
{
    /// <summary>Exact decimal, JSON string.</summary>
    ExactDecimal,

    /// <summary>Integer.</summary>
    WholeNumber,

    /// <summary>Boolean.</summary>
    Flag,

    /// <summary>Text or enumeration code.</summary>
    Text,

    /// <summary>A JSON object (for example a rounding rule).</summary>
    Json,
}

/// <summary>One effective-dated value a pack contributes to the country layer (REQ-MKT-126, D-REG-01).</summary>
/// <param name="Key">Configuration key, for example <c>tax.ipt.rate.general</c>.</param>
/// <param name="Type">Value type.</param>
/// <param name="Value">Canonical text of the value: invariant decimal text, a code, or compact JSON for <see cref="ConfigValueType.Json"/>.</param>
/// <param name="LegalStatus">Legal status.</param>
/// <param name="SourceRef">Source: PRD id and section, plus the law where the PRD cites it.</param>
/// <param name="MotorPath">The value sits on the motor path (REQ-MKT-343).</param>
/// <param name="ValidFrom">First day the value applies; null when the PRDs state no start (open).</param>
/// <param name="ValidTo">First day it no longer applies; null when open.</param>
/// <param name="Note">Reading, conflict or open-question note for the pack index.</param>
public sealed record PackConfigValue(
    string Key,
    ConfigValueType Type,
    string Value,
    LegalStatus LegalStatus,
    string SourceRef,
    bool MotorPath,
    BusinessDate? ValidFrom = null,
    BusinessDate? ValidTo = null,
    string? Note = null);

/// <summary>
/// A country pack's data-only contribution to the configuration (REQ-MKT-126 "pack data", REQ-MKT-145). Bound by the Host;
/// MKT reads it into the country layer (L3) of the jurisdiction. Values the PRDs do not state are simply absent (D-REG-01).
/// </summary>
public interface IPackConfigurationSource
{
    /// <summary>Pack id, for example <c>gr</c>.</summary>
    string PackId { get; }

    /// <summary>Semantic version of the pack data (REQ-MKT-129).</summary>
    string PackVersion { get; }

    /// <summary>ISO 3166-1 country whose L3 node the values populate.</summary>
    string Country { get; }

    /// <summary>The pack's values.</summary>
    IReadOnlyList<PackConfigValue> Values { get; }
}
