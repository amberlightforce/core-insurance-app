using System.Text.Json;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using DomainIntermediaryStatus = CoreIns.Modules.Party.Domain.IntermediaryStatus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.CommissionAgreement.createVersion</c> (BO-PTY-10).</summary>
internal sealed record CreateCommissionVersion(CommissionAgreementCreateVersionRequest Request) : ICommand<CommissionAgreementCreateVersionResponse>;

/// <summary><c>pty.CommissionAgreement.submit</c>: DRAFT to PENDING_APPROVAL, opening the PLT approval request.</summary>
internal sealed record SubmitCommissionVersion(CommissionAgreementSubmitRequest Request) : ICommand<CommissionAgreementSubmitResponse>;

/// <summary><c>pty.CommissionAgreement.approve</c>: the checker decides the PLT request; the version becomes ACTIVE or REJECTED.</summary>
internal sealed record ApproveCommissionVersion(CommissionAgreementApproveRequest Request) : ICommand<CommissionAgreementApproveResponse>;

internal sealed class CreateCommissionVersionValidator : AbstractValidator<CreateCommissionVersion>
{
    public CreateCommissionVersionValidator()
    {
        RuleFor(c => c.Request.IntermediaryId.Value).NotEmpty();
        RuleFor(c => c.Request.ProductCode).MaximumLength(100);
        RuleFor(c => c.Request.LineOfBusiness).MaximumLength(100);
        RuleFor(c => c.Request.Notes).MaximumLength(1000);
        RuleFor(c => c.Request.RateLines).NotEmpty().Must(l => l.Count <= 50);
        RuleFor(c => c.Request.ExpectedAnnualVolume).Must(m => m.Amount >= 0m && m.Currency.Code == "EUR")
            .WithMessage("The expected annual volume must be zero or more, in EUR.");
    }
}

internal sealed class SubmitCommissionVersionValidator : AbstractValidator<SubmitCommissionVersion>
{
    public SubmitCommissionVersionValidator()
    {
        RuleFor(c => c.Request.VersionId).NotEmpty();
        RuleFor(c => c.Request.Reason).MaximumLength(500);
    }
}

internal sealed class ApproveCommissionVersionValidator : AbstractValidator<ApproveCommissionVersion>
{
    public ApproveCommissionVersionValidator()
    {
        RuleFor(c => c.Request.VersionId).NotEmpty();
        RuleFor(c => c.Request.Comment).MaximumLength(500);
        RuleFor(c => c.Request.Comment).NotEmpty().When(c => c.Request.Decision == CommissionAgreementApproveRequest.DecisionValue.Reject).WithMessage("A rejection needs a comment.");
    }
}

/// <summary>Shared checks and the PLT binding of the commission agreement commands.</summary>
internal static class CommissionSupport
{
    public static DomainError NotFound() => DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The commission agreement or version does not exist.");

    public static DomainError State(string detail) => DomainError.Of(ModuleCode.PTY, "STATE", detail);

    public static DomainError Stale(string detail) => DomainError.Of(ModuleCode.PTY, "STALE", detail);

    /// <summary>A suspended or terminated intermediary cannot get a new active agreement (REQ-PTY-198).</summary>
    public static DomainError? CheckIntermediary(IntermediaryRow? intermediary)
    {
        if (intermediary is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The intermediary does not exist.");
        }

        var status = Codes.Parse<DomainIntermediaryStatus>(intermediary.Status);
        return status is DomainIntermediaryStatus.Suspended or DomainIntermediaryStatus.Terminated
            ? DomainError.Of(ModuleCode.PTY, "INTERMEDIARY-NOT-ACTIVE", "The intermediary is suspended or terminated; it cannot get a new agreement.")
            : null;
    }

    /// <summary>No backdating: the version must take effect after the settled date.</summary>
    public static DomainError? CheckBackdate(BusinessDate effectiveFrom, CommissionOptions options, BusinessDate today)
    {
        var settled = CommissionRules.SettledThrough(options, today);
        return effectiveFrom > settled
            ? null
            : DomainError.Of(ModuleCode.PTY, "BACKDATE", $"A version cannot take effect on or before the settled date {settled}.");
    }

