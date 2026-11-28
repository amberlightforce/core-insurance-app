using System.Text.Json;
using System.Text.RegularExpressions;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Decline.create</c>: turn an automated DECLINE outcome, or a manual decision, into a decline record (REQ-UW-005).</summary>
internal sealed record CreateDecline(DeclineCreateRequest Request) : ICommand<DeclineCreateResponse>;

/// <summary><c>uw.Decline.requestReview</c>: the customer or producer asks for a person to look at an automated decline (REQ-UW-219).</summary>
internal sealed record RequestDeclineReview(DeclineRequestReviewRequest Request) : ICommand<DeclineRequestReviewResponse>;

/// <summary><c>uw.Decline.decideReview</c>: a second underwriter upholds or overturns the decline.</summary>
internal sealed record DecideDeclineReview(DeclineDecideReviewRequest Request) : ICommand<DeclineDecideReviewResponse>;

/// <summary>What the decline requests carry in their loosely typed JSON fields, parsed once.</summary>
internal static partial class DeclineInput
{
    public const int MaxReasons = 10;
    public const int MaxPairs = 50;

    [GeneratedRegex("^[A-Z][A-Z0-9_-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();

    public static Guid? Id(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } v && Guid.TryParse(v.GetString(), out var id) && id != Guid.Empty ? id : null;

    public static string? Text(JsonElement? value) => value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static IReadOnlyList<string>? Reasons(JsonElement? value)
    {
        if (value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return [];
        }

        if (value is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } code || !ReasonPattern().IsMatch(code))
            {
                return null;
            }

            list.Add(code);
        }

        return list.Distinct(StringComparer.Ordinal).Count() == list.Count && list.Count <= MaxReasons ? list : null;
    }

    public static IReadOnlyList<JsonElement>? Pairs(JsonElement? value)
    {
        if (value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return [];
        }

        return value is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() <= MaxPairs && array.EnumerateArray().All(p => p.ValueKind == JsonValueKind.Object)
            ? [.. array.EnumerateArray().Select(p => p.Clone())]
            : null;
    }

    public static bool Absent(JsonElement? value) => value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
}

internal sealed class CreateDeclineValidator : AbstractValidator<CreateDecline>
{
    public CreateDeclineValidator()
    {
        RuleFor(c => c.Request).Custom((r, context) =>
        {
            var job = DeclineInput.Id(r.JobRef);
            var intake = DeclineInput.Id(r.IntakeRef);
            if ((job is null) == (intake is null) || (!DeclineInput.Absent(r.JobRef) && job is null) || (!DeclineInput.Absent(r.IntakeRef) && intake is null))
            {
                context.AddFailure("jobRef", "Give exactly one of jobRef or intakeRef, as a uuid.");
            }

            var scope = DeclineInput.Absent(r.Scope) ? "FULL" : DeclineInput.Text(r.Scope);
            if (scope is not ("FULL" or "PARTIAL"))
            {
                context.AddFailure("scope", "scope must be FULL or PARTIAL.");
            }

            if (DeclineInput.Reasons(r.Reasons) is null)
            {
                context.AddFailure("reasons", $"reasons must be at most {DeclineInput.MaxReasons} distinct codes (upper-case letters, digits, '_' or '-').");
            }

            var pairs = DeclineInput.Pairs(r.Pairs);
            if (pairs is null)
            {
                context.AddFailure("pairs", $"pairs must be at most {DeclineInput.MaxPairs} objects.");
            }
            else if (scope == "PARTIAL" && pairs.Count == 0)
            {
                context.AddFailure("pairs", "A PARTIAL decline names the declined pairs.");
            }

            if (!DeclineInput.Absent(r.Wording) && DeclineInput.Text(r.Wording) is not { Length: > 0 and <= 2000 })
            {
                context.AddFailure("wording", "wording must be text of at most 2000 characters.");
            }
        });
    }
}

internal sealed class RequestDeclineReviewValidator : AbstractValidator<RequestDeclineReview>
{
    public RequestDeclineReviewValidator()
    {
        RuleFor(c => c.Request.DeclineId).NotNull().NotEqual(Guid.Empty);
        RuleFor(c => DeclineInput.Text(c.Request.Requester)).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(200);
        RuleFor(c => DeclineInput.Text(c.Request.Statement)).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(2000).WithErrorCode("REASON");
    }
}

internal sealed class DecideDeclineReviewValidator : AbstractValidator<DecideDeclineReview>
{
    public DecideDeclineReviewValidator()
    {
        RuleFor(c => c.Request.DeclineId).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Outcome).IsInEnum();
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
    }
}

