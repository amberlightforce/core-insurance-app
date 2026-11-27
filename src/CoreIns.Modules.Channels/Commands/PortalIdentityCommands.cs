using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Commands;

/// <summary>Mapping and errors shared by the portal identity commands.</summary>
internal static class PortalIdentitySupport
{
    public static PortalIdentityView ToView(PortalIdentityRow row) => new()
    {
        Subject = row.Subject,
        PartyId = row.PartyId,
        Status = row.Status == PortalIdentityStatuses.Linked ? PortalIdentityView.StatusValue.Linked : PortalIdentityView.StatusValue.Unlinked,
        LinkedAt = row.LinkedAt,
        LinkedBy = row.LinkedBy,
        UnlinkedAt = row.UnlinkedAt,
        UnlinkedBy = row.UnlinkedBy,
    };

    public static DomainError NotFound() => DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The portal identity is not linked.");

    public static DomainError AlreadyLinked() => DomainError.Of(ModuleCode.CHN, "IDENTITY-LINKED", "The identity or the party already has a live link; unlink it first.");

    public static bool ValidSubject(string? subject) => subject is { Length: >= 1 and <= 200 } && !subject.Any(char.IsControl) && subject.Trim() == subject;
}

/// <summary><c>chn.PortalIdentity.link</c>: staff link a verified sign-in identity to exactly one party.</summary>
internal sealed record LinkPortalIdentity(PortalIdentityLinkRequest Request) : ICommand<PortalIdentityLinkResponse>;

internal sealed class LinkPortalIdentityValidator : AbstractValidator<LinkPortalIdentity>
{
    public LinkPortalIdentityValidator()
    {
        RuleFor(c => c.Request.Subject).Must(PortalIdentitySupport.ValidSubject).WithMessage("The subject has 1-200 characters, no control characters and no surrounding spaces.");
        RuleFor(c => c.Request.PartyId.Value).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Verification).NotEmpty().MaximumLength(500);
    }
}

/// <summary>
/// Link: the party must exist in the legal entity (PTY), the staff member cannot link their own sign-in (no self-linking), and a subject or
/// party that already has a live link is refused (CHN-ERR-IDENTITY-LINKED) until it is unlinked. Repeating the same link is a no-op.
/// </summary>
internal sealed class LinkPortalIdentityHandler(
    ChannelsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IServiceProvider services) : ICommandHandler<LinkPortalIdentity, PortalIdentityLinkResponse>
{
    public async Task<Result<PortalIdentityLinkResponse>> HandleAsync(LinkPortalIdentity command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        if (string.Equals(request.Subject, context.Actor.Id, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.CHN, "SELF-LINK", "You cannot link your own sign-in identity to a party.");
        }

        var parties = services.GetService<IPartyPartyService>();
        if (parties is null)
        {
            return DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The party cannot be verified.");
        }

        try
        {
            _ = await parties.GetAsync(request.PartyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException)
        {
            return DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The party does not exist in your legal entity.");
        }

        var live = await db.PortalIdentities
            .Where(r => r.LegalEntityId == legalEntity && r.Status == PortalIdentityStatuses.Linked && (r.Subject == request.Subject || r.PartyId == request.PartyId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (live.Count == 1 && live[0].Subject == request.Subject && live[0].PartyId == request.PartyId)
        {
            return new PortalIdentityLinkResponse { Identity = PortalIdentitySupport.ToView(live[0]) };
        }

        if (live.Count > 0)
        {
            return PortalIdentitySupport.AlreadyLinked();
        }

        var row = new PortalIdentityRow
        {
            PortalIdentityId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            Subject = request.Subject,
            PartyId = request.PartyId,
            Status = PortalIdentityStatuses.Linked,
            Verification = request.Verification.Trim(),
            LinkedAt = clock.Now,
            LinkedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.PortalIdentities.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique indexes fail closed when two staff link at the same moment.
            return PortalIdentitySupport.AlreadyLinked();
        }

        return new PortalIdentityLinkResponse { Identity = PortalIdentitySupport.ToView(row) };
    }
}

/// <summary>Audit facts of <c>chn.PortalIdentity.link</c>.</summary>
internal sealed class LinkPortalIdentityAuditor : ICommandAuditor<LinkPortalIdentity, PortalIdentityLinkResponse>
{
    public CommandAuditFacts Describe(LinkPortalIdentity command, Result<PortalIdentityLinkResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("subject", command.Request.Subject).With("partyId", command.Request.PartyId.Value.ToString("D"));
        return result is { IsSuccess: true }
            ? new CommandAuditFacts { BusinessKeys = keys, Changes = AuditDiff.Compute(null, new { status = "LINKED", verification = command.Request.Verification }) }
            : new CommandAuditFacts { BusinessKeys = keys };
    }
}

/// <summary><c>chn.PortalIdentity.unlink</c>: ends the live link of an identity. The row stays as history.</summary>
internal sealed record UnlinkPortalIdentity(PortalIdentityUnlinkRequest Request) : ICommand<PortalIdentityLinkResponse>;

internal sealed class UnlinkPortalIdentityValidator : AbstractValidator<UnlinkPortalIdentity>
{
    public UnlinkPortalIdentityValidator()
    {
        RuleFor(c => c.Request.Subject).Must(PortalIdentitySupport.ValidSubject);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class UnlinkPortalIdentityHandler(
    ChannelsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock) : ICommandHandler<UnlinkPortalIdentity, PortalIdentityLinkResponse>
{
    public async Task<Result<PortalIdentityLinkResponse>> HandleAsync(UnlinkPortalIdentity command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var subject = command.Request.Subject;
        var row = await db.PortalIdentities
            .SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.Subject == subject && r.Status == PortalIdentityStatuses.Linked, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return PortalIdentitySupport.NotFound();
        }

        row.Status = PortalIdentityStatuses.Unlinked;
        row.UnlinkedAt = clock.Now;
        row.UnlinkedBy = context.Actor.ToString();
        row.UnlinkReason = command.Request.Reason.Trim();
        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PortalIdentityLinkResponse { Identity = PortalIdentitySupport.ToView(row) };
    }
}

/// <summary>Audit facts of <c>chn.PortalIdentity.unlink</c>.</summary>
internal sealed class UnlinkPortalIdentityAuditor : ICommandAuditor<UnlinkPortalIdentity, PortalIdentityLinkResponse>
{
    public CommandAuditFacts Describe(UnlinkPortalIdentity command, Result<PortalIdentityLinkResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("subject", command.Request.Subject);
        return result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                BusinessKeys = keys.With("partyId", ok.Value.Identity.PartyId.Value.ToString("D")),
                Changes = AuditDiff.Compute(new { status = "LINKED" }, new { status = "UNLINKED", reason = command.Request.Reason }),
            }
            : new CommandAuditFacts { BusinessKeys = keys };
    }
}
