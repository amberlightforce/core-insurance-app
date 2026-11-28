using System.Text.Json;
using System.Text.RegularExpressions;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.Contingency.create</c>: a condition on a policy term with a due date and an owner (REQ-UW-006).</summary>
internal sealed record CreateContingency(ContingencyCreateRequest Request) : ICommand<ContingencyCreateResponse>;

/// <summary><c>uw.Contingency.update</c>: change the text or owner of an open contingency.</summary>
internal sealed record UpdateContingency(Guid Id, ContingencyUpdateRequest Request) : ICommand<ContingencyUpdateResponse>;

/// <summary><c>uw.Contingency.satisfy</c>: the evidence arrived.</summary>
internal sealed record SatisfyContingency(ContingencySatisfyRequest Request) : ICommand<ContingencySatisfyResponse>;

/// <summary><c>uw.Contingency.waive</c>: an underwriter waives the contingency, with a reason.</summary>
internal sealed record WaiveContingency(ContingencyWaiveRequest Request) : ICommand<ContingencyWaiveResponse>;

/// <summary><c>uw.Contingency.extend</c>: move the due date later, with a reason.</summary>
internal sealed record ExtendContingency(ContingencyExtendRequest Request) : ICommand<ContingencyExtendResponse>;

/// <summary><c>uw.Contingency.applyConsequence</c>: apply the consequence of an overdue contingency (cancel the quote, or add an issue).</summary>
internal sealed record ApplyContingencyConsequence(ContingencyApplyConsequenceRequest Request) : ICommand<ContingencyApplyConsequenceResponse>;

/// <summary>The overdue sweep (internal command, no HTTP route): marks Open contingencies past their due date and publishes <c>ContingencyOverdue</c> once each.</summary>
internal sealed record SweepContingencies : ICommand<int>;

internal static partial class ContingencyInput
{
    [GeneratedRegex("^[A-Z][A-Z0-9_-]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex CodePattern();

    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Point(BlockingPoint? point) => point switch
    {
        BlockingPoint.PreQuote => "PRE_QUOTE",
        BlockingPoint.PreBind => "PRE_BIND",
        BlockingPoint.NonBlocking => "NON_BLOCKING",
        _ => "PRE_ISSUE",
    };

    public static string Consequence(ContingencyCreateRequest.ConsequenceCodeValue? code) => code switch
    {
        ContingencyCreateRequest.ConsequenceCodeValue.CancelQuote => "CANCEL_QUOTE",
        ContingencyCreateRequest.ConsequenceCodeValue.AddIssue => "ADD_ISSUE",
        _ => "NONE",
    };
}

internal sealed class CreateContingencyValidator : AbstractValidator<CreateContingency>
{
    public CreateContingencyValidator()
    {
        RuleFor(c => c.Request.TermRef).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.JobRef!.Value.Value).NotEqual(Guid.Empty).When(c => c.Request.JobRef is not null);
        RuleFor(c => c.Request.ContingencyType).NotEmpty().Must(t => ContingencyInput.CodePattern().IsMatch(t ?? string.Empty)).WithMessage("contingencyType must be an upper-case code.");
        RuleFor(c => c.Request.Description).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(1000);
        RuleFor(c => c.Request.CustomerText).MaximumLength(1000);
        RuleFor(c => c.Request.Owner).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(200);
        RuleFor(c => c.Request.BlockingPoint).IsInEnum().When(c => c.Request.BlockingPoint is not null);
        RuleFor(c => c.Request.ConsequenceCode).IsInEnum().When(c => c.Request.ConsequenceCode is not null);
        RuleFor(c => c.Request.ConsequenceIssueType)
            .NotEmpty().WithMessage("consequenceIssueType is required for ADD_ISSUE.")
            .When(c => c.Request.ConsequenceCode == ContingencyCreateRequest.ConsequenceCodeValue.AddIssue);
        RuleFor(c => c.Request.JobRef)
            .NotNull().WithMessage("jobRef is required for a CANCEL_QUOTE or ADD_ISSUE consequence.")
            .When(c => c.Request.ConsequenceCode is ContingencyCreateRequest.ConsequenceCodeValue.AddIssue or ContingencyCreateRequest.ConsequenceCodeValue.CancelQuote);
    }
}

internal sealed class UpdateContingencyValidator : AbstractValidator<UpdateContingency>
{
    public UpdateContingencyValidator()
    {
        RuleFor(c => c.Id).NotEqual(Guid.Empty);
        RuleFor(c => c.Request).Must(r => r.Description is not null || r.CustomerText is not null || r.Owner is not null).WithMessage("Give at least one of description, customerText or owner.");
        RuleFor(c => c.Request.Description).MaximumLength(1000).Must(t => t is null || !string.IsNullOrWhiteSpace(t));
        RuleFor(c => c.Request.CustomerText).MaximumLength(1000);
        RuleFor(c => c.Request.Owner).MaximumLength(200).Must(t => t is null || !string.IsNullOrWhiteSpace(t));
    }
}

internal sealed class SatisfyContingencyValidator : AbstractValidator<SatisfyContingency>
{
    public SatisfyContingencyValidator()
    {
        RuleFor(c => c.Request.ContingencyId).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.EvidenceRef).MaximumLength(200);
        RuleFor(c => c.Request.Note).MaximumLength(1000);
    }
}

