using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Party number issued by PLT numbering for PTY; stable through merge (contract §3.2.3).</summary>
[JsonConverter(typeof(StringValueJsonConverter<PartyNumber>))]
public readonly record struct PartyNumber : IStringValue<PartyNumber>
{
    private PartyNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static PartyNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(PartyNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out PartyNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new PartyNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Account number issued by PLT numbering for PTY.</summary>
[JsonConverter(typeof(StringValueJsonConverter<AccountNumber>))]
public readonly record struct AccountNumber : IStringValue<AccountNumber>
{
    private AccountNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static AccountNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(AccountNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out AccountNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new AccountNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Producer code (PTY, effective-dated).</summary>
[JsonConverter(typeof(StringValueJsonConverter<ProducerCode>))]
public readonly record struct ProducerCode : IStringValue<ProducerCode>
{
    private ProducerCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ProducerCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ProducerCode), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ProducerCode result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ProducerCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Product code (PFC).</summary>
[JsonConverter(typeof(StringValueJsonConverter<ProductCode>))]
public readonly record struct ProductCode : IStringValue<ProductCode>
{
    private ProductCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ProductCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ProductCode), value, IdentifierRules.CodeDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ProductCode result)
    {
        if (IdentifierRules.IsCode(value))
        {
            result = new ProductCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Job number issued by PLT numbering for POL (one per job, all types).</summary>
[JsonConverter(typeof(StringValueJsonConverter<JobNumber>))]
public readonly record struct JobNumber : IStringValue<JobNumber>
{
    private JobNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static JobNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(JobNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out JobNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new JobNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Policy number issued by PLT numbering (NumberingScheme); unchanged across renewal terms.</summary>
[JsonConverter(typeof(StringValueJsonConverter<PolicyNumber>))]
public readonly record struct PolicyNumber : IStringValue<PolicyNumber>
{
    private PolicyNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static PolicyNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(PolicyNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out PolicyNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new PolicyNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Policy transaction number (monotonic per policy; reversal/reapply transactions get their own).</summary>
[JsonConverter(typeof(StringValueJsonConverter<TransactionNumber>))]
public readonly record struct TransactionNumber : IStringValue<TransactionNumber>
{
    private TransactionNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static TransactionNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(TransactionNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out TransactionNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new TransactionNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Billing account number issued by PLT numbering for BIL.</summary>
[JsonConverter(typeof(StringValueJsonConverter<BillingAccountNumber>))]
public readonly record struct BillingAccountNumber : IStringValue<BillingAccountNumber>
{
    private BillingAccountNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static BillingAccountNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(BillingAccountNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out BillingAccountNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new BillingAccountNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Invoice number (non-fiscal payment demand) issued by PLT numbering for BIL.</summary>
[JsonConverter(typeof(StringValueJsonConverter<InvoiceNumber>))]
public readonly record struct InvoiceNumber : IStringValue<InvoiceNumber>
{
    private InvoiceNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static InvoiceNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(InvoiceNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out InvoiceNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new InvoiceNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Receipt (incoming payment) number issued by PLT numbering for BIL.</summary>
[JsonConverter(typeof(StringValueJsonConverter<ReceiptNumber>))]
public readonly record struct ReceiptNumber : IStringValue<ReceiptNumber>
{
    private ReceiptNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ReceiptNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ReceiptNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ReceiptNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ReceiptNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Disbursement number issued by PLT numbering for BIL.</summary>
[JsonConverter(typeof(StringValueJsonConverter<DisbursementNumber>))]
public readonly record struct DisbursementNumber : IStringValue<DisbursementNumber>
{
    private DisbursementNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static DisbursementNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(DisbursementNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DisbursementNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new DisbursementNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Claim number issued by PLT numbering for CLM.</summary>
[JsonConverter(typeof(StringValueJsonConverter<ClaimNumber>))]
public readonly record struct ClaimNumber : IStringValue<ClaimNumber>
{
    private ClaimNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ClaimNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ClaimNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ClaimNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ClaimNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Exposure number (claim number + exposure number) for CLM.</summary>
[JsonConverter(typeof(StringValueJsonConverter<ExposureNumber>))]
public readonly record struct ExposureNumber : IStringValue<ExposureNumber>
{
    private ExposureNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ExposureNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ExposureNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ExposureNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ExposureNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Journal number (FIN).</summary>
[JsonConverter(typeof(StringValueJsonConverter<JournalNumber>))]
public readonly record struct JournalNumber : IStringValue<JournalNumber>
{
    private JournalNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static JournalNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(JournalNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out JournalNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new JournalNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>AADE MARK of a registered fiscal document (CMP, myDATA).</summary>
[JsonConverter(typeof(StringValueJsonConverter<FiscalMark>))]
public readonly record struct FiscalMark : IStringValue<FiscalMark>
{
    private FiscalMark(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static FiscalMark Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(FiscalMark), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out FiscalMark result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new FiscalMark(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>UW referral number.</summary>
[JsonConverter(typeof(StringValueJsonConverter<ReferralNumber>))]
public readonly record struct ReferralNumber : IStringValue<ReferralNumber>
{
    private ReferralNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ReferralNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ReferralNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ReferralNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ReferralNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Outbound document number (DOC).</summary>
[JsonConverter(typeof(StringValueJsonConverter<DocumentNumber>))]
public readonly record struct DocumentNumber : IStringValue<DocumentNumber>
{
    private DocumentNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static DocumentNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(DocumentNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DocumentNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new DocumentNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Complaint number (CMP).</summary>
[JsonConverter(typeof(StringValueJsonConverter<ComplaintNumber>))]
public readonly record struct ComplaintNumber : IStringValue<ComplaintNumber>
{
    private ComplaintNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ComplaintNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ComplaintNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ComplaintNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ComplaintNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>DSAR number (CMP).</summary>
[JsonConverter(typeof(StringValueJsonConverter<DsarNumber>))]
public readonly record struct DsarNumber : IStringValue<DsarNumber>
{
    private DsarNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static DsarNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(DsarNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DsarNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new DsarNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Activity number (WRK).</summary>
[JsonConverter(typeof(StringValueJsonConverter<ActivityNumber>))]
public readonly record struct ActivityNumber : IStringValue<ActivityNumber>
{
    private ActivityNumber(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ActivityNumber Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ActivityNumber), value, IdentifierRules.BusinessNumberDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ActivityNumber result)
    {
        if (IdentifierRules.IsBusinessNumber(value))
        {
            result = new ActivityNumber(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Legal entity code (one stamp per legal entity, e.g. GR-TEST); carried on every business row and event (legalEntity).</summary>
[JsonConverter(typeof(StringValueJsonConverter<LegalEntityId>))]
public readonly record struct LegalEntityId : IStringValue<LegalEntityId>
{
    private LegalEntityId(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static LegalEntityId Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(LegalEntityId), value, IdentifierRules.LegalEntityDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out LegalEntityId result)
    {
        if (IdentifierRules.IsLegalEntity(value))
        {
            result = new LegalEntityId(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>ISO 3166-1 alpha-2 jurisdiction whose rules apply (contract §3.2.1), e.g. GR.</summary>
[JsonConverter(typeof(StringValueJsonConverter<Jurisdiction>))]
public readonly record struct Jurisdiction : IStringValue<Jurisdiction>
{
    private Jurisdiction(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static Jurisdiction Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(Jurisdiction), value, IdentifierRules.JurisdictionDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out Jurisdiction result)
    {
        if (IdentifierRules.IsJurisdiction(value))
        {
            result = new Jurisdiction(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Statutory or business clock code <MOD>_<NAME> (R-28), e.g. BIL_NONPAY_NOTICE.</summary>
[JsonConverter(typeof(StringValueJsonConverter<ClockCode>))]
public readonly record struct ClockCode : IStringValue<ClockCode>
{
    private ClockCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ClockCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ClockCode), value, IdentifierRules.ClockCodeDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ClockCode result)
    {
        if (IdentifierRules.IsClockCode(value))
        {
            result = new ClockCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>AI feature id AI-<MOD>-<NN> (contract §3.6.1).</summary>
[JsonConverter(typeof(StringValueJsonConverter<AiFeatureId>))]
public readonly record struct AiFeatureId : IStringValue<AiFeatureId>
{
    private AiFeatureId(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static AiFeatureId Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(AiFeatureId), value, IdentifierRules.AiFeatureIdDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out AiFeatureId result)
    {
        if (IdentifierRules.IsAiFeatureId(value))
        {
            result = new AiFeatureId(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Retention class RC-... (contract §3.2.1).</summary>
[JsonConverter(typeof(StringValueJsonConverter<RetentionClassCode>))]
public readonly record struct RetentionClassCode : IStringValue<RetentionClassCode>
{
    private RetentionClassCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static RetentionClassCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(RetentionClassCode), value, IdentifierRules.RetentionClassDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out RetentionClassCode result)
    {
        if (IdentifierRules.IsRetentionClass(value))
        {
            result = new RetentionClassCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Obligation id OBL-<CODE> (contract §3.6.1).</summary>
[JsonConverter(typeof(StringValueJsonConverter<ObligationCode>))]
public readonly record struct ObligationCode : IStringValue<ObligationCode>
{
    private ObligationCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ObligationCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ObligationCode), value, IdentifierRules.ObligationDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ObligationCode result)
    {
        if (IdentifierRules.IsObligation(value))
        {
            result = new ObligationCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Stable operation name <mod>.<Resource>.<operation> (contract §3.5.1), e.g. pol.Job.bind.</summary>
[JsonConverter(typeof(StringValueJsonConverter<OperationName>))]
public readonly record struct OperationName : IStringValue<OperationName>
{
    private OperationName(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static OperationName Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(OperationName), value, IdentifierRules.OperationNameDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out OperationName result)
    {
        if (IdentifierRules.IsOperationName(value))
        {
            result = new OperationName(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Catalogued event name, PascalCase past tense (e.g. PolicyBound).</summary>
[JsonConverter(typeof(StringValueJsonConverter<EventTypeName>))]
public readonly record struct EventTypeName : IStringValue<EventTypeName>
{
    private EventTypeName(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static EventTypeName Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(EventTypeName), value, IdentifierRules.EventTypeNameDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out EventTypeName result)
    {
        if (IdentifierRules.IsEventTypeName(value))
        {
            result = new EventTypeName(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Configuration key in its owning module's namespace (REQ-MKT-033), e.g. cfg.retro.max_days.</summary>
[JsonConverter(typeof(StringValueJsonConverter<ConfigKey>))]
public readonly record struct ConfigKey : IStringValue<ConfigKey>
{
    private ConfigKey(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static ConfigKey Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(ConfigKey), value, IdentifierRules.ConfigKeyDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ConfigKey result)
    {
        if (IdentifierRules.IsConfigKey(value))
        {
            result = new ConfigKey(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Capability switch key cap.<area>.<name> (REQ-MKT-071).</summary>
[JsonConverter(typeof(StringValueJsonConverter<CapabilitySwitchKey>))]
public readonly record struct CapabilitySwitchKey : IStringValue<CapabilitySwitchKey>
{
    private CapabilitySwitchKey(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static CapabilitySwitchKey Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(CapabilitySwitchKey), value, IdentifierRules.CapabilitySwitchDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out CapabilitySwitchKey result)
    {
        if (IdentifierRules.IsCapabilitySwitch(value))
        {
            result = new CapabilitySwitchKey(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Country/region pack id (eu, group, gr, cy-stub, ...).</summary>
[JsonConverter(typeof(StringValueJsonConverter<PackCode>))]
public readonly record struct PackCode : IStringValue<PackCode>
{
    private PackCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static PackCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(PackCode), value, IdentifierRules.PackCodeDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out PackCode result)
    {
        if (IdentifierRules.IsPackCode(value))
        {
            result = new PackCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Authority type registered by its owning module (CD-05), e.g. MKT_CONFIG_APPROVAL.</summary>
[JsonConverter(typeof(StringValueJsonConverter<AuthorityTypeCode>))]
public readonly record struct AuthorityTypeCode : IStringValue<AuthorityTypeCode>
{
    private AuthorityTypeCode(string value) => Value = value;

    /// <summary>The value as text.</summary>
    public string Value { get; }

    /// <summary>Parses and validates; throws <see cref="FormatException"/> when invalid.</summary>
    public static AuthorityTypeCode Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException(IdentifierRules.Invalid(nameof(AuthorityTypeCode), value, IdentifierRules.AuthorityTypeDescription));

    /// <summary>Validates without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out AuthorityTypeCode result)
    {
        if (IdentifierRules.IsAuthorityType(value))
        {
            result = new AuthorityTypeCode(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The value.</summary>
    public override string ToString() => Value ?? string.Empty;
}
