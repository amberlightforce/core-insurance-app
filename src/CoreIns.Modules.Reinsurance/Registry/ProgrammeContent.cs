using System.Globalization;
using System.Text.RegularExpressions;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>One layer of a programme ladder in plain decimals (EUR only, D-SL4-04) and the contract that carries it.</summary>
internal sealed record ProgrammeLayerContent(int LayerNo, decimal Attachment, decimal Limit, RiContractId ContractId);

/// <summary>The business content of a programme version: what the approval will bind to (BO-RI-02).</summary>
internal sealed record ProgrammeContent(string Name, BusinessDate ValidFrom, BusinessDate ValidTo, IReadOnlyList<ProgrammeLayerContent> Layers)
{
    /// <summary>The retention of the programme: where layer 1 attaches.</summary>
    public decimal Retention => Layers.OrderBy(l => l.LayerNo).First().Attachment;

    /// <summary>The distinct contracts of the ladder, in the order of their lowest layer.</summary>
    public IReadOnlyList<RiContractId> ContractIds => [.. Layers.OrderBy(l => l.LayerNo).Select(l => l.ContractId).Distinct()];

    /// <summary>
    /// SHA-256 over the canonical text of the version: programme, version number, identity, name, period and the ladder with the contract each
    /// layer sits on, amounts in a fixed scale. The programme id and version number are part of the text, so a hash cannot be replayed (BO-RI-02).
    /// </summary>
    public Sha256Hash Hash(RiProgrammeId programmeId, int versionNo, string code, string lineCode, int year)
    {
        var text = new System.Text.StringBuilder();
        void Line(string key, string value) => text.Append(key).Append('=').Append(value).Append((char)10);
        Line("programme", programmeId.Value.ToString("D"));
        Line("version", versionNo.ToString(CultureInfo.InvariantCulture));
        Line("code", code);
        Line("line", lineCode);
        Line("year", year.ToString(CultureInfo.InvariantCulture));
        Line("name", Name);
        Line("validFrom", ValidFrom.ToString());
        Line("validTo", ValidTo.ToString());
        foreach (var layer in Layers.OrderBy(l => l.LayerNo))
        {
            Line(
                FormattableString.Invariant($"layer.{layer.LayerNo}"),
                string.Join('|', layer.Attachment.ToString("F4", CultureInfo.InvariantCulture), layer.Limit.ToString("F4", CultureInfo.InvariantCulture), layer.ContractId.Value.ToString("D")));
        }

        return Sha256Hash.ComputeUtf8(text.ToString());
    }

    /// <summary>The content as flat <c>field = text</c> facts, for the diff the commands return.</summary>
    public IReadOnlyDictionary<string, string> Facts()
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = Name,
            ["period"] = ValidFrom + " / " + ValidTo,
        };
        foreach (var layer in Layers.OrderBy(l => l.LayerNo))
        {
            facts[FormattableString.Invariant($"layers[{layer.LayerNo}]")] =
                string.Create(CultureInfo.InvariantCulture, $"{layer.Attachment:F2} xs {layer.Limit:F2} on {layer.ContractId.Value:D}");
        }

        return facts;
    }
}

/// <summary>The business rules of a programme (BO-RI-01). Pure; no I/O. The rules that need the stored contracts are in <see cref="ProgrammeSupport"/>.</summary>
internal static partial class ProgrammeRules
{
    /// <summary>Money columns are NUMERIC(19,4).</summary>
    public const int MoneyScale = 4;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();

    /// <summary>True when the code is acceptable (upper-case letters, digits, '_' and '-', 2 to 40 characters).</summary>
    public static bool IsCode(string? code) => code is not null && CodeShape().IsMatch(code);