/// <summary>Shared steps of the decline commands: the legal entity, the JSON view of a decline, the letter data and the event.</summary>
internal sealed class DeclineSupport(
    DeclineStore store, UnderwritingStore uwStore, RequestContext context, ILegalEntityDirectory legalEntities, IEventPublisher events, IServiceProvider services)
{
    public const string LetterDocumentType = "DT-REFUSAL-LETTER";

    public DeclineStore Store => store;

    public UnderwritingStore Uw => uwStore;

    public string Me => context.Actor.ToString();

    public bool CallerIsHuman => context.Actor.Kind == ActorKind.User;

    public Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public static DomainError NotFound(Guid id) => DomainError.Of(ModuleCode.UW, "DECLINE-NOT-FOUND", $"Decline {id:D} was not found.");

    public static DomainError State(string message) => DomainError.Of(ModuleCode.UW, "DECLINE-STATE", message);

    /// <summary>The decline as the contract's JSON (<c>decline</c>).</summary>
    public static JsonElement View(DeclineRecord r) => JsonSerializer.SerializeToElement(
        new
        {
            id = r.DeclineId,
            declineNumber = r.DeclineNumber,
            jobRef = r.JobId,
            intakeRef = r.IntakeId,
            scope = r.Scope,
            reasonCodes = JsonSerializer.Deserialize<JsonElement>(r.ReasonCodes),
            pairs = JsonSerializer.Deserialize<JsonElement>(r.Pairs),
            wording = r.Wording,
            automated = r.Automated,
            status = r.Status,
            issuedAt = Iso(r.CreatedAt),
            issuedBy = r.CreatedBy,
            letter = new { status = r.LetterStatus, data = JsonSerializer.Deserialize<JsonElement>(r.LetterData) },
            review = r.ReviewRequestedAt is null
                ? null
                : new
                {
                    requestedAt = Iso(r.ReviewRequestedAt.Value),
                    requestedBy = r.ReviewRequestedBy,
                    requester = r.ReviewRequester,
                    statement = r.ReviewStatement,
                    decidedAt = r.ReviewDecidedAt is { } at ? Iso(at) : null,
                    decidedBy = r.ReviewDecidedBy,
                    outcome = r.ReviewOutcome,
                    reason = r.ReviewReason,
                },
            recordVersion = r.RecordVersion,
        }, SharedKernelJson.Options);

    /// <summary>
    /// Whether a refusal document is required (<c>refusalRequirement</c>): the core default is NotRequired (REQ-MKT-103, the country pack
    /// binds the real rule); an automated decline always needs the notice of automation and the review route (REQ-UW-219).
    /// </summary>
    public static JsonElement Requirement(DeclineRecord r) => JsonSerializer.SerializeToElement(
        r.Status == "Overturned"
            ? new { required = false, rule = "OVERTURNED", source = "CORE_DEFAULT" }
            : r.Automated
                ? new { required = true, rule = "AUTOMATED_DECISION_NOTICE", source = "REQ-UW-219" }
                : new { required = false, rule = "NOT_REQUIRED", source = "CORE_DEFAULT" },
        SharedKernelJson.Options);

    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Asks DOC to compose the refusal letter (preview, nothing is persisted there). When DOC has no template for the type yet the letter
    /// data stays on the decline and the status says so; the decline itself never fails because of the letter.
    /// </summary>
    public async Task<string> TryComposeLetterAsync(string number, IReadOnlyList<string> reasons, bool automated, DateTime at, CancellationToken cancellationToken)
    {
        if (services.GetService<IDocumentsDocumentService>() is not { } documents)
        {
            return "DATA_READY";
        }

        try
        {
            await documents.PreviewAsync(
                new DocumentPreviewRequest
                {
                    DocumentType = LetterDocumentType,
                    Language = "el",
                    Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["declineNumber"] = number,
                        ["reasons"] = string.Join(", ", reasons),
                        ["declineDate"] = at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        ["automatedNotice"] = automated ? "yes" : "no",
                    },
                }, cancellationToken).ConfigureAwait(false);
            return "COMPOSED";
        }
        catch (DomainException)
        {
            // TODO(hardening): the DT-REFUSAL-LETTER template is not seeded or approved yet; the letter is requested again once it is
            // (and with the customer name, which UW does not hold: the sender passes it to doc.Document.request).
            return "PENDING_TEMPLATE";
        }
    }

    public void PublishIssued(DeclineRecord r, IReadOnlyList<string> reasons, IReadOnlyList<JsonElement> pairs)
    {
        var keys = BusinessKeys.Empty.With("declineId", r.DeclineId.ToString("D"));
        string aggregateType;
        string aggregateId;
        if (r.JobId is { } job)
        {
            keys = keys.With("jobId", job.ToString("D"));
            (aggregateType, aggregateId) = ("Job", job.ToString("D"));
        }
        else
        {
            (aggregateType, aggregateId) = ("SubmissionIntake", r.IntakeId!.Value.ToString("D"));
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(DeclineIssuedV1.Descriptor), aggregateType, aggregateId,
            new DeclineIssuedV1
            {
                DeclineId = r.DeclineId,
                DeclineNumber = r.DeclineNumber,
                JobId = r.JobId is { } j ? JobId.From(j) : null,
                IntakeId = r.IntakeId,
                Scope = r.Scope,
                ReasonCodes = reasons,
                DeclinedPairs = pairs,
                Automated = r.Automated,
            },
            keys));
    }
}