internal sealed class WaiveContingencyValidator : AbstractValidator<WaiveContingency>
{
    public WaiveContingencyValidator()
    {
        RuleFor(c => c.Request.ContingencyId).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
    }
}

internal sealed class ExtendContingencyValidator : AbstractValidator<ExtendContingency>
{
    public ExtendContingencyValidator()
    {
        RuleFor(c => c.Request.ContingencyId).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
    }
}

internal sealed class ApplyContingencyConsequenceValidator : AbstractValidator<ApplyContingencyConsequence>
{
    public ApplyContingencyConsequenceValidator() => RuleFor(c => c.Request.ContingencyId).NotEqual(Guid.Empty);
}

/// <summary>Shared steps of the contingency commands: the legal entity, the JSON view, the human check and the three events.</summary>
internal sealed class ContingencySupport(ContingencyStore store, RequestContext context, ILegalEntityDirectory legalEntities, IEventPublisher events)
{
    public ContingencyStore Store => store;

    public string Me => context.Actor.ToString();

    public Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public DomainError? RequireHuman(string what) =>
        context.Actor.Kind == ActorKind.User ? null : DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", $"Only a person may {what}.");

    public static DomainError NotFound(Guid id) => DomainError.Of(ModuleCode.UW, "CONTINGENCY-NOT-FOUND", $"Contingency {id:D} was not found.");

    public static DomainError State(string message) => DomainError.Of(ModuleCode.UW, "CONTINGENCY-STATE", message);

    public static DomainError Stale(Guid id) =>
        new(ErrorCode.For(ModuleCode.UW, "STALE"), "The contingency changed since it was read; reload it and try again.")
        { Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["contingencyId"] = id.ToString("D") } };

    /// <summary>Loads and locks an Open contingency; the not-found or state error otherwise.</summary>
    public async Task<(ContingencyRecord? Row, DomainError? Error)> LockOpenAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(LegalEntity, id, forUpdate: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return (null, NotFound(id));
        }

        return row.Status == "Open" ? (row, null) : (null, State($"Contingency is {row.Status}; only an Open contingency can change."));
    }

    public async Task<JsonElement> ViewAsync(Guid id, CancellationToken cancellationToken) =>
        View((await store.GetAsync(LegalEntity, id, forUpdate: false, cancellationToken).ConfigureAwait(false))!, store.Today);

    public static ContingencyListItem Item(ContingencyRecord r, DateOnly today) => new()
    {
        Id = r.ContingencyId,
        TermRef = r.TermId,
        JobRef = r.JobId is { } job ? JobId.From(job) : null,
        ContingencyType = r.ContingencyType,
        Description = r.Description,
        CustomerText = r.CustomerText,
        DueDate = new BusinessDate(r.Due),
        Owner = r.Owner,
        BlockingPoint = r.BlockingPoint switch
        {
            "PRE_QUOTE" => BlockingPoint.PreQuote,
            "PRE_BIND" => BlockingPoint.PreBind,
            "NON_BLOCKING" => BlockingPoint.NonBlocking,
            _ => BlockingPoint.PreIssue,
        },
        Status = r.Status,
        Overdue = r.Status == "Open" && r.Due < today,
        ConsequenceCode = r.ConsequenceCode,
        RecordVersion = r.RecordVersion,
    };

    public static JsonElement View(ContingencyRecord r, DateOnly today) => JsonSerializer.SerializeToElement(Item(r, today), SharedKernelJson.Options);

    public static BusinessKeys Keys(Guid termId, Guid contingencyId) =>
        BusinessKeys.Empty.With("policyTermId", termId.ToString("D")).With("contingencyId", contingencyId.ToString("D"));

    public void PublishCreated(ContingencyRecord r) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ContingencyCreatedV1.Descriptor), "PolicyTerm", r.TermId.ToString("D"),
            new ContingencyCreatedV1
            {
                ContingencyId = r.ContingencyId,
                TermId = PolicyTermId.From(r.TermId),
                ContingencyType = r.ContingencyType,
                DueDate = new BusinessDate(r.Due),
                Owner = r.Owner,
            },
            Keys(r.TermId, r.ContingencyId)));

    public void PublishOverdue(ContingencyRecord r) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ContingencyOverdueV1.Descriptor), "PolicyTerm", r.TermId.ToString("D"),
            new ContingencyOverdueV1
            {
                ContingencyId = r.ContingencyId,
                TermId = PolicyTermId.From(r.TermId),
                DueDate = new BusinessDate(r.Due),
                ConsequenceCode = r.ConsequenceCode,
            },
            Keys(r.TermId, r.ContingencyId)));

    public void PublishResolved(ContingencyRecord r, ContingencyResolvedV1.OutcomeValue outcome, Guid? consequenceJob) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ContingencyResolvedV1.Descriptor), "PolicyTerm", r.TermId.ToString("D"),
            new ContingencyResolvedV1
            {
                ContingencyId = r.ContingencyId,
                Outcome = outcome,
                ConsequenceJobId = consequenceJob is { } job ? JobId.From(job) : null,
            },
            Keys(r.TermId, r.ContingencyId)));
}