    /// <summary>
    /// Null when the content is acceptable. Checks the shape and the ladder: layers numbered from 1 without a gap, positive limits, and
    /// each layer attaching exactly where the one below ends (contiguous, no overlap, so the limits and the retention are consistent).
    /// </summary>
    public static DomainError? Validate(string? code, string? lineCode, ProgrammeContent content, bool checkIdentity)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<FieldError>();
        void Add(string field, string errorCode) => errors.Add(new FieldError(field, errorCode, "ri." + errorCode.ToLowerInvariant()));

        if (checkIdentity)
        {
            if (!IsCode(code))
            {
                Add("code", "PROGRAMME_CODE_FORMAT");
            }

            if (string.IsNullOrWhiteSpace(lineCode) || lineCode.Length > 128 || lineCode.Any(char.IsWhiteSpace))
            {
                Add("lineCode", "LINE_CODE_FORMAT");
            }
        }

        if (string.IsNullOrWhiteSpace(content.Name) || content.Name.Length > 200)
        {
            Add("name", "PROGRAMME_NAME");
        }

        if (content.ValidTo <= content.ValidFrom)
        {
            Add("period", "PERIOD_END_REQUIRED");
        }

        if (content.Layers.Count == 0)
        {
            Add("layers", "LAYER_REQUIRED");
        }
        else
        {
            CheckLayers(content.Layers, Add);
        }

        return errors.Count == 0
            ? null
            : new DomainError(ErrorCode.For(ModuleCode.RI, "VALIDATION"), "The programme is not valid.") { FieldErrors = errors };
    }

    private static void CheckLayers(IReadOnlyList<ProgrammeLayerContent> layers, Action<string, string> add)
    {
        var ordered = layers.OrderBy(l => l.LayerNo).ToArray();
        if (ordered.Select(l => l.LayerNo).Distinct().Count() != ordered.Length || ordered[0].LayerNo != 1
            || ordered.Select((l, i) => l.LayerNo == i + 1).Contains(false))
        {
            add("layers", "LAYER_NO");
        }

        foreach (var (layer, index) in layers.Select((l, i) => (l, i)))
        {
            var path = FormattableString.Invariant($"layers[{index}]");
            CheckAmount(layer.Attachment, path + ".attachment", mustBePositive: false, add);
            CheckAmount(layer.Limit, path + ".limit", mustBePositive: true, add);
            if (layer.ContractId.Value == Guid.Empty)
            {
                add(path + ".contractId", "CONTRACT_REQUIRED");
            }
        }

        for (var index = 1; index < ordered.Length; index++)
        {
            var below = ordered[index - 1];
            var attachment = ordered[index].Attachment;
            if (attachment < below.Attachment + below.Limit)
            {
                add("layers", "LAYERS_OVERLAP");
                break;
            }

            if (attachment > below.Attachment + below.Limit)
            {
                add("layers", "LAYERS_NOT_CONTIGUOUS");
                break;
            }
        }
    }

    private static void CheckAmount(decimal value, string field, bool mustBePositive, Action<string, string> add)
    {
        if (mustBePositive ? value <= 0m : value < 0m)
        {
            add(field, mustBePositive ? "AMOUNT_POSITIVE" : "AMOUNT_NEGATIVE");
        }
        else if (decimal.Round(value, MoneyScale, MidpointRounding.ToZero) != value)
        {
            add(field, "AMOUNT_SCALE");
        }
    }

    /// <summary>The changes between two contents, as the diff the commands return (<c>before</c> is null for a new fact, <c>after</c> for a removed one).</summary>
    public static IReadOnlyList<(string Field, string? Before, string? After)> Diff(ProgrammeContent? before, ProgrammeContent after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var old = before?.Facts() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var now = after.Facts();
        var result = new List<(string, string?, string?)>();
        foreach (var key in old.Keys.Concat(now.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            old.TryGetValue(key, out var was);
            now.TryGetValue(key, out var becomes);
            if (!string.Equals(was, becomes, StringComparison.Ordinal))
            {
                result.Add((key, was, becomes));
            }
        }

        return result;
    }
}
