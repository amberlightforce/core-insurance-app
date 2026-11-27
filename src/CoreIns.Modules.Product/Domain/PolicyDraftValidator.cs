using System.Globalization;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Product.Domain;

/// <summary>
/// <c>pfc.PolicyDraft.validate</c> (BO-PFC-05): pure validation of a risk and coverage selection against a compiled artefact
/// (REQ-PFC-224, -225): element cardinality, field presence and type, CHOICE membership, coverage existence, requires and
/// term options and ranges, the Greece MTPL statutory coverage, and question answers (through <see cref="QuestionEvaluator"/>).
/// </summary>
internal static class PolicyDraftValidator
{
    public static List<PolicyDraftIssue> Validate(ProductArtefact artefact, PolicyDraft draft, string? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(artefact);
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<PolicyDraftIssue>();
        var elements = draft.Elements ?? [];
        var coverages = draft.Coverages ?? [];
        ValidateElements(artefact, elements, checkpoint, issues);
        ValidateCoverages(artefact, elements, coverages, issues);
        ValidateAnswers(artefact, draft, issues);
        return issues;
    }

    private static void ValidateElements(ProductArtefact a, IReadOnlyList<PolicyDraftElement> instances, string? checkpoint, List<PolicyDraftIssue> issues)
    {
        var defs = a.Elements.ToDictionary(e => e.Code, StringComparer.Ordinal);
        var byId = new Dictionary<string, PolicyDraftElement>(StringComparer.Ordinal);
        for (var i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            var path = $"elements[{i}]";
            if (!defs.TryGetValue(inst.Element, out var def))
            {
                issues.Add(Issue("ELEMENT", "PFC-VAL-UNKNOWN-ELEMENT", path + ".element", "Element '" + inst.Element + "' is not part of this product.", "Το στοιχείο '" + inst.Element + "' δεν ανήκει στο προϊόν."));
                continue;
            }

            if (!string.IsNullOrEmpty(inst.Id) && !byId.TryAdd(inst.Id, inst))
            {
                issues.Add(Issue("ELEMENT", "PFC-VAL-DUPLICATE-ID", path + ".id", "Element id '" + inst.Id + "' is used more than once.", "Το αναγνωριστικό '" + inst.Id + "' χρησιμοποιείται περισσότερες από μία φορές."));
            }

            ValidateFields(def, inst, path, checkpoint, issues);
        }

        // Parent links.
        for (var i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            if (!defs.TryGetValue(inst.Element, out var def))
            {
                continue;
            }

            var path = $"elements[{i}].parent";
            if (def.Parent is null)
            {
                if (!string.IsNullOrEmpty(inst.Parent))
                {
                    issues.Add(Issue("ELEMENT", "PFC-VAL-PARENT", path, "Element '" + def.Code + "' has no parent element.", "Το στοιχείο '" + def.Code + "' δεν έχει γονικό στοιχείο."));
                }
            }
            else if (string.IsNullOrEmpty(inst.Parent) || !byId.TryGetValue(inst.Parent, out var parent) || parent.Element != def.Parent)
            {
                issues.Add(Issue("ELEMENT", "PFC-VAL-PARENT", path, "Element '" + def.Code + "' needs a parent instance of '" + def.Parent + "'.", "Το στοιχείο '" + def.Code + "' απαιτεί γονική εμφάνιση του '" + def.Parent + "'."));
            }
        }

        // Cardinality: per product for a root element, per parent instance for a child.
        foreach (var def in a.Elements)
        {
            if (def.Parent is null)
            {
                Cardinality(def, instances.Count(x => x.Element == def.Code), "elements", issues);
                continue;
            }

            for (var i = 0; i < instances.Count; i++)
            {
                if (instances[i].Element == def.Parent && !string.IsNullOrEmpty(instances[i].Id))
                {
                    Cardinality(def, instances.Count(x => x.Element == def.Code && x.Parent == instances[i].Id), $"elements[{i}]", issues);
                }
            }
        }
    }

    private static void Cardinality(CatalogueElement def, int count, string path, List<PolicyDraftIssue> issues)
    {
        if (count < def.Minimum || count > def.Maximum)
        {
            issues.Add(Issue("ELEMENT", "PFC-VAL-CARDINALITY", path,
                $"Element '{def.Code}' needs between {def.Minimum} and {def.Maximum} entries; {count} given.",
                $"Το στοιχείο '{def.Code}' απαιτεί από {def.Minimum} έως {def.Maximum} εγγραφές· δόθηκαν {count}."));
        }
    }