    public static Sha256Hash HashOf(CommissionAgreementRow agreement, CommissionAgreementVersionRow version, IEnumerable<CommissionRateLineRow> lines) =>
        CommissionRules.Hash(
            version.VersionId, agreement.IntermediaryId.Value, agreement.ScopeKey, version.EffectiveFrom, version.ExpectedVolume, version.Currency,
            lines.Select(l => (l.BusinessType, l.LineOfBusiness, l.Basis, l.RatePct)));
}

internal sealed class CreateCommissionVersionHandler(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, IOptions<CommissionOptions> options, CommissionAgreementQueries queries)
    : ICommandHandler<CreateCommissionVersion, CommissionAgreementCreateVersionResponse>
{
    public async Task<Result<CommissionAgreementCreateVersionResponse>> HandleAsync(CreateCommissionVersion command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var intermediary = await db.Intermediaries.SingleOrDefaultAsync(i => i.IntermediaryId == request.IntermediaryId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (CommissionSupport.CheckIntermediary(intermediary) is { } refused)
        {
            return refused;
        }

        if (CommissionRules.CheckLines(request.RateLines, options.Value) is { } badLines)
        {
            return badLines;
        }

        var now = clock.Now;
        if (CommissionSupport.CheckBackdate(request.EffectiveFrom, options.Value, RoleSupport.Today(clock)) is { } backdated)
        {
            return backdated;
        }

        var scope = CommissionRules.ScopeKey(request.ProductCode, request.LineOfBusiness);
        var actor = context.Actor.ToString();
        var agreement = await db.CommissionAgreements
            .SingleOrDefaultAsync(a => a.LegalEntityId == legalEntity && a.IntermediaryId == request.IntermediaryId && a.ScopeKey == scope, cancellationToken).ConfigureAwait(false);
        var versions = agreement is null
            ? []
            : await db.CommissionVersions.Where(v => v.AgreementId == agreement.AgreementId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versions.Any(v => v.Status is CommissionStatus.Draft or CommissionStatus.PendingApproval))
        {
            return CommissionSupport.State("The agreement already has an open version; submit and decide it, or wait for its decision, first.");
        }

        var latest = versions.Where(v => v.Status == CommissionStatus.Active).Select(v => (BusinessDate?)v.EffectiveFrom).Max();
        if (latest is { } last && request.EffectiveFrom <= last)
        {
            return DomainError.Of(ModuleCode.PTY, "BACKDATE", $"A new version must take effect after the latest active version ({last}).");
        }

        if (agreement is null)
        {
            agreement = new CommissionAgreementRow
            {
                AgreementId = EntityIds.NewGuid(),
                LegalEntityId = legalEntity,
                IntermediaryId = request.IntermediaryId,
                ProductCode = request.ProductCode,
                LineOfBusiness = request.LineOfBusiness,
                ScopeKey = scope,
                CreatedAt = now,
                CreatedBy = actor,
            };
            db.CommissionAgreements.Add(agreement);
        }

        var version = new CommissionAgreementVersionRow
        {
            VersionId = EntityIds.NewGuid(),
            AgreementId = agreement.AgreementId,
            LegalEntityId = legalEntity,
            VersionNo = versions.Count == 0 ? 1 : versions.Max(v => v.VersionNo) + 1,
            Status = CommissionStatus.Draft,
            EffectiveFrom = request.EffectiveFrom,
            ExpectedVolume = request.ExpectedAnnualVolume.Amount,
            Currency = request.ExpectedAnnualVolume.Currency.Code,
            MaxRatePct = request.RateLines.Max(l => l.RatePct),
            Notes = request.Notes,
            CreatedAt = now,
            CreatedBy = actor,
        };
        var lines = request.RateLines.Select((l, i) => new CommissionRateLineRow
        {
            LineId = EntityIds.NewGuid(),
            VersionId = version.VersionId,
            LegalEntityId = legalEntity,
            LineNo = i + 1,
            BusinessType = Codes.Of(l.BusinessType),
            LineOfBusiness = l.LineOfBusiness,
            Basis = Codes.Of(l.Basis),
            RatePct = l.RatePct,
        }).ToList();
        version.ContentHash = CommissionSupport.HashOf(agreement, version, lines).Value;
        db.CommissionVersions.Add(version);
        db.CommissionRateLines.AddRange(lines);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return CommissionSupport.State("The agreement changed meanwhile; reload it and try again.");
        }

        return new CommissionAgreementCreateVersionResponse
        {
            Agreement = (await queries.AgreementAsync(legalEntity, agreement.AgreementId, cancellationToken).ConfigureAwait(false))!,
            Version = await queries.VersionViewAsync(version, cancellationToken).ConfigureAwait(false),
        };
    }
}