/// <summary>
/// Creates the decline. With a job: when its newest evaluation is a DECLINE the decline is automated (the reasons default to the rules that
/// matched); otherwise it is a manual decision by a person who must give the reasons (UW-ERR-REASON-REQUIRED). A job or intake has at most
/// one standing decline (UW-ERR-DECLINE-EXISTS) until it is overturned. The number comes from the PLT series DECLINE; <c>DeclineIssued</c>
/// is published in the same transaction.
/// </summary>
internal sealed class CreateDeclineHandler(DeclineSupport support, INumberingService numbering) : ICommandHandler<CreateDecline, DeclineCreateResponse>
{
    public async Task<Result<DeclineCreateResponse>> HandleAsync(CreateDecline command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var jobId = DeclineInput.Id(request.JobRef);
        var intakeId = DeclineInput.Id(request.IntakeRef);
        var legalEntity = support.LegalEntity;
        var reasons = (IReadOnlyList<string>)[.. DeclineInput.Reasons(request.Reasons) ?? []];
        var pairs = DeclineInput.Pairs(request.Pairs) ?? [];
        var scope = DeclineInput.Absent(request.Scope) ? "FULL" : DeclineInput.Text(request.Scope)!;

        EvaluationOutcome? evaluation = null;
        if (jobId is { } job)
        {
            await support.Uw.LockJobAsync(job, cancellationToken).ConfigureAwait(false);
            evaluation = await support.Store.LatestEvaluationAsync(legalEntity, job, cancellationToken).ConfigureAwait(false);
        }

        var automated = evaluation is { Outcome: "DECLINE" };
        if (!automated && !support.CallerIsHuman)
        {
            return DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", "Only a person may decline a risk that the rules did not decline.");
        }

        if (reasons.Count == 0 && automated)
        {
            // TODO(hardening): keep the rule type in the evaluation trace so the reasons are the DECLINE rules only, not every matched rule.
            var declining = evaluation!.MatchedRules.Where(r => r.StartsWith("DECLINE", StringComparison.Ordinal)).ToList();
            reasons = [.. (declining.Count > 0 ? declining : [.. evaluation.MatchedRules]).Take(DeclineInput.MaxReasons)];
        }

        if (reasons.Count == 0)
        {
            return DomainError.Of(ModuleCode.UW, "REASON-REQUIRED", "A decline needs at least one reason code.");
        }

        if (await support.Store.StandingAsync(legalEntity, jobId, intakeId, cancellationToken).ConfigureAwait(false) is { } standing)
        {
            return DomainError.Of(ModuleCode.UW, "DECLINE-EXISTS", $"Decline {standing.DeclineNumber} already stands for this {(jobId is null ? "intake" : "job")}.");
        }

        var number = (await numbering.NextAsync(new NumberRequest(NumberingSchemes.Decline), cancellationToken).ConfigureAwait(false)).Value;
        var now = support.Store.Now;
        var letterData = JsonSerializer.Serialize(
            new
            {
                declineNumber = number,
                issuedAt = DeclineSupport.Iso(now),
                scope,
                reasonCodes = reasons,
                wording = DeclineInput.Text(request.Wording),
                automated,
                automatedNotice = automated,
                humanReviewRoute = automated ? "uw.Decline.requestReview" : null,
            }, SharedKernelJson.Options);
        var row = new DeclineRecord
        {
            DeclineId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            DeclineNumber = number,
            JobId = jobId,
            IntakeId = intakeId,
            Scope = scope,
            ReasonCodes = JsonSerializer.Serialize(reasons),
            Pairs = scope == "FULL" ? "[]" : JsonSerializer.Serialize(pairs),
            Wording = DeclineInput.Text(request.Wording),
            Automated = automated,
            Status = "Issued",
            LetterData = letterData,
            LetterStatus = await support.TryComposeLetterAsync(number, reasons, automated, now, cancellationToken).ConfigureAwait(false),
            CreatedAt = now,
            CreatedBy = support.Me,
            RecordVersion = 1,
        };
        await support.Store.InsertAsync(row, evaluation?.EvaluationId, cancellationToken).ConfigureAwait(false);
        support.PublishIssued(row, reasons, scope == "FULL" ? [] : pairs);
        return new DeclineCreateResponse { Decline = DeclineSupport.View(row), RefusalRequirement = DeclineSupport.Requirement(row) };
    }
}

