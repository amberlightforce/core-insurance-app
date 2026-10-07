using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Product.Domain;

/// <summary>
/// Pure evaluation of a question set against the answers given so far (REQ-PFC-006): which questions are visible and
/// required, which answers knock the quote out or refer it, and which visible required questions are still open.
/// Conditions are declarative and refer only to earlier questions, so one forward pass suffices.
/// </summary>
internal static class QuestionEvaluator
{
    public static Result<QuestionSetEvaluateResponse> Evaluate(QuestionSetDef set, IReadOnlyDictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(answers);
        var known = set.Questions.ToDictionary(q => q.Code, StringComparer.Ordinal);
        foreach (var code in answers.Keys)
        {
            if (!known.ContainsKey(code))
            {
                return DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"Question '{code}' is not in question set '{set.Code}'.");
            }
        }

        var visibleSoFar = new Dictionary<string, bool>(StringComparer.Ordinal);
        var states = new List<QuestionState>();
        var knockOuts = new List<QuestionOutcomeHit>();
        var referrals = new List<QuestionOutcomeHit>();
        var missing = new List<string>();

        foreach (var question in set.Questions)
        {
            var visible = question.VisibleWhen is null || Holds(question.VisibleWhen, answers, visibleSoFar);
            visibleSoFar[question.Code] = visible;
            var required = visible && (question.Required || (question.RequiredWhen is not null && Holds(question.RequiredWhen, answers, visibleSoFar)));
            var answered = visible && answers.TryGetValue(question.Code, out var value) && !string.IsNullOrWhiteSpace(value);
            states.Add(new QuestionState { Question = question.Code, Visible = visible, Required = required, Answered = answered });

            if (required && !answered)
            {
                missing.Add(question.Code);
            }

            if (!answered)
            {
                continue;
            }

            var given = answers[question.Code];
            if (question.AnswerType != QuestionAnswerType.Choice)
            {
                continue;
            }

            var match = question.Answers?.FirstOrDefault(a => string.Equals(a.Code, given, StringComparison.Ordinal));
            if (match is null)
            {
                return DomainError.Of(ModuleCode.PFC, "UNKNOWN-ITEM", $"'{given}' is not an allowed answer to question '{question.Code}'.");
            }

            var hit = new QuestionOutcomeHit { Question = question.Code, Answer = match.Code, ReasonKey = match.ReasonKey, UwRuleCode = match.UwRuleCode };
            if (match.Outcome == AnswerOutcome.KnockOut)
            {
                knockOuts.Add(hit);
            }
            else if (match.Outcome == AnswerOutcome.Referral)
            {
                referrals.Add(hit);
            }
        }

        return new QuestionSetEvaluateResponse
        {
            Questions = states,
            KnockOuts = knockOuts,
            Referrals = referrals,
            MissingRequired = missing,
            Complete = missing.Count == 0,
        };
    }

    private static bool Holds(QuestionCondition condition, IReadOnlyDictionary<string, string> answers, Dictionary<string, bool> visible) =>
        visible.TryGetValue(condition.Question, out var shown) && shown
        && answers.TryGetValue(condition.Question, out var value) && condition.AnsweredWith.Contains(value, StringComparer.Ordinal);
}