internal sealed class SubmitCommissionVersionHandler(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, IOptions<CommissionOptions> options,
    CommissionAgreementQueries queries, IPlatformApprovalService approvals)
    : ICommandHandler<SubmitCommissionVersion, CommissionAgreementSubmitResponse>
{
    public async Task<Result<CommissionAgreementSubmitResponse>> HandleAsync(SubmitCommissionVersion command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var version = await db.CommissionVersions.SingleOrDefaultAsync(v => v.VersionId == command.Request.VersionId && v.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return CommissionSupport.NotFound();
        }

        if (version.Status != CommissionStatus.Draft)
        {
            return CommissionSupport.State($"Only a draft can be submitted; the version is {version.Status}.");
        }

        var agreement = await db.CommissionAgreements.SingleAsync(a => a.AgreementId == version.AgreementId, cancellationToken).ConfigureAwait(false);
        var intermediary = await db.Intermediaries.SingleOrDefaultAsync(i => i.IntermediaryId == agreement.IntermediaryId, cancellationToken).ConfigureAwait(false);
        if (CommissionSupport.CheckIntermediary(intermediary) is { } refused)
        {
            return refused;
        }

        var now = clock.Now;
        if (CommissionSupport.CheckBackdate(version.EffectiveFrom, options.Value, RoleSupport.Today(clock)) is { } backdated)
        {
            return backdated;
        }

        var lines = await db.CommissionRateLines.Where(l => l.VersionId == version.VersionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hash = CommissionSupport.HashOf(agreement, version, lines);
        if (hash.Value != version.ContentHash)
        {
            return CommissionSupport.Stale("The stored version no longer matches its content hash.");
        }

        var actor = context.Actor.ToString();
        var band = CommissionRules.Band(version.MaxRatePct, options.Value.StandardRatePct);
        var response = await approvals.RequestAsync(
            new ApprovalRequestRequest
            {
                Type = PartyAuthorityTypes.CommissionApprovalType,
                ObjectRef = new ObjectRef(ModuleCode.PTY, "CommissionAgreementVersion", version.VersionId.ToString("D")),
                PayloadHash = hash,
                Authority = new ApprovalAuthority
                {
                    Type = PartyAuthorityTypes.CommissionAgreement.Value,
                    Amount = new Money(version.ExpectedVolume, Currency.FromCode(version.Currency)),
                    Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [PartyAuthorityTypes.RateBandDimension] = band },
                },
                ReferralRole = "Staff.UnderwritingManager",
                Editors = string.Equals(version.CreatedBy, actor, StringComparison.Ordinal) ? [] : [version.CreatedBy],
                Reason = command.Request.Reason ?? "Commission agreement version " + version.VersionNo,
                Diff = JsonSerializer.SerializeToElement(new
                {
                    agreementId = agreement.AgreementId, versionNo = version.VersionNo, effectiveFrom = version.EffectiveFrom.ToString(),
                    maxRatePct = CommissionRules.RateText(version.MaxRatePct), rateBand = band, lines = lines.Count,
                }),
            },
            CommandOptions.New(), cancellationToken).ConfigureAwait(false);

        version.Status = CommissionStatus.PendingApproval;
        version.ApprovalRequestId = response.Request.RequestId;
        version.SubmittedBy = actor;
        version.SubmittedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CommissionAgreementSubmitResponse { Version = await queries.VersionViewAsync(version, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// The checker's decision. PLT enforces maker ≠ checker, the editors, the human checker and the authority for the expected volume and
/// the rate band; PTY recomputes the content hash from the stored rows (the client never supplies the approved content) and refuses an
/// approval for a suspended or terminated intermediary or a date that has become backdated.
/// </summary>
internal sealed class ApproveCommissionVersionHandler(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, IOptions<CommissionOptions> options,
    CommissionAgreementQueries queries, IPlatformApprovalService approvals)
    : ICommandHandler<ApproveCommissionVersion, CommissionAgreementApproveResponse>
{
    public async Task<Result<CommissionAgreementApproveResponse>> HandleAsync(ApproveCommissionVersion command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var reject = request.Decision == CommissionAgreementApproveRequest.DecisionValue.Reject;
        var legalEntity = protection.Current(context);
        var version = await db.CommissionVersions.SingleOrDefaultAsync(v => v.VersionId == request.VersionId && v.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return CommissionSupport.NotFound();
        }

        if (version.Status != CommissionStatus.PendingApproval || version.ApprovalRequestId is not { } requestId)
        {
            return CommissionSupport.State($"Only a version pending approval can be decided; the version is {version.Status}.");
        }

        if (request.ContentHash is { } reviewed && reviewed.Value != version.ContentHash)
        {
            return CommissionSupport.Stale("The version changed after you reviewed it.");
        }

        var agreement = await db.CommissionAgreements.SingleAsync(a => a.AgreementId == version.AgreementId, cancellationToken).ConfigureAwait(false);
        var lines = await db.CommissionRateLines.Where(l => l.VersionId == version.VersionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hash = CommissionSupport.HashOf(agreement, version, lines);
        if (hash.Value != version.ContentHash)
        {
            return CommissionSupport.Stale("The stored version no longer matches its content hash.");
        }

        if (!reject)
        {
            var intermediary = await db.Intermediaries.SingleOrDefaultAsync(i => i.IntermediaryId == agreement.IntermediaryId, cancellationToken).ConfigureAwait(false);
            if (CommissionSupport.CheckIntermediary(intermediary) is { } refused)
            {
                return refused;
            }

            if (CommissionSupport.CheckBackdate(version.EffectiveFrom, options.Value, RoleSupport.Today(clock)) is { } backdated)
            {
                return backdated;
            }

            var activeStarts = await db.CommissionVersions.Where(v => v.AgreementId == agreement.AgreementId && v.Status == CommissionStatus.Active)
                .Select(v => v.EffectiveFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
            BusinessDate? latest = activeStarts.Count == 0 ? null : activeStarts.Max();
            if (latest is { } last && version.EffectiveFrom <= last)
            {
                return DomainError.Of(ModuleCode.PTY, "BACKDATE", $"A version must take effect after the latest active version ({last}).");
            }
        }

        var subject = new ObjectRef(ModuleCode.PTY, "CommissionAgreementVersion", version.VersionId.ToString("D"));
        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId, PayloadHash = hash, Comment = request.Comment,
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            if (!reject)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = PartyAuthorityTypes.CommissionApprovalType, ObjectRef = subject },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != PartyAuthorityTypes.CommissionAgreement.Value)
                {
                    return CommissionSupport.Stale($"PLT refused execution: the request is {verified.Status}.");
                }
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        version.Status = reject ? CommissionStatus.Rejected : CommissionStatus.Active;
        version.DecidedBy = context.Actor.ToString();
        version.DecidedAt = clock.Now;
        version.DecisionComment = request.Comment;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CommissionAgreementApproveResponse { Version = await queries.VersionViewAsync(version, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of the commission agreement commands: the version only (rates are business data, not personal).</summary>
internal sealed class CommissionAuditor :
    ICommandAuditor<CreateCommissionVersion, CommissionAgreementCreateVersionResponse>,
    ICommandAuditor<SubmitCommissionVersion, CommissionAgreementSubmitResponse>,
    ICommandAuditor<ApproveCommissionVersion, CommissionAgreementApproveResponse>
{
    public CommandAuditFacts Describe(CreateCommissionVersion command, Result<CommissionAgreementCreateVersionResponse>? result) =>
        Facts(result is { IsSuccess: true } ok ? ok.Value.Version : null);

    public CommandAuditFacts Describe(SubmitCommissionVersion command, Result<CommissionAgreementSubmitResponse>? result) =>
        Facts(result is { IsSuccess: true } ok ? ok.Value.Version : null);

    public CommandAuditFacts Describe(ApproveCommissionVersion command, Result<CommissionAgreementApproveResponse>? result) =>
        Facts(result is { IsSuccess: true } ok ? ok.Value.Version : null);

    private static CommandAuditFacts Facts(CommissionAgreementVersionView? version) => version is null
        ? new CommandAuditFacts()
        : new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PTY, "CommissionAgreementVersion", version.VersionId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("agreementId", version.AgreementId.ToString("D")),
        };
}