/// <summary>
/// Creates an Open contingency; the due date is today or later. With a job and a blocking point (default PRE_ISSUE) it gates
/// <c>uw.Issue.blockingStatus</c> until it is satisfied or waived. <c>ContingencyCreated</c> is published in the same transaction.
/// </summary>
internal sealed class CreateContingencyHandler(ContingencySupport support, IssueTypeStore issueTypes) : ICommandHandler<CreateContingency, ContingencyCreateResponse>
{
    public async Task<Result<ContingencyCreateResponse>> HandleAsync(CreateContingency command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.DueDate.Value < support.Store.Today)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "dueDate cannot be in the past.");
        }

        var consequence = ContingencyInput.Consequence(request.ConsequenceCode);
        if (consequence == "ADD_ISSUE" && await issueTypes.GetAsync(request.ConsequenceIssueType!, cancellationToken).ConfigureAwait(false) is not { Active: true })
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", $"consequenceIssueType {request.ConsequenceIssueType} is not an active issue type.");
        }

        var row = new ContingencyRecord
        {
            ContingencyId = Guid.CreateVersion7(),
            LegalEntityId = support.LegalEntity,
            TermId = request.TermRef,
            JobId = request.JobRef?.Value,
            ContingencyType = request.ContingencyType,
            Description = request.Description.Trim(),
            CustomerText = ContingencyInput.Blank(request.CustomerText),
            DueDate = request.DueDate.Value.ToDateTime(TimeOnly.MinValue),
            Owner = request.Owner.Trim(),
            BlockingPoint = ContingencyInput.Point(request.BlockingPoint),
            ConsequenceCode = consequence,
            ConsequenceIssueType = consequence == "ADD_ISSUE" ? request.ConsequenceIssueType : null,
            Status = "Open",
            CreatedAt = support.Store.Now,
            CreatedBy = support.Me,
            RecordVersion = 1,
        };
        await support.Store.InsertAsync(row, cancellationToken).ConfigureAwait(false);
        support.PublishCreated(row);
        return new ContingencyCreateResponse { Contingency = ContingencySupport.View(row, support.Store.Today) };
    }
}