    private static void ValidateFields(CatalogueElement def, PolicyDraftElement inst, string path, string? checkpoint, List<PolicyDraftIssue> issues)
    {
        var fields = inst.Fields ?? new Dictionary<string, string>();
        foreach (var code in fields.Keys.Where(k => def.Fields.All(f => f.Code != k)))
        {
            issues.Add(Issue("FIELD", "PFC-VAL-UNKNOWN-FIELD", $"{path}.fields.{code}", $"Field '{code}' is not part of element '{def.Code}'.", $"Το πεδίο '{code}' δεν ανήκει στο στοιχείο '{def.Code}'."));
        }

        foreach (var field in def.Fields)
        {
            var fp = $"{path}.fields.{field.Code}";
            fields.TryGetValue(field.Code, out var value);
            if (string.IsNullOrWhiteSpace(value))
            {
                var applies = checkpoint is null || field.RequiredAt is null || field.RequiredAt.Contains(checkpoint, StringComparer.Ordinal);
                if (field.Required && applies)
                {
                    issues.Add(Issue("FIELD", "PFC-VAL-REQUIRED", fp, $"Field '{field.Code}' is required.", $"Το πεδίο '{field.Code}' είναι υποχρεωτικό."));
                }

                continue;
            }

            switch (field.DataType)
            {
                case FieldDataType.Choice:
                    if (field.Choices?.Contains(value, StringComparer.Ordinal) != true)
                    {
                        issues.Add(Issue("FIELD", "PFC-VAL-CHOICE", fp, $"'{value}' is not an allowed value of '{field.Code}'.", $"Η τιμή '{value}' δεν επιτρέπεται για το '{field.Code}'."));
                    }

                    break;
                case FieldDataType.Integer or FieldDataType.Decimal or FieldDataType.Money:
                    if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                        || (field.DataType == FieldDataType.Integer && number != decimal.Truncate(number)))
                    {
                        issues.Add(Issue("FIELD", "PFC-VAL-TYPE", fp, $"'{value}' is not a valid number for '{field.Code}'.", $"Η τιμή '{value}' δεν είναι έγκυρος αριθμός για το '{field.Code}'."));
                    }
                    else if ((field.Minimum is { } min && number < min) || (field.Maximum is { } max && number > max))
                    {
                        issues.Add(Issue("FIELD", "PFC-VAL-RANGE", fp, $"'{field.Code}' must be between {field.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-"} and {field.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "-"}.", $"Το '{field.Code}' είναι εκτός των επιτρεπτών ορίων."));
                    }

                    break;
                case FieldDataType.Date:
                    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        issues.Add(Issue("FIELD", "PFC-VAL-TYPE", fp, $"'{value}' is not a date (yyyy-MM-dd).", $"Η τιμή '{value}' δεν είναι ημερομηνία (yyyy-MM-dd)."));
                    }

                    break;
                case FieldDataType.Boolean:
                    if (value is not ("true" or "false"))
                    {
                        issues.Add(Issue("FIELD", "PFC-VAL-TYPE", fp, $"'{value}' is not true or false.", $"Η τιμή '{value}' δεν είναι true ή false."));
                    }

                    break;
            }
        }
    }

    private static void ValidateCoverages(ProductArtefact a, IReadOnlyList<PolicyDraftElement> instances, IReadOnlyList<PolicyDraftCoverage> selected, List<PolicyDraftIssue> issues)
    {
        var defs = a.Coverages.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < selected.Count; i++)
        {
            var sel = selected[i];
            var path = $"coverages[{i}]";
            if (!defs.TryGetValue(sel.Coverage, out var def))
            {
                issues.Add(Issue("COVERAGE", "PFC-VAL-UNKNOWN-COVERAGE", path + ".coverage", $"Coverage '{sel.Coverage}' is not part of this product.", $"Η κάλυψη '{sel.Coverage}' δεν ανήκει στο προϊόν."));
                continue;
            }

            if (!seen.Add(sel.Coverage + "|" + (sel.Element ?? string.Empty)))
            {
                issues.Add(Issue("COVERAGE", "PFC-VAL-DUPLICATE-COVERAGE", path, $"Coverage '{sel.Coverage}' is selected more than once.", $"Η κάλυψη '{sel.Coverage}' επιλέχθηκε περισσότερες από μία φορές."));
            }

            if (!string.IsNullOrEmpty(sel.Element)
                && !instances.Any(x => x.Id == sel.Element && x.Element == def.CoveredElement))
            {
                issues.Add(Issue("COVERAGE", "PFC-VAL-COVERAGE-ELEMENT", path + ".element", $"Coverage '{def.Code}' covers an instance of '{def.CoveredElement}'; '{sel.Element}' is not one.", $"Η κάλυψη '{def.Code}' αφορά εμφάνιση του '{def.CoveredElement}'."));
            }

            foreach (var requires in def.Requires ?? [])
            {
                if (!selected.Any(s => s.Coverage == requires && (s.Element is null || sel.Element is null || s.Element == sel.Element)))
                {
                    issues.Add(Issue("COVERAGE", "PFC-VAL-REQUIRES", path, $"Coverage '{def.Code}' requires coverage '{requires}'.", $"Η κάλυψη '{def.Code}' απαιτεί την κάλυψη '{requires}'."));
                }
            }

            ValidateTerms(a, def, sel, path, issues);
        }

        // Required coverages (the Greece MTPL statutory one, REQ-PFC-088, is flagged with its own rule).
        foreach (var def in a.Coverages.Where(c => c.Existence == CoverageExistence.Required))
        {
            var mtpl = a.Jurisdiction == "GR" && def.Code == "MTPL";
            foreach (var inst in instances.Where(x => x.Element == def.CoveredElement))
            {
                if (!selected.Any(s => s.Coverage == def.Code && (s.Element is null || s.Element == inst.Id)))
                {
                    issues.Add(mtpl
                        ? Issue("COVERAGE", "PFC-VAL-MTPL-REQUIRED", "coverages", $"Greece: MTPL is mandatory on every {def.CoveredElement} (REQ-PFC-088).", $"Ελλάδα: η υποχρεωτική ασφάλιση αστικής ευθύνης (MTPL) απαιτείται σε κάθε {def.CoveredElement}.")
                        : Issue("COVERAGE", "PFC-VAL-REQUIRED-COVERAGE", "coverages", $"Coverage '{def.Code}' is required.", $"Η κάλυψη '{def.Code}' είναι υποχρεωτική."));
                }
            }
        }
    }

    private static void ValidateTerms(ProductArtefact a, CatalogueCoverage def, PolicyDraftCoverage sel, string path, List<PolicyDraftIssue> issues)
    {
        var values = sel.Terms ?? new Dictionary<string, string>();
        foreach (var code in values.Keys.Where(k => def.Terms.All(t => t.Code != k)))
        {
            issues.Add(Issue("TERM", "PFC-VAL-UNKNOWN-TERM", $"{path}.terms.{code}", $"Term '{code}' is not part of coverage '{def.Code}'.", $"Ο όρος '{code}' δεν ανήκει στην κάλυψη '{def.Code}'."));
        }

        foreach (var term in def.Terms)
        {
            var tp = $"{path}.terms.{term.Code}";
            var statutory = a.Jurisdiction == "GR" && def.Code == "MTPL" && ArtefactCompiler.MtplFinals.ContainsKey(term.Code);
            if (!values.TryGetValue(term.Code, out var value) || string.IsNullOrWhiteSpace(value))
            {
                if (statutory)
                {
                    issues.Add(Issue("TERM", "PFC-VAL-MTPL-MINIMUM", tp, $"Greece: the MTPL limit '{term.Code}' must be chosen (statutory minimum, REQ-PFC-085).", $"Ελλάδα: το όριο MTPL '{term.Code}' πρέπει να επιλεγεί (νόμιμο ελάχιστο)."));
                }
                else if (term.Required)
                {
                    issues.Add(Issue("TERM", "PFC-VAL-TERM-REQUIRED", tp, $"Term '{term.Code}' is required.", $"Ο όρος '{term.Code}' είναι υποχρεωτικός."));
                }

                continue;
            }

            if (term.Kind == TermKind.OptionList && term.Options is { Count: > 0 } options)
            {
                var match = options.FirstOrDefault(o => o.Code == value);
                if (match is null)
                {
                    issues.Add(Issue("TERM", "PFC-VAL-TERM-OPTION", tp, $"'{value}' is not an offered option of '{term.Code}'.", $"Η τιμή '{value}' δεν είναι διαθέσιμη επιλογή του '{term.Code}'."));
                }
                else if (statutory && match.Value < options.Min(o => o.Value))
                {
                    issues.Add(Issue("TERM", "PFC-VAL-MTPL-MINIMUM", tp, $"Greece: '{term.Code}' is below the statutory minimum.", $"Ελλάδα: το '{term.Code}' είναι κάτω από το νόμιμο ελάχιστο."));
                }
            }
            else if (term.Kind == TermKind.Range && term.Range is { } range)
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || number < range.Minimum || number > range.Maximum)
                {
                    issues.Add(Issue("TERM", "PFC-VAL-TERM-RANGE", tp, $"'{term.Code}' must be a number between {range.Minimum.ToString(CultureInfo.InvariantCulture)} and {range.Maximum.ToString(CultureInfo.InvariantCulture)}.", $"Το '{term.Code}' πρέπει να είναι αριθμός εντός των ορίων."));
                }
            }
        }
    }

    private static void ValidateAnswers(ProductArtefact a, PolicyDraft draft, List<PolicyDraftIssue> issues)
    {
        var answers = draft.Answers ?? new Dictionary<string, string>();
        if (string.IsNullOrEmpty(draft.QuestionSet))
        {
            if (answers.Count > 0)
            {
                issues.Add(Issue("QUESTION", "PFC-VAL-QUESTION-SET", "questionSet", "Answers were given without a question set.", "Δόθηκαν απαντήσεις χωρίς σύνολο ερωτήσεων."));
            }

            return;
        }

        var set = a.QuestionSets.FirstOrDefault(s => s.Code == draft.QuestionSet);
        if (set is null)
        {
            issues.Add(Issue("QUESTION", "PFC-VAL-QUESTION-SET", "questionSet", $"Question set '{draft.QuestionSet}' is not part of this product.", $"Το σύνολο ερωτήσεων '{draft.QuestionSet}' δεν ανήκει στο προϊόν."));
            return;
        }

        var before = issues.Count;
        foreach (var (code, given) in answers)
        {
            var question = set.Questions.FirstOrDefault(q => q.Code == code);
            if (question is null)
            {
                issues.Add(Issue("QUESTION", "PFC-VAL-UNKNOWN-QUESTION", "answers." + code, $"Question '{code}' is not in question set '{set.Code}'.", $"Η ερώτηση '{code}' δεν ανήκει στο σύνολο '{set.Code}'."));
            }
            else if (question.AnswerType == QuestionAnswerType.Choice && !string.IsNullOrWhiteSpace(given) && question.Answers?.Any(x => x.Code == given) != true)
            {
                issues.Add(Issue("QUESTION", "PFC-VAL-CHOICE", "answers." + code, $"'{given}' is not an allowed answer to '{code}'.", $"Η απάντηση '{given}' δεν επιτρέπεται για την ερώτηση '{code}'."));
            }
        }

        if (issues.Count > before)
        {
            return;
        }

        var evaluated = QuestionEvaluator.Evaluate(set, answers);
        if (evaluated.IsFailure)
        {
            issues.Add(Issue("QUESTION", "PFC-VAL-QUESTION", "answers", evaluated.Error.Detail ?? "Invalid answers.", evaluated.Error.Detail ?? "Μη έγκυρες απαντήσεις."));
            return;
        }

        foreach (var code in evaluated.Value.MissingRequired)
        {
            issues.Add(Issue("QUESTION", "PFC-VAL-REQUIRED", "answers." + code, $"Question '{code}' must be answered.", $"Η ερώτηση '{code}' πρέπει να απαντηθεί."));
        }

        foreach (var hit in evaluated.Value.KnockOuts)
        {
            issues.Add(Issue("QUESTION", "PFC-VAL-KNOCKOUT", "answers." + hit.Question, $"The answer to '{hit.Question}' makes the risk ineligible.", $"Η απάντηση στην '{hit.Question}' καθιστά τον κίνδυνο μη ασφαλίσιμο."));
        }

        foreach (var hit in evaluated.Value.Referrals)
        {
            issues.Add(Issue("QUESTION", "PFC-VAL-REFERRAL", "answers." + hit.Question, $"The answer to '{hit.Question}' refers the risk to an underwriter.", $"Η απάντηση στην '{hit.Question}' παραπέμπει τον κίνδυνο σε ασφαλιστή.", warning: true));
        }
    }

    private static PolicyDraftIssue Issue(string level, string rule, string path, string en, string el, bool warning = false) => new()
    {
        Level = level,
        Rule = rule,
        Path = path,
        MessageKey = "pfc.val." + rule["PFC-VAL-".Length..].ToLowerInvariant(),
        Message = new LocalizedText { El = el, En = en },
        Severity = warning ? PolicyDraftIssue.SeverityValue.Warning : PolicyDraftIssue.SeverityValue.Error,
    };
}
