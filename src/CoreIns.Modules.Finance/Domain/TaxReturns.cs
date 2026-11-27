using System.Globalization;
using System.Text;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>
/// The IPT return (BO-FIN-09, REQ-FIN-005, -178..-188, -301): codes, the reconciliation rule, the content hash a checker approves and the
/// submission file. Amounts are brought to 2 places with the shared rounding policy (<see cref="Reconciliation.Money"/>), never with a default
/// <c>decimal.Round</c>.
/// </summary>
internal static class TaxReturns
{
    public const string Draft = "DRAFT";
    public const string Reviewed = "REVIEWED";
    public const string Filed = "FILED";
    public const string Paid = "PAID";

    public const string DefaultTaxCode = "TAX-IPT";

    /// <summary>The ledger account of the IPT payable; GL-2411 (written, not yet due) is not a liability until IPT_DUE moves it here.</summary>
    // TODO(hardening): resolve the IPT payable account(s) per tax code from FIN posting configuration (the derivation of the GL key) instead of this constant.
    public const string PayableAccount = "GL-2410";

    /// <summary>The source module of the journals that come from BIL billing entries (the BIL tax lines).</summary>
    public const string BilSourceModule = "BIL";

    public const string Matched = "MATCHED";
    public const string Explained = "EXPLAINED";
    public const string Unexplained = "UNEXPLAINED";

    /// <summary>The IPT liability point key of the country pack: the legal status of the IPT values is read from it.</summary>
    public const string PackKey = "tax.ipt.liability_point";

    public const string ContentType = "text/plain; charset=utf-8";

    public static string Text(decimal value) => Reconciliation.Text(value);

    public static decimal Money(decimal value) => Reconciliation.Money(value);

    /// <summary>MATCHED when the ledger and the BIL tax lines agree; EXPLAINED when they differ and a reconciliation explanation is recorded; else UNEXPLAINED.</summary>
    public static string ReconStatus(decimal ledger, decimal bil, string? explanation) =>
        Money(ledger) == Money(bil) ? Matched : string.IsNullOrWhiteSpace(explanation) ? Unexplained : Explained;

    public static decimal Net(decimal ledger, decimal adjustments) => Money(ledger) + Money(adjustments);

    /// <summary>The SHA-256 over everything that is filed: a changed return can never be approved or filed on an earlier approval (NX-EC-09).</summary>
    public static Sha256Hash Hash(TaxReturnRow row, IEnumerable<TaxReturnAdjustmentRow> adjustments)
    {
        var text = new StringBuilder("FIN.TAX_RETURN/v1|")
            .Append(row.ReturnId.ToString("D")).Append('|').Append(row.PeriodCode).Append('|').Append(row.TaxCode).Append('|').Append(row.VersionNo.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(row.Currency.Trim()).Append('|').Append(Text(row.LedgerAmount)).Append('|').Append(Text(row.BilAmount)).Append('|').Append(row.EntryCount.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(row.LegalStatus).Append('|').Append(row.ReconExplanation);
        foreach (var adjustment in adjustments.OrderBy(a => a.AdjustmentId))
        {
            text.Append('|').Append(adjustment.AdjustmentId.ToString("D")).Append(':').Append(Text(adjustment.Amount)).Append(':').Append(adjustment.Reason);
        }

        text.Append("|net=").Append(Text(row.NetPayable));
        return Sha256Hash.ComputeUtf8(text.ToString());
    }

    public static string FileName(string legalEntityCode, TaxReturnRow row) =>
        $"IPT-{legalEntityCode}-{row.PeriodCode}-{row.TaxCode}-v{row.VersionNo.ToString(CultureInfo.InvariantCulture)}.txt";

    /// <summary>The submission file: a deterministic text of the frozen content (the real authority format is a country-pack concern).</summary>
    // TODO(hardening): the Greek IPT return layout (ΠΟΛ form / myDATA submission) is a country-pack file profile; this is a neutral text rendering.
    public static string FileContent(string legalEntityCode, TaxReturnRow row, IEnumerable<TaxReturnAdjustmentRow> adjustments)
    {
        var text = new StringBuilder();
        text.Append("RETURN;").Append(row.ReturnId.ToString("D")).Append(';').Append(row.TaxCode).Append(';').Append(row.PeriodCode).Append(";v").Append(row.VersionNo.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("ENTITY;").Append(legalEntityCode).Append(";CURRENCY;").Append(row.Currency.Trim()).Append('\n')
            .Append("LEDGER;").Append(Text(row.LedgerAmount)).Append('\n');
        foreach (var adjustment in adjustments.OrderBy(a => a.AdjustmentId))
        {
            text.Append("ADJUSTMENT;").Append(Text(adjustment.Amount)).Append(';').Append(adjustment.Reason.Replace(';', ',').Replace('\n', ' ')).Append('\n');
        }

        text.Append("NET;").Append(Text(row.NetPayable)).Append('\n')
            .Append("CONTENT-HASH;").Append(row.ContentHash).Append('\n');
        return text.ToString();
    }

    public static bool IsAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text) || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            return false;
        }

        // At most 2 decimals: the value must survive the shared rounding policy unchanged.
        return Money(amount) == amount && amount != 0m;
    }
}

/// <summary>
/// The maker-checker binding of a return to <c>plt.Approval</c> (in process): the approval type, the authority type (<c>FIN.TAX_RETURN</c>, limited by
/// the absolute net payable) and the subject. The preparer submits, a different person with authority for the amount approves.
/// </summary>
internal static class TaxReturnApproval
{
    public const string Type = "FIN.TAX_RETURN";

    public const string ReferralRole = "Staff.Finance";

    public static AuthorityTypeCode Authority { get; } = AuthorityTypeCode.Parse(Type);

    public static AuthorityTypeDefinition Definition { get; } = new(
        Authority, ModuleCode.FIN, new LocalizedText("Έγκριση δήλωσης φόρου", "Tax return approval"),
        [new AuthorityDimensionDefinition("amount", DimensionKind.Money)]);

    public static ObjectRef Subject(Guid id) => new(ModuleCode.FIN, "TaxReturn", id.ToString("D"));

    public static Money Total(TaxReturnRow row) => new(Math.Abs(row.NetPayable), Currency.FromCode(row.Currency.Trim()));
}
