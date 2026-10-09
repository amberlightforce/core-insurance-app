using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform.Authority;

/// <summary>
/// Authority types of slice 3 that several modules check but that no single module's slice owns (SL3-PLT-SUPPORT):
/// <c>BIL.REFUND</c> (REQ-BIL-188, REQ-BIL-326) and <c>POL.EFFECTIVE_DATE_OVERRIDE</c> (PRD-05 §12; the authority type code format is upper case, so the contract's BIL.Refund and POL.EffectiveDateOverride are written like this). Registering a type
/// grants nobody anything: the grants are configuration (<c>Platform:Authority:Grants</c>), illustrative where
/// commercial, and dropped in Production (D-SL2-03).
/// </summary>
public static class SupportAuthorityTypes
{
    /// <summary>Refund of a customer payment or premium.</summary>
    public static AuthorityTypeCode Refund { get; } = AuthorityTypeCode.Parse("BIL.REFUND");

    /// <summary>Override of the policy effective-date limits (back-dating beyond the configured limit).</summary>
    public static AuthorityTypeCode EffectiveDateOverride { get; } = AuthorityTypeCode.Parse("POL.EFFECTIVE_DATE_OVERRIDE");

    /// <summary>The refund amount (money, so it carries its currency).</summary>
    public const string AmountDimension = "amount";

    /// <summary>The refund currency as a code (ISO 4217), for per-currency limits.</summary>
    public const string CurrencyDimension = "currency";

    /// <summary>
    /// Whether the refund goes to a payee (or a payee bank account) other than the established one, as the code "true" or
    /// "false". A Code, not a Flag, so it travels in <c>plt.Approval</c> (which carries only Money and Code dimensions) and is
    /// re-checked at decide time; a grant limits it with an IN_SET limit on "false" (REQ-BIL-188, REQ-BIL-189).
    /// </summary>
    public const string PayeeChangedDimension = "payeeChanged";

    /// <summary>The refund reason code (REQ-BIL-188).</summary>
    public const string ReasonDimension = "reason";

    /// <summary>The code of the <see cref="PayeeChangedDimension"/> value.</summary>
    public static string PayeeChangedCode(bool changed) => changed ? "true" : "false";

    /// <summary>Product code.</summary>
    public const string ProductDimension = "product";

    /// <summary>Policy transaction type code.</summary>
    public const string TransactionTypeDimension = "transactionType";

    /// <summary>Days of back-dating beyond the policy's own limit.</summary>
    public const string DaysDimension = "days";

    /// <summary>The definitions.</summary>
    public static IReadOnlyList<AuthorityTypeDefinition> Definitions { get; } =
    [
        new AuthorityTypeDefinition(
            Refund, ModuleCode.BIL, new LocalizedText("Επιστροφή χρημάτων", "Refund"),
            [
                new AuthorityDimensionDefinition(AmountDimension, DimensionKind.Money),
                new AuthorityDimensionDefinition(CurrencyDimension, DimensionKind.Code),
                new AuthorityDimensionDefinition(PayeeChangedDimension, DimensionKind.Code),
                new AuthorityDimensionDefinition(ReasonDimension, DimensionKind.Code),
            ]),
        new AuthorityTypeDefinition(
            EffectiveDateOverride, ModuleCode.POL, new LocalizedText("Παράκαμψη ημερομηνίας έναρξης", "Effective-date override"),
            [
                new AuthorityDimensionDefinition(ProductDimension, DimensionKind.Code),
                new AuthorityDimensionDefinition(TransactionTypeDimension, DimensionKind.Code),
                new AuthorityDimensionDefinition(DaysDimension, DimensionKind.Number),
            ]),
    ];

    /// <summary>Registers the types.</summary>
    public static IServiceCollection AddSupportAuthorityTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var definition in Definitions)
        {
            services.AddAuthorityType(definition);
        }

        return services;
    }
}
