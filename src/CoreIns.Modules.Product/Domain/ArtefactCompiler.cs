using System.Text;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Product.Domain;

/// <summary>A compiled product version: canonical JSON (RFC 8785) and its SHA-256 (PRD-02 §7.6, REQ-PFC-193/194).</summary>
internal sealed record CompiledArtefact(ProductArtefact Artefact, string CanonicalJson, Sha256Hash Hash)
{
    public int SizeBytes => Encoding.UTF8.GetByteCount(CanonicalJson);
}

/// <summary>
/// Lint and compile of a product source document (slice subset of PRD-02 §6/§7): structural checks the seed and the later
/// authoring workflow share, the Greece MTPL statutory check (REQ-PFC-088), derived values (<c>writtenPremium</c>,
/// REQ-PFC-250) and deterministic serialisation (same source, same bytes, same hash).
/// </summary>
internal static class ArtefactCompiler
{
    /// <summary>The key bindings every Greece MTPL coverage must carry (REQ-PFC-085, REQ-PFC-088).</summary>
    internal static readonly IReadOnlyDictionary<string, string> MtplFinals = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BI_PER_PERSON"] = "gr.mtpl.min_bi_per_person",
        ["PD_PER_ACCIDENT"] = "gr.mtpl.min_pd_per_accident",
    };

    public static Result<CompiledArtefact> Compile(ProductArtefact artefact)
    {
        ArgumentNullException.ThrowIfNull(artefact);
        var findings = Lint(artefact);
        if (findings.Count > 0)
        {
            return new DomainError(ErrorCode.For(ModuleCode.PFC, "INVALID-DEFINITION"), "The product definition fails lint.") { FieldErrors = findings };
        }

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(artefact, SharedKernelJson.Options);
        var canonical = CanonicalJson.Canonicalize(json);
        var text = Encoding.UTF8.GetString(canonical);
        return new CompiledArtefact(artefact, text, Sha256Hash.Compute(canonical));
    }

    /// <summary>Parses canonical JSON back into the artefact.</summary>
    public static ProductArtefact Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<ProductArtefact>(json, SharedKernelJson.Options)
        ?? throw new InvalidOperationException("The artefact is empty.");

    internal static List<FieldError> Lint(ProductArtefact a)
    {
        var errors = new List<FieldError>();

        void Add(string code, string path, string message) => errors.Add(new FieldError(path, code, "pfc.lint." + code.ToLowerInvariant(), message));

        void Unique(IEnumerable<string> codes, string path, string what)
        {
            foreach (var duplicate in codes.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                Add("PFC-LINT-DUPLICATE", path, $"{what} code '{duplicate.Key}' is used more than once.");
            }
        }

        if (a.SchemaVersion != 1)
        {
            Add("PFC-LINT-SCHEMA", "schemaVersion", "Only product schema version 1 is supported.");
        }

        if (a.Channels.Count == 0)
        {
            Add("PFC-LINT-CHANNELS", "channels", "A version needs at least one channel (REQ-PFC-039).");
        }

        Unique(a.Channels, "channels", "Channel");
        if (!a.Languages.Contains("el") || !a.Languages.Contains("en"))
        {
            Add("PFC-LINT-LANGUAGES", "languages", "Greek and English are mandatory (REQ-PFC-041).");
        }

        if (a.Terms.Allowed.Count == 0 || !a.Terms.Allowed.Contains(a.Terms.Default))
        {
            Add("PFC-LINT-TERMS", "terms", "The default term must be one of the allowed terms (REQ-PFC-034).");
        }

        if (a.Windows.Renewal.Start < a.Windows.NewBusiness.Start)
        {
            Add("PFC-LINT-WINDOWS", "windows.renewal", "The renewal window cannot open before the new-business window (REQ-PFC-166).");
        }

        // Elements: acyclic parents, unique codes, CHOICE fields list their choices.
        Unique(a.Elements.Select(e => e.Code), "elements", "Element");
        var elementCodes = a.Elements.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (element, i) in a.Elements.Select((e, i) => (e, i)))
        {
            if (element.Parent is { } parent && (!elementCodes.Contains(parent) || parent == element.Code))
            {
                Add("PFC-LINT-ELEMENT-PARENT", $"elements[{i}].parent", $"Parent '{parent}' is not another element of the version (REQ-PFC-049).");
            }

            if (element.Maximum < element.Minimum || element.Minimum < 0)
            {
                Add("PFC-LINT-CARDINALITY", $"elements[{i}]", "Cardinality maximum must not be below minimum.");
            }

            Unique(element.Fields.Select(f => f.Code), $"elements[{i}].fields", "Field");
            foreach (var (field, j) in element.Fields.Select((f, j) => (f, j)))
            {
                fields.Add($"{element.Code}.{field.Code}");
                if (field.DataType == FieldDataType.Choice && (field.Choices is null || field.Choices.Count == 0))
                {
                    Add("PFC-LINT-CHOICES", $"elements[{i}].fields[{j}].choices", "A CHOICE field lists its allowed codes.");
                }
            }
        }

        // Coverages and terms.
        Unique(a.Coverages.Select(c => c.Code), "coverages", "Coverage");
        var coverageCodes = a.Coverages.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var (coverage, i) in a.Coverages.Select((c, i) => (c, i)))
        {
            var path = $"coverages[{i}]";
            if (!elementCodes.Contains(coverage.CoveredElement))
            {
                Add("PFC-LINT-COVERED-ELEMENT", path + ".coveredElement", $"Element '{coverage.CoveredElement}' does not exist.");
            }

            if (coverage.Existence == CoverageExistence.Required && coverage.Removal != CoverageRemoval.Block)
            {
                Add("PFC-LINT-REQUIRED-REMOVABLE", path + ".removal", "A required coverage must block removal (REQ-PFC-071).");
            }

            foreach (var requires in coverage.Requires ?? [])
            {
                if (!coverageCodes.Contains(requires))
                {
                    Add("PFC-LINT-REQUIRES", path + ".requires", $"Coverage '{requires}' does not exist (REQ-PFC-072).");
                }
            }

            Unique(coverage.Terms.Select(t => t.Code), path + ".terms", "Term");
            foreach (var (term, j) in coverage.Terms.Select((t, j) => (t, j)))
            {
                var tp = $"{path}.terms[{j}]";
                if (term.Kind == TermKind.OptionList && (term.Options is null || term.Options.Count == 0))
                {
                    Add("PFC-LINT-OPTIONS", tp + ".options", "An option-list term needs at least one option (REQ-PFC-080).");
                }

                if (term.Kind == TermKind.Range && (term.Range is null || term.Range.Maximum < term.Range.Minimum))
                {
                    Add("PFC-LINT-RANGE", tp + ".range", "A range term needs a range with maximum not below minimum (REQ-PFC-079).");
                }

                if (term.ValueType == TermValueType.Money && term.Currency is null)
                {
                    Add("PFC-LINT-CURRENCY", tp + ".currency", "A money term names its currency (REQ-PFC-040).");
                }

                if (term.Options is { Count: > 0 } options)
                {
                    Unique(options.Select(o => o.Code), tp + ".options", "Option");
                }
            }

            // Greece MTPL: required, non-removable, limits bound to the final keys (REQ-PFC-088 / PFC-LINT-STATUTORY-001).
            if (a.Jurisdiction == "GR" && coverage.Code == "MTPL")
            {
                if (coverage.Existence != CoverageExistence.Required || coverage.Removal != CoverageRemoval.Block)
                {
                    Add("PFC-LINT-STATUTORY-001", path, "Greece: MTPL is required and non-removable on every vehicle (REQ-PFC-088).");
                }

                foreach (var (termCode, key) in MtplFinals)
                {
                    var bound = coverage.Terms.FirstOrDefault(t => t.Code == termCode)?.FinalBinding?.Key;
                    if (bound != key)
                    {
                        Add("PFC-LINT-STATUTORY-001", path + ".terms", $"Greece: MTPL term {termCode} must be bound to final key {key} (REQ-PFC-085).");
                    }
                }
            }
        }

        if (a.Jurisdiction == "GR" && a.Coverages.All(c => c.Code != "MTPL"))
        {
            Add("PFC-LINT-STATUTORY-001", "coverages", "Greece: a motor version carries the MTPL coverage (REQ-PFC-088).");
        }

        // Charge types: derived written-premium membership, tax/levy shape, resolvable references.
        Unique(a.ChargeTypes.Select(c => c.Code), "chargeTypes", "Charge type");
        var chargeCodes = a.ChargeTypes.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var (charge, i) in a.ChargeTypes.Select((c, i) => (c, i)))
        {
            var path = $"chargeTypes[{i}]";
            var written = charge.Category is ChargeCategory.Premium or ChargeCategory.Surcharge or ChargeCategory.Discount;
            if (charge.Category != ChargeCategory.Credit && charge.WrittenPremium != written)
            {
                Add("PFC-LINT-WRITTEN-PREMIUM", path + ".writtenPremium", "writtenPremium is derived from the category and cannot be overridden (REQ-PFC-250).");
            }

            var taxLike = charge.Category is ChargeCategory.Tax or ChargeCategory.Levy;
            if (taxLike && (charge.ComputedBy != ChargeComputedBy.TaxCalculator || charge.CancellationTreatment != CancellationTreatment.PackTaxTreatment))
            {
                Add("PFC-LINT-TAX-SHAPE", path, "Tax and levy charge types are computed by the TaxCalculator and carry PACK_TAX_TREATMENT (REQ-PFC-116, REQ-PFC-123).");
            }

            if (!taxLike && charge.ComputedBy == ChargeComputedBy.TaxCalculator)
            {
                Add("PFC-LINT-TAX-SHAPE", path + ".computedBy", "Only tax and levy charge types are computed by the TaxCalculator.");
            }

            if (charge.Category == ChargeCategory.Premium && (charge.Coverage is null || !coverageCodes.Contains(charge.Coverage)))
            {
                Add("PFC-LINT-ORPHAN-CHARGE", path + ".coverage", "A premium charge type names an existing coverage (BR-PFC-021).");
            }

            if (taxLike && (charge.BaseChargeCodes is null || charge.BaseChargeCodes.Count == 0))
            {
                Add("PFC-LINT-TAX-BASE", path + ".baseChargeCodes", "A tax or levy charge type names the charge types forming its base.");
            }

            foreach (var reference in (charge.BaseChargeCodes ?? []).Concat(charge.IncludedInTaxBases ?? []))
            {
                if (!chargeCodes.Contains(reference))
                {
                    Add("PFC-LINT-REF-001", path, $"Charge type '{reference}' does not exist.");
                }
            }

            if (charge.BillingTreatment == BillingTreatment.Billed && charge.FiscalCategoryKey is null)
            {
                Add("PFC-LINT-FISCAL-KEY", path + ".fiscalCategoryKey", "A billed charge type carries a fiscal-category key (REQ-PFC-121).");
            }
        }

        // Question sets: unique codes, resolvable conditions and field mappings, choice questions list their answers.
        Unique(a.QuestionSets.Select(s => s.Code), "questionSets", "Question set");
        foreach (var (set, i) in a.QuestionSets.Select((s, i) => (s, i)))
        {
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (q, index) in set.Questions.Select((q, index) => (q, index)))
            {
                position.TryAdd(q.Code, index);
            }

            Unique(set.Questions.Select(q => q.Code), $"questionSets[{i}].questions", "Question");
            foreach (var (question, j) in set.Questions.Select((q, j) => (q, j)))
            {
                var path = $"questionSets[{i}].questions[{j}]";
                if (question.AnswerType == QuestionAnswerType.Choice && (question.Answers is null || question.Answers.Count == 0))
                {
                    Add("PFC-LINT-ANSWERS", path + ".answers", "A CHOICE question lists its allowed answers.");
                }

                if (question.MapsToField is { } target && !fields.Contains(target))
                {
                    Add("PFC-LINT-QUESTION-FIELD", path + ".mapsToField", $"Field '{target}' does not exist.");
                }

                foreach (var condition in new[] { question.VisibleWhen, question.RequiredWhen }.OfType<QuestionCondition>())
                {
                    if (!position.TryGetValue(condition.Question, out var at) || at >= j)
                    {
                        Add("PFC-LINT-QUESTION-CONDITION", path, $"Condition must refer to an earlier question of the set; '{condition.Question}' does not (REQ-PFC-109: display rules acyclic).");
                    }
                }
            }
        }

        return errors;
    }
}