/// <summary>
/// The customer or producer (through CHN, or staff on their behalf) asks for a person to review an automated decline. A decline decided by a
/// person is not reviewable this way; one review is open at a time. The "review referral" is the decline itself in status ReviewRequested.
/// </summary>
internal sealed class RequestDeclineReviewHandler(DeclineSupport support) : ICommandHandler<RequestDeclineReview, DeclineRequestReviewResponse>
{
    public async Task<Result<DeclineRequestReviewResponse>> HandleAsync(RequestDeclineReview command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var id = request.DeclineId!.Value;
        if (await support.Store.GetAsync(support.LegalEntity, id, forUpdate: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return DeclineSupport.NotFound(id);
        }

        if (!row.Automated)
        {
            return DeclineSupport.State("Only an automated decline can be sent for human review; this one was decided by a person.");
        }

        if (row.Status != "Issued")
        {
            return DeclineSupport.State($"Decline {row.DeclineNumber} is {row.Status}; a review can be requested once, on an Issued decline.");
        }

        await support.Store.RequestReviewAsync(id, support.Me, DeclineInput.Text(request.Requester)!, DeclineInput.Text(request.Statement)!, cancellationToken).ConfigureAwait(false);
        return new DeclineRequestReviewResponse
        {
            ReviewReferralRef = JsonSerializer.SerializeToElement(new { id, kind = "DECLINE_REVIEW", declineNumber = row.DeclineNumber, status = "ReviewRequested" }, SharedKernelJson.Options),
        };
    }
}

/// <summary>
/// The second pair of eyes: a person other than the one who issued the decline upholds or overturns it. Overturning ends the decline (the
/// job may be quoted again; a new decline can then be issued) and is final.
/// </summary>
internal sealed class DecideDeclineReviewHandler(DeclineSupport support) : ICommandHandler<DecideDeclineReview, DeclineDecideReviewResponse>
{
    public async Task<Result<DeclineDecideReviewResponse>> HandleAsync(DecideDeclineReview command, CancellationToken cancellationToken)
    {
        if (!support.CallerIsHuman)
        {
            return DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", "Only a person may decide the review of a decline.");
        }

        var request = command.Request;
        var id = request.DeclineId;
        if (await support.Store.GetAsync(support.LegalEntity, id, forUpdate: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return DeclineSupport.NotFound(id);
        }

        if (row.Status != "ReviewRequested")
        {
            return DeclineSupport.State($"Decline {row.DeclineNumber} is {row.Status}; only a decline with a requested review can be decided.");
        }

        if (string.Equals(row.CreatedBy, support.Me, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.UW, "SOD", "A decline is reviewed by a person other than the one who issued it.");
        }

        var outcome = request.Outcome == DeclineDecideReviewRequest.OutcomeValue.Upheld ? "UPHELD" : "OVERTURNED";
        await support.Store.DecideReviewAsync(id, outcome, support.Me, request.Reason, cancellationToken).ConfigureAwait(false);
        // TODO(hardening): tell POL to return the job to Quoted (REQ-UW-220) once an event for an overturned decline exists in the catalogue.
        var updated = (await support.Store.GetAsync(support.LegalEntity, id, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        return new DeclineDecideReviewResponse { Decline = DeclineSupport.View(updated), RefusalRequirement = DeclineSupport.Requirement(updated) };
    }
}

/// <summary>Audit facts of the decline commands: the decline, its number, status and the reasons given.</summary>
internal sealed class DeclineAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result)
    {
        Guid? id = command switch
        {
            RequestDeclineReview c => c.Request.DeclineId,
            DecideDeclineReview c => c.Request.DeclineId,
            _ => null,
        };
        JsonElement? decline = result is { IsSuccess: true } ok
            ? ok.Value switch { DeclineCreateResponse r => r.Decline, DeclineDecideReviewResponse r => r.Decline, _ => null }
            : null;
        if (decline is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("id", out var idProperty) && idProperty.TryGetGuid(out var created))
        {
            id = created;
        }

        var action = command switch { CreateDecline => "CREATE", RequestDeclineReview => "REQUEST_REVIEW", _ => "DECIDE_REVIEW" };
        var reason = command switch
        {
            DecideDeclineReview c => $"{c.Request.Outcome}: {c.Request.Reason}",
            RequestDeclineReview c => DeclineInput.Text(c.Request.Statement),
            _ => null,
        };
        return new CommandAuditFacts
        {
            ObjectRef = id is { } known ? new ObjectRef(ModuleCode.UW, "Decline", known.ToString("D")) : null,
            BusinessKeys = id is { } key ? BusinessKeys.Empty.With("declineId", key.ToString("D")) : BusinessKeys.Empty,
            Changes = Platform.Audit.AuditDiff.Compute(new { }, new { action, reason, status = decline is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("status", out var st) ? st.GetString() : null }),
        };
    }
}
