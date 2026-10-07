namespace CoreIns.CountryPacks.GR;

/// <summary>Identity of the Greece pack (PRD-17 §10.4) and the rule-set ids it records with its outputs.</summary>
public static class GrPack
{
    /// <summary>Pack id (PRD-17 §7 Pack).</summary>
    public const string PackId = "gr";

    /// <summary>ISO 3166-1 country of the pack.</summary>
    public const string Country = "GR";

    /// <summary>BCP 47 language of the pack's language rules (REQ-MKT-340).</summary>
    public const string Language = "el";

    /// <summary>
    /// ELOT 743 Type 2 (aligned with ISO 843) rule set (REQ-MKT-091, GR-12 Settled on secondary sources; the paid
    /// standard's table detail is OI-MKT-18). Recorded as the transliterator version (REQ-PTY-062).
    /// </summary>
    public const string ElotRuleSetId = "GR-ELOT743-T2/1";

    /// <summary>Identifier validator version stored with each identifier (REQ-PTY-048).</summary>
    public const string IdValidatorVersion = "GR-ID/1";

    /// <summary>Plate normalisation rule set (REQ-MKT-339).</summary>
    public const string PlateRuleSetId = "GR-PLATE/1";

    /// <summary>Address rule set (REQ-MKT-092, REQ-PTY-076).</summary>
    public const string AddressRuleSetId = "GR-ADDR/1";

    /// <summary>Language rule version (REQ-MKT-340).</summary>
    public const string LanguageRuleVersion = "el/1";
}

/// <summary>Identifier scheme codes of the Greece pack's catalogue (REQ-PTY-047).</summary>
public static class GrSchemes
{
    /// <summary>Greek tax identification number, mod-11 check digit (REQ-PTY-049, REQ-MKT-260).</summary>
    public const string Afm = "AFM";

    /// <summary>EU VAT number with the VIES prefix EL (REQ-MKT-260, BR-PTY-003).</summary>
    public const string Vat = "VAT";

    /// <summary>GEMI business registry number (REQ-MKT-260; format not stated in the PRDs).</summary>
    public const string Gemi = "GEMI";

    public const string Passport = "PASSPORT";

    public const string NationalId = "NATIONAL_ID";

    public const string ResidencePermit = "RESIDENCE_PERMIT";

    public const string DrivingLicence = "DRIVING_LICENCE";
}