internal sealed class UpdateContingencyHandler(ContingencySupport support) : ICommandHandler<UpdateContingency, ContingencyUpdateResponse>
{
    public async Task<Result<ContingencyUpdateResponse>> HandleAsync(UpdateContingency command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var (row, error) = await support.LockOpenAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        if (request.RecordVersion is { } expected && expected != row.RecordVersion)
        {
            return ContingencySupport.Stale(command.Id);
        }

        await support.Store.UpdateTextAsync(
            command.Id, ContingencyInput.Blank(request.Description), ContingencyInput.Blank(request.CustomerText), ContingencyInput.Blank(request.Owner), cancellationToken).ConfigureAwait(false);
        return new ContingencyUpdateResponse { Contingency = await support.ViewAsync(command.Id, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class SatisfyContingencyHandler(ContingencySupport support) : ICommandHandler<SatisfyContingency, ContingencySatisfyResponse>
{
    public async Task<Result<ContingencySatisfyResponse>> HandleAsync(SatisfyContingency command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var (row, error) = await support.LockOpenAsync(request.ContingencyId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        await support.Store.ResolveAsync(row.ContingencyId, "Satisfied", support.Me, ContingencyInput.Blank(request.Note), ContingencyInput.Blank(request.EvidenceRef), cancellationToken).ConfigureAwait(false);
        support.PublishResolved(row, ContingencyResolvedV1.OutcomeValue.Satisfied, null);
        return new ContingencySatisfyResponse { Contingency = await support.ViewAsync(row.ContingencyId, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class WaiveContingencyHandler(ContingencySupport support) : ICommandHandler<WaiveContingency, ContingencyWaiveResponse>
{
    public async Task<Result<ContingencyWaiveResponse>> HandleAsync(WaiveContingency command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman("waive a contingency") is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var (row, error) = await support.LockOpenAsync(request.ContingencyId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        await support.Store.ResolveAsync(row.ContingencyId, "Waived", support.Me, request.Reason.Trim(), null, cancellationToken).ConfigureAwait(false);
        support.PublishResolved(row, ContingencyResolvedV1.OutcomeValue.Waived, null);
        return new ContingencyWaiveResponse { Contingency = await support.ViewAsync(row.ContingencyId, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ExtendContingencyHandler(ContingencySupport support) : ICommandHandler<ExtendContingency, ContingencyExtendResponse>
{
    public async Task<Result<ContingencyExtendResponse>> HandleAsync(ExtendContingency command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman("extend a contingency") is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var (row, error) = await support.LockOpenAsync(request.ContingencyId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        var newDue = request.NewDueDate.Value;
        if (newDue <= row.Due || newDue < support.Store.Today)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "newDueDate must be after the current due date and not in the past.");
        }

        // TODO(hardening): keep the extension history (old date, reason, who) on its own table; today it is on the audit record only.
        await support.Store.ExtendAsync(row.ContingencyId, newDue, cancellationToken).ConfigureAwait(false);
        return new ContingencyExtendResponse { Contingency = await support.ViewAsync(row.ContingencyId, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Applies the consequence of an overdue Open contingency. ADD_ISSUE raises a manual issue of the named type on the job (blocking its
/// point, like <c>uw.Issue.addManual</c>); CANCEL_QUOTE records the outcome and publishes <c>ContingencyResolved</c> with the job, which POL
/// acts on. Either way the contingency ends as ConsequenceApplied and stops blocking.
/// </summary>
internal sealed class ApplyContingencyConsequenceHandler(ContingencySupport support, IssueLifecycleSupport issues, IssueTypeStore issueTypes)
    : ICommandHandler<ApplyContingencyConsequence, ContingencyApplyConsequenceResponse>
{
    public async Task<Result<ContingencyApplyConsequenceResponse>> HandleAsync(ApplyContingencyConsequence command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman("apply the consequence of a contingency") is { } notHuman)
        {
            return notHuman;
        }

        var (row, error) = await support.LockOpenAsync(command.Request.ContingencyId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        if (row.Due >= support.Store.Today)
        {
            return ContingencySupport.State($"The contingency is due on {row.Due:yyyy-MM-dd}; its consequence applies once it is overdue.");
        }

        if (row.ConsequenceCode == "NONE" || row.JobId is not { } jobId)
        {
            return ContingencySupport.State("The contingency has no consequence to apply.");
        }

        if (row.ConsequenceCode == "ADD_ISSUE")
        {
            if (await issueTypes.GetAsync(row.ConsequenceIssueType!, cancellationToken).ConfigureAwait(false) is not { Active: true } type)
            {
                return ContingencySupport.State($"Issue type {row.ConsequenceIssueType} is no longer an active type of the catalogue.");
            }

            await issues.Store.LockJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (await issues.Lifecycle.LatestEvaluationAsync(issues.LegalEntity, jobId, cancellationToken).ConfigureAwait(false) is not { } evaluationId)
            {
                return ContingencySupport.State("The job has not been evaluated by underwriting yet, so no issue can be added to it.");
            }

            var issueId = Guid.CreateVersion7();
            var key = $"MANUAL:{type.Code}:{issueId:N}";
            var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
            await issues.Store.InsertIssueAsync(
                issueId, issues.LegalEntity, jobId, type.Code, key, type.BlockingPoint, type.Severity, type.DefaultLane, "MANUAL", type.NameEn, type.NameEl, evaluationId, fingerprint, null,
                cancellationToken).ConfigureAwait(false);
            issues.PublishRaised(jobId, issueId, type.Code, key, DecideIssuesHandler.Point(type.BlockingPoint), type.Severity, type.DefaultLane, reopened: false);
        }

        await support.Store.ResolveAsync(row.ContingencyId, "ConsequenceApplied", support.Me, row.ConsequenceCode, null, cancellationToken).ConfigureAwait(false);
        support.PublishResolved(row, ContingencyResolvedV1.OutcomeValue.ConsequenceApplied, jobId);
        return new ContingencyApplyConsequenceResponse { Contingency = await support.ViewAsync(row.ContingencyId, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// The due-date job: every Open contingency past its due date, not marked yet, gets its overdue mark and one <c>ContingencyOverdue</c>
/// event. Returns how many were marked; a second run finds none, so the job is safe to repeat.
/// </summary>
internal sealed class SweepContingenciesHandler(ContingencySupport support) : ICommandHandler<SweepContingencies, int>
{
    public async Task<Result<int>> HandleAsync(SweepContingencies command, CancellationToken cancellationToken)
    {
        var due = await support.Store.DueAsync(support.LegalEntity, support.Store.Today, cancellationToken).ConfigureAwait(false);
        foreach (var row in due)
        {
            await support.Store.MarkOverdueAsync(row.ContingencyId, cancellationToken).ConfigureAwait(false);
            support.PublishOverdue(row);
        }

        return due.Count;
    }
}

/// <summary>Audit facts of the contingency commands: the contingency, the action and the reason given.</summary>
internal sealed class ContingencyAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result)
    {
        Guid? id = command switch
        {
            UpdateContingency c => c.Id,
            SatisfyContingency c => c.Request.ContingencyId,
            WaiveContingency c => c.Request.ContingencyId,
            ExtendContingency c => c.Request.ContingencyId,
            ApplyContingencyConsequence c => c.Request.ContingencyId,
            _ => null,
        };
        JsonElement? view = result is { IsSuccess: true } ok
            ? ok.Value switch
            {
                ContingencyCreateResponse r => r.Contingency,
                ContingencyUpdateResponse r => r.Contingency,
                ContingencySatisfyResponse r => r.Contingency,
                ContingencyWaiveResponse r => r.Contingency,
                ContingencyExtendResponse r => r.Contingency,
                ContingencyApplyConsequenceResponse r => r.Contingency,
                _ => null,
            }
            : null;
        if (view is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("id", out var idProperty) && idProperty.TryGetGuid(out var known))
        {
            id = known;
        }

        var action = command switch
        {
            CreateContingency => "CREATE",
            UpdateContingency => "UPDATE",
            SatisfyContingency => "SATISFY",
            WaiveContingency => "WAIVE",
            ExtendContingency => "EXTEND",
            _ => "APPLY_CONSEQUENCE",
        };
        var reason = command switch
        {
            WaiveContingency c => c.Request.Reason,
            ExtendContingency c => $"{c.Request.NewDueDate.Value:yyyy-MM-dd}: {c.Request.Reason}",
            SatisfyContingency c => c.Request.Note,
            _ => null,
        };
        string? status = view is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("status", out var st) ? st.GetString() : null;
        return new CommandAuditFacts
        {
            ObjectRef = id is { } oid ? new ObjectRef(ModuleCode.UW, "Contingency", oid.ToString("D")) : null,
            BusinessKeys = id is { } key ? BusinessKeys.Empty.With("contingencyId", key.ToString("D")) : BusinessKeys.Empty,
            Changes = Platform.Audit.AuditDiff.Compute(new { }, new { action, reason, status }),
        };
    }
}
