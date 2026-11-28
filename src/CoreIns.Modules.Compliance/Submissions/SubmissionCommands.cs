using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform;
using CoreIns.Platform.Approvals;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Submissions;

internal sealed record CreateSubmission(SubmissionCreateRequest Request) : ICommand<SubmissionCreateResponse>;

internal sealed record AttachSubmissionContent(SubmissionAttachContentRequest Request) : ICommand<SubmissionAttachContentResponse>;

internal sealed record CommentSubmission(SubmissionCommentRequest Request) : ICommand<SubmissionCommentResponse>;

internal sealed record ApproveSubmission(SubmissionApproveRequest Request) : ICommand<SubmissionApproveResponse>;

internal sealed record RecordSubmissionFiling(SubmissionRecordFilingRequest Request) : ICommand<SubmissionRecordFilingResponse>;

internal sealed record RecordSubmissionResponse(SubmissionRecordResponseRequest Request) : ICommand<SubmissionRecordResponseResponse>;

internal sealed record ExportSubmissionEvidencePack(SubmissionEvidencePackRequest Request) : ICommand<SubmissionEvidencePackResponse>;

internal sealed class CreateSubmissionValidator : AbstractValidator<CreateSubmission>
{
    public CreateSubmissionValidator()
    {
        RuleFor(c => c.Request.SubmissionType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Regulator).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.PeriodLabel).NotEmpty().MaximumLength(32);
        RuleFor(c => c.Request.Note).MaximumLength(1000);
    }
}

internal sealed class AttachSubmissionContentValidator : AbstractValidator<AttachSubmissionContent>
{
    public AttachSubmissionContentValidator()
    {
        RuleFor(c => c.Request.RefId).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.Label).MaximumLength(200);
    }
}

internal sealed class CommentSubmissionValidator : AbstractValidator<CommentSubmission>
{
    public CommentSubmissionValidator() => RuleFor(c => c.Request.Text).NotEmpty().MaximumLength(2000);
}

internal sealed class ApproveSubmissionValidator : AbstractValidator<ApproveSubmission>
{
    public ApproveSubmissionValidator() => RuleFor(c => c.Request.Comment).MaximumLength(512);
}

internal sealed class RecordSubmissionFilingValidator : AbstractValidator<RecordSubmissionFiling>
{
    public RecordSubmissionFilingValidator()
    {
        RuleFor(c => c.Request.Channel).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.FilingReference).NotEmpty().MaximumLength(128);
        RuleFor(c => c.Request.Filer).MaximumLength(128);
    }
}

internal sealed class RecordSubmissionResponseValidator : AbstractValidator<RecordSubmissionResponse>
{
    public RecordSubmissionResponseValidator()
    {
        RuleFor(c => c.Request.Reference).MaximumLength(128);
        RuleFor(c => c.Request.Note).MaximumLength(1000);
    }
}

internal sealed class ExportSubmissionEvidencePackValidator : AbstractValidator<ExportSubmissionEvidencePack>
{
    public ExportSubmissionEvidencePackValidator()
    {
        RuleFor(c => c.Request.Subject).MaximumLength(256);
        RuleFor(c => c.Request.LegalClaimReason).MaximumLength(500);
    }
}

