using System.Text.Json.Serialization;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Module codes (contract §3.6.1). Every event has exactly one producing module; error codes start with it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModuleCode>))]
public enum ModuleCode
{
    /// <summary>PRD-01 Party and distribution.</summary>
    PTY,

    /// <summary>PRD-02 Product factory.</summary>
    PFC,

    /// <summary>PRD-03 Rating.</summary>
    RAT,

    /// <summary>PRD-04 Underwriting.</summary>
    UW,

    /// <summary>PRD-05 Policy administration.</summary>
    POL,

    /// <summary>PRD-06 Billing.</summary>
    BIL,

    /// <summary>PRD-07 Claims.</summary>
    CLM,

    /// <summary>PRD-08 Reinsurance.</summary>
    RI,

    /// <summary>PRD-09 Finance.</summary>
    FIN,

    /// <summary>PRD-10 Documents.</summary>
    DOC,

    /// <summary>PRD-11 Compliance.</summary>
    CMP,

    /// <summary>PRD-12 Channels.</summary>
    CHN,

    /// <summary>PRD-13 Work management.</summary>
    WRK,

    /// <summary>PRD-14 Platform.</summary>
    PLT,

    /// <summary>PRD-15 Data and analytics.</summary>
    DAT,

    /// <summary>PRD-16 Migration.</summary>
    MIG,

    /// <summary>PRD-17 Multi-market.</summary>
    MKT,
}

/// <summary>Helpers for <see cref="ModuleCode"/>.</summary>
public static class ModuleCodes
{
    /// <summary>Parses an upper-case module code (exact match).</summary>
    public static bool TryParse(string? text, out ModuleCode code)
    {
        code = default;
        return text is { Length: >= 2 and <= 3 }
               && text.All(char.IsAsciiLetterUpper)
               && Enum.TryParse(text, ignoreCase: false, out code)
               && Enum.IsDefined(code);
    }

    /// <summary>The lower-case form used in schemas, topics and operation names (e.g. <c>pol</c>).</summary>
    public static string ToLowerCode(this ModuleCode code) => code.ToString().ToLowerInvariant();
}
