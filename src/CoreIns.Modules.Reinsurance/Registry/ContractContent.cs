using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>One layer of the section in plain decimals (EUR only, D-SL4-04).</summary>
internal sealed record ContentLayer(int LayerNo, decimal Attachment, decimal Limit, decimal Aad, decimal? Aal);

/// <summary>One signed line of the panel (PRD-08 RIParticipation).</summary>
internal sealed record ContentLine(Guid ReinsurerPartyId, Guid? BrokerPartyId, decimal SignedLinePct, bool Lead);

/// <summary>
/// The business content of a contract version: everything a checker approves and the hash covers (REQ-RI-057). Built from
/// the request (create, update) or from the stored rows (submit, approve, activate) so the hash is always computed from
/// what is actually stored (PITFALLS 3).
/// </summary>
internal sealed record ContractContent(
    string ContractType,
    int ContractYear,
    string Currency,
    BusinessDate ValidFrom,
    BusinessDate ValidTo,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> CoverageCodes,
    bool AlaeIncluded,
    bool StatutoryInterestIncluded,
    string RecoveriesInure,
    IReadOnlyList<ContentLayer> Layers,
    IReadOnlyList<ContentLine> Lines,
    decimal PlacedPct)
{
    /// <summary>Money columns are NUMERIC(19,4).</summary>
    public const int MoneyScale = 4;

    /// <summary>Percentage columns are NUMERIC(9,6).</summary>
    public const int PercentScale = 6;

    /// <summary>
    /// SHA-256 over the canonical text: one <c>key=value</c> line per fact, codes and rows in a fixed order, decimals in a
    /// fixed scale (so 100 and 100.000000 hash alike). The contract id and version number are part of the text, so a
    /// hash cannot be replayed on another contract.
    /// </summary>
    public Sha256Hash Hash(RiContractId contractId, int versionNo)
    {
        var text = new StringBuilder();
        void Line(string key, string value) => text.Append(key).Append('=').Append(value).Append('\n');
        Line("contract", contractId.Value.ToString("D"));
        Line("version", versionNo.ToString(CultureInfo.InvariantCulture));
        Line("type", ContractType);
        Line("year", ContractYear.ToString(CultureInfo.InvariantCulture));
        Line("currency", Currency);
        Line("validFrom", ValidFrom.ToString());
        Line("validTo", ValidTo.ToString());
        Line("placedPct", Fixed(PlacedPct, PercentScale));
        Line("products", JsonSerializer.Serialize(ProductCodes.Order(StringComparer.Ordinal).ToArray()));
        Line("coverages", JsonSerializer.Serialize(CoverageCodes.Order(StringComparer.Ordinal).ToArray()));
        Line("alae", AlaeIncluded ? "1" : "0");
        Line("interest", StatutoryInterestIncluded ? "1" : "0");
        Line("inure", RecoveriesInure);
        foreach (var layer in Layers.OrderBy(l => l.LayerNo))
        {
            Line(
                FormattableString.Invariant($"layer.{layer.LayerNo}"),
                string.Join('|', Fixed(layer.Attachment, MoneyScale), Fixed(layer.Limit, MoneyScale), Fixed(layer.Aad, MoneyScale), layer.Aal is { } aal ? Fixed(aal, MoneyScale) : "-"));
        }

        foreach (var line in Lines.OrderBy(l => l.ReinsurerPartyId))
        {
            Line(
                FormattableString.Invariant($"line.{line.ReinsurerPartyId:D}"),
                string.Join('|', line.BrokerPartyId?.ToString("D") ?? "-", Fixed(line.SignedLinePct, PercentScale), line.Lead ? "lead" : "follow"));
        }

        return Sha256Hash.ComputeUtf8(text.ToString());
    }

    /// <summary>A decimal in a fixed scale, invariant culture.</summary>
    public static string Fixed(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.ToZero).ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}

/// <summary>The business rules of a contract's content (REQ-RI-037, -038, -046, -047, D-SL4-04). Pure; no I/O.</summary>
internal static class ContractRules
{
    /// <summary>The only contract type of slice 4 (D-SL4-04).</summary>
    public const string XolPerRisk = "XOL_PER_RISK";

    /// <summary>The only currency of slice 4 (D-SL4-04).</summary>
    public const string Eur = "EUR";

    /// <summary>The only inuring rule of slice 4 (REQ-RI-117 default).</summary>
    public const string RealisedOnly = "REALISED_ONLY";