internal sealed class CreateSubmissionHandler(SubmissionEngine engine) : ICommandHandler<CreateSubmission, SubmissionCreateResponse>
{
    public async Task<Result<SubmissionCreateResponse>> HandleAsync(CreateSubmission command, CancellationToken cancellationToken) =>
        (await engine.CreateAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(v => new SubmissionCreateResponse { Submission = v });
}

internal sealed class AttachSubmissionContentHandler(SubmissionEngine engine) : ICommandHandler<AttachSubmissionContent, SubmissionAttachContentResponse>
{
    public async Task<Result<SubmissionAttachContentResponse>> HandleAsync(AttachSubmissionContent command, CancellationToken cancellationToken) =>
        (await engine.AttachContentAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(v => new SubmissionAttachContentResponse { Submission = v });
}

internal sealed class CommentSubmissionHandler(SubmissionEngine engine) : ICommandHandler<CommentSubmission, SubmissionCommentResponse>
{
    public async Task<Result<SubmissionCommentResponse>> HandleAsync(CommentSubmission command, CancellationToken cancellationToken) =>
        (await engine.CommentAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(v => new SubmissionCommentResponse { Submission = v });
}

internal sealed class ApproveSubmissionHandler(SubmissionEngine engine) : ICommandHandler<ApproveSubmission, SubmissionApproveResponse>
{
    public async Task<Result<SubmissionApproveResponse>> HandleAsync(ApproveSubmission command, CancellationToken cancellationToken) =>
        (await engine.ApproveAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(v => new SubmissionApproveResponse { Submission = v });
}

internal sealed class RecordSubmissionFilingHandler(SubmissionEngine engine) : ICommandHandler<RecordSubmissionFiling, SubmissionRecordFilingResponse>
{
    public async Task<Result<SubmissionRecordFilingResponse>> HandleAsync(RecordSubmissionFiling command, CancellationToken cancellationToken) =>
        (await engine.RecordFilingAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(v => new SubmissionRecordFilingResponse { Submission = v });
}

internal sealed class RecordSubmissionResponseHandler(SubmissionEngine engine) : ICommandHandler<RecordSubmissionResponse, SubmissionRecordResponseResponse>
{
    public async Task<Result<SubmissionRecordResponseResponse>> HandleAsync(RecordSubmissionResponse command, CancellationToken cancellationToken) =>
        (await engine.RecordResponseAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(v => new SubmissionRecordResponseResponse { Submission = v });
}

internal sealed class ExportSubmissionEvidencePackHandler(SubmissionEngine engine) : ICommandHandler<ExportSubmissionEvidencePack, SubmissionEvidencePackResponse>
{
    public async Task<Result<SubmissionEvidencePackResponse>> HandleAsync(ExportSubmissionEvidencePack command, CancellationToken cancellationToken)
    {
        CoreIns.Modules.Documents.Contracts.Api.EvidencePackExportResponse? pack = null;
        var result = await engine.EvidencePackAsync(command.Request, cancellationToken, p => pack = p).ConfigureAwait(false);
        return result.IsFailure
            ? result.Error!
            : new SubmissionEvidencePackResponse
            {
                Submission = result.Value,
                ArchiveId = pack!.ArchiveId,
                Hash = pack.Hash,
                DocumentCount = pack.DocumentCount,
                ExcludedCount = pack.ExcludedCount,
            };
    }
}

/// <summary>Audit facts of the submission commands: the submission, its number and the status change (audit on every transition).</summary>
internal static class SubmissionAudit
{
    public static CommandAuditFacts Facts(Guid id, SubmissionView? after, string action)
    {
        var subject = new ObjectRef(ModuleCode.CMP, "Submission", id.ToString("D"));
        var keys = BusinessKeys.Empty.With("submissionId", id.ToString("D"));
        return after is null
            ? new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys }
            : new CommandAuditFacts
            {
                ObjectRef = subject,
                ObjectNumber = after.Number,
                BusinessKeys = keys.With("submissionStatus", after.Status.ToString()),
                Changes = AuditDiff.Compute(new { action }, new { action, status = after.Status.ToString(), approval = after.ApprovalStatus.ToString(), version = after.Version }),
            };
    }
}

internal sealed class CreateSubmissionAuditor : ICommandAuditor<CreateSubmission, SubmissionCreateResponse>
{
    public CommandAuditFacts Describe(CreateSubmission command, Result<SubmissionCreateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? SubmissionAudit.Facts(ok.Value.Submission.SubmissionId, ok.Value.Submission, "create")
            : SubmissionAudit.Facts(command.Request.ResubmitOf ?? Guid.Empty, null, "create");
}

internal sealed class AttachSubmissionContentAuditor : ICommandAuditor<AttachSubmissionContent, SubmissionAttachContentResponse>
{
    public CommandAuditFacts Describe(AttachSubmissionContent command, Result<SubmissionAttachContentResponse>? result) =>
        SubmissionAudit.Facts(command.Request.Id, result is { IsSuccess: true } ok ? ok.Value.Submission : null, "attachContent");
}

internal sealed class CommentSubmissionAuditor : ICommandAuditor<CommentSubmission, SubmissionCommentResponse>
{
    public CommandAuditFacts Describe(CommentSubmission command, Result<SubmissionCommentResponse>? result) =>
        SubmissionAudit.Facts(command.Request.Id, result is { IsSuccess: true } ok ? ok.Value.Submission : null, "comment");
}

internal sealed class ApproveSubmissionAuditor : ICommandAuditor<ApproveSubmission, SubmissionApproveResponse>
{
    public CommandAuditFacts Describe(ApproveSubmission command, Result<SubmissionApproveResponse>? result) =>
        SubmissionAudit.Facts(command.Request.Id, result is { IsSuccess: true } ok ? ok.Value.Submission : null, "approve:" + command.Request.Decision);
}

internal sealed class RecordSubmissionFilingAuditor : ICommandAuditor<RecordSubmissionFiling, SubmissionRecordFilingResponse>
{
    public CommandAuditFacts Describe(RecordSubmissionFiling command, Result<SubmissionRecordFilingResponse>? result) =>
        SubmissionAudit.Facts(command.Request.SubmissionId, result is { IsSuccess: true } ok ? ok.Value.Submission : null, "recordFiling");
}

internal sealed class RecordSubmissionResponseAuditor : ICommandAuditor<RecordSubmissionResponse, SubmissionRecordResponseResponse>
{
    public CommandAuditFacts Describe(RecordSubmissionResponse command, Result<SubmissionRecordResponseResponse>? result) =>
        SubmissionAudit.Facts(command.Request.Id, result is { IsSuccess: true } ok ? ok.Value.Submission : null, "recordResponse");
}

internal sealed class ExportSubmissionEvidencePackAuditor : ICommandAuditor<ExportSubmissionEvidencePack, SubmissionEvidencePackResponse>
{
    public CommandAuditFacts Describe(ExportSubmissionEvidencePack command, Result<SubmissionEvidencePackResponse>? result) =>
        SubmissionAudit.Facts(command.Request.Id, result is { IsSuccess: true } ok ? ok.Value.Submission : null, "evidencePack");
}

/// <summary>Registration of the submission tracker (called once from <c>ComplianceModule</c>).</summary>
internal static class SubmissionRegistration
{
    public static IServiceCollection AddSubmissions(this IServiceCollection services)
    {
        services.AddScoped<SubmissionEngine>();
        services.AddAuthorityType(SubmissionApproval.Definition);
        services.AddOwnerDecidedApprovalType(SubmissionApproval.Type);

        services.AddScoped<IValidator<CreateSubmission>, CreateSubmissionValidator>();
        services.AddScoped<IValidator<AttachSubmissionContent>, AttachSubmissionContentValidator>();
        services.AddScoped<IValidator<CommentSubmission>, CommentSubmissionValidator>();
        services.AddScoped<IValidator<ApproveSubmission>, ApproveSubmissionValidator>();
        services.AddScoped<IValidator<RecordSubmissionFiling>, RecordSubmissionFilingValidator>();
        services.AddScoped<IValidator<RecordSubmissionResponse>, RecordSubmissionResponseValidator>();
        services.AddScoped<IValidator<ExportSubmissionEvidencePack>, ExportSubmissionEvidencePackValidator>();

        services.AddCommandAuditor<CreateSubmission, SubmissionCreateResponse, CreateSubmissionAuditor>();
        services.AddCommandAuditor<AttachSubmissionContent, SubmissionAttachContentResponse, AttachSubmissionContentAuditor>();
        services.AddCommandAuditor<CommentSubmission, SubmissionCommentResponse, CommentSubmissionAuditor>();
        services.AddCommandAuditor<ApproveSubmission, SubmissionApproveResponse, ApproveSubmissionAuditor>();
        services.AddCommandAuditor<RecordSubmissionFiling, SubmissionRecordFilingResponse, RecordSubmissionFilingAuditor>();
        services.AddCommandAuditor<RecordSubmissionResponse, SubmissionRecordResponseResponse, RecordSubmissionResponseAuditor>();
        services.AddCommandAuditor<ExportSubmissionEvidencePack, SubmissionEvidencePackResponse, ExportSubmissionEvidencePackAuditor>();

        services.AddCommand<CreateSubmission, SubmissionCreateResponse, CreateSubmissionHandler>(CommandDescriptor.For("cmp.Submission.create"));
        services.AddCommand<AttachSubmissionContent, SubmissionAttachContentResponse, AttachSubmissionContentHandler>(CommandDescriptor.For("cmp.Submission.attachContent"));
        services.AddCommand<CommentSubmission, SubmissionCommentResponse, CommentSubmissionHandler>(CommandDescriptor.For("cmp.Submission.comment"));
        services.AddCommand<ApproveSubmission, SubmissionApproveResponse, ApproveSubmissionHandler>(CommandDescriptor.For("cmp.Submission.approve"));
        services.AddCommand<RecordSubmissionFiling, SubmissionRecordFilingResponse, RecordSubmissionFilingHandler>(CommandDescriptor.For("cmp.Submission.recordFiling"));
        services.AddCommand<RecordSubmissionResponse, SubmissionRecordResponseResponse, RecordSubmissionResponseHandler>(CommandDescriptor.For("cmp.Submission.recordResponse"));
        services.AddCommand<ExportSubmissionEvidencePack, SubmissionEvidencePackResponse, ExportSubmissionEvidencePackHandler>(CommandDescriptor.For("cmp.Submission.evidencePack"));
        services.AddScoped<IComplianceSubmissionService, ComplianceSubmissionService>();
        return services;
    }
}

/// <summary>The in-process <see cref="IComplianceSubmissionService"/> (D-ARC-16): FIN, PLT and RAT register and file through it.</summary>
internal sealed class ComplianceSubmissionService(
    RequestContext context,
    ICommandHandler<CreateSubmission, SubmissionCreateResponse> create,
    ICommandHandler<RecordSubmissionFiling, SubmissionRecordFilingResponse> filing) : IComplianceSubmissionService
{
    public async Task<SubmissionCreateResponse> CreateAsync(SubmissionCreateRequest request, CoreIns.Platform.Contracts.CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await create.HandleAsync(new CreateSubmission(request), cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<SubmissionRecordFilingResponse> RecordFilingAsync(SubmissionRecordFilingRequest request, CoreIns.Platform.Contracts.CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await filing.HandleAsync(new RecordSubmissionFiling(request), cancellationToken).ConfigureAwait(false));
        }
    }

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new CoreIns.Platform.Errors.DomainException(result.Error!);
}