    /// <summary>
    /// Null when the content is acceptable. Shape and amount problems are <c>RI-ERR-VALIDATION</c> with field errors;
    /// a panel that does not add up to the placed share, has no or several leads, or repeats a reinsurer is
    /// <c>RI-ERR-SIGNED-LINES</c> (REQ-RI-046/047).
    /// </summary>
    public static DomainError? Validate(ContractContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<FieldError>();
        void Add(string field, string code) => errors.Add(new FieldError(field, code, "ri." + code.ToLowerInvariant()));

        if (content.ContractType != XolPerRisk)
        {
            Add("contractType", "CONTRACT_TYPE_NOT_SUPPORTED");
        }

        if (content.Currency != Eur)
        {
            Add("currency", "CURRENCY_NOT_SUPPORTED");
        }

        if (content.ValidTo <= content.ValidFrom)
        {
            Add("period", "PERIOD_END_REQUIRED");
        }

        if (content.ContractYear != content.ValidFrom.Year)
        {
            Add("contractYear", "CONTRACT_YEAR_MISMATCH");
        }

        CheckCodes(content.ProductCodes, "scope.productCodes", Add);
        CheckCodes(content.CoverageCodes, "scope.coverageCodes", Add);
        if (content.RecoveriesInure != RealisedOnly)
        {
            Add("clause.recoveriesInure", "INURE_NOT_SUPPORTED");
        }

        CheckPercent(content.PlacedPct, "placedPct", Add);
        CheckLayers(content.Layers, Add);
        if (errors.Count > 0)
        {
            return new DomainError(ErrorCode.For(ModuleCode.RI, "VALIDATION"), "The contract is not valid.") { FieldErrors = errors };
        }

        return CheckPanel(content);
    }

    private static DomainError? CheckPanel(ContractContent content)
    {
        var errors = new List<FieldError>();
        void Add(string field, string code) => errors.Add(new FieldError(field, code, "ri." + code.ToLowerInvariant()));
        if (content.Lines.Count == 0)
        {
            Add("participations", "PANEL_REQUIRED");
        }

        foreach (var (line, index) in content.Lines.Select((l, i) => (l, i)))
        {
            CheckPercent(line.SignedLinePct, FormattableString.Invariant($"participations[{index}].signedLinePct"), Add);
            if (line.ReinsurerPartyId == Guid.Empty)
            {
                Add(FormattableString.Invariant($"participations[{index}].reinsurerPartyId"), "REINSURER_REQUIRED");
            }
        }

        if (content.Lines.GroupBy(l => l.ReinsurerPartyId).Any(g => g.Count() > 1))
        {
            Add("participations", "REINSURER_REPEATED");
        }

        var leads = content.Lines.Count(l => l.Lead);
        if (leads != 1)
        {
            Add("participations", leads == 0 ? "LEAD_REQUIRED" : "ONE_LEAD_ONLY");
        }

        if (content.Lines.Sum(l => l.SignedLinePct) != content.PlacedPct)
        {
            Add("participations", "SIGNED_LINES_DIFFER_FROM_PLACED");
        }

        return errors.Count == 0
            ? null
            : new DomainError(ErrorCode.For(ModuleCode.RI, "SIGNED-LINES"), "The signed lines must add up to the placed share, with exactly one lead (REQ-RI-046/047).") { FieldErrors = errors };
    }

    private static void CheckCodes(IReadOnlyList<string> codes, string field, Action<string, string> add)
    {
        if (codes.Count == 0)
        {
            add(field, "SCOPE_REQUIRED");
            return;
        }

        // Scope uses the shared Code shape (1–128 non-whitespace/control characters), also enforced by ProductCode.
        // Configuration owns the vocabulary: MOTOR-GR and OWN-DAMAGE are valid catalogue codes.
        if (codes.Any(c => !ProductCode.TryParse(c, out _)))
        {
            add(field, "CODE_FORMAT");
        }

        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
        {
            add(field, "CODE_REPEATED");
        }
    }

    private static void CheckPercent(decimal value, string field, Action<string, string> add)
    {
        if (value <= 0m || value > 100m)
        {
            add(field, "PERCENT_RANGE");
        }
        else if (decimal.Round(value, ContractContent.PercentScale, MidpointRounding.ToZero) != value)
        {
            add(field, "PERCENT_SCALE");
        }
    }

    private static void CheckAmount(decimal value, string field, bool mustBePositive, Action<string, string> add)
    {
        if (mustBePositive ? value <= 0m : value < 0m)
        {
            add(field, mustBePositive ? "AMOUNT_POSITIVE" : "AMOUNT_NEGATIVE");
        }
        else if (decimal.Round(value, ContractContent.MoneyScale, MidpointRounding.ToZero) != value)
        {
            add(field, "AMOUNT_SCALE");
        }
    }

    private static void CheckLayers(IReadOnlyList<ContentLayer> layers, Action<string, string> add)
    {
        if (layers.Count == 0)
        {
            add("layers", "LAYER_REQUIRED");
            return;
        }

        if (layers.Select(l => l.LayerNo).Distinct().Count() != layers.Count || layers.Any(l => l.LayerNo < 1))
        {
            add("layers", "LAYER_NO");
        }

        foreach (var (layer, index) in layers.Select((l, i) => (l, i)))
        {
            var path = FormattableString.Invariant($"layers[{index}]");
            CheckAmount(layer.Attachment, path + ".attachment", mustBePositive: false, add);
            CheckAmount(layer.Limit, path + ".limit", mustBePositive: true, add);
            CheckAmount(layer.Aad, path + ".aad", mustBePositive: false, add);
            if (layer.Aal is { } aal)
            {
                CheckAmount(aal, path + ".aal", mustBePositive: true, add);
            }
        }

        // This slice has no explicit non-contiguous declaration, so adjacent numbered layers must meet exactly.
        var ordered = layers.OrderBy(l => l.LayerNo).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Attachment != ordered[index - 1].Attachment + ordered[index - 1].Limit)
            {
                add("layers", "LAYERS_NOT_CONTIGUOUS");
                break;
            }
        }
    }
}
