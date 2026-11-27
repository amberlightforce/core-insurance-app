using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using VerificationStatusCode = CoreIns.Modules.Party.Queries.VerificationStatus;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Identifier.verify</c> (BO-PTY-01, REQ-PTY-003, REQ-PTY-054).</summary>
internal sealed record VerifyIdentifier(IdentifierVerifyRequest Request) : ICommand<IdentifierVerifyResponse>;

internal sealed class VerifyIdentifierValidator : AbstractValidator<VerifyIdentifier>
{
    public VerifyIdentifierValidator() => RuleFor(c => c.Request.Scheme).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("SCHEME");
}

/// <summary>
/// Re-checks the party's current identifier of a scheme with the bound <see cref="IIdValidator"/> (format and check digit) and
/// records the outcome: an invalid value becomes VERIFICATION_FAILED; a valid one keeps a stronger status (document or registry
/// verified) or stays SELF_DECLARED, with source ID_VALIDATOR and the time checked. No registry or VIES adapter is bound, so a
/// check never raises the status to REGISTRY_VERIFIED (TODO(hardening): registry adapter, PTY-ERR-REGISTRY-UNAVAILABLE).
/// </summary>
internal sealed class VerifyIdentifierHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, IIdValidator idValidator)
    : ICommandHandler<VerifyIdentifier, IdentifierVerifyResponse>
{
    private const string Source = "ID_VALIDATOR";

    public async Task<Result<IdentifierVerifyResponse>> HandleAsync(VerifyIdentifier command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var scheme = command.Request.Scheme;
        var partyId = command.Request.PartyId;
        var party = await db.Parties.SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.PartyId == partyId, cancellationToken).ConfigureAwait(false);
        if (party is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.");
        }

        var row = await db.Identifiers.SingleOrDefaultAsync(
            i => i.PartyId == partyId && i.LegalEntityId == legalEntity && i.Scheme == scheme && i.ValidTo == null && i.RecordedTo == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", $"The party holds no {scheme} identifier.");
        }

        var value = await protection.DecryptIdentifierAsync(legalEntity, row.IdentifierId, row.ValueEncrypted, cancellationToken).ConfigureAwait(false);
        IdValidationResult result;
        try
        {
            result = await idValidator.ValidateAsync(
                new IdScheme(scheme), value,
                new IdValidationContext(row.IssuingCountry ?? party.Jurisdiction, party.PartyType == "PERSON" ? "Person" : "Organisation"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SpiException ex) when (ex.Category == SpiErrorCategory.NotApplicable)
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), $"Identifier scheme {scheme} is not in the identifier catalogue.");
        }

        var now = clock.Now;
        var previous = row.VerificationStatus;
        var suggestions = new List<string>();
        if (result.IsAccepted)
        {
            row.VerificationStatus = previous is "DOCUMENT_VERIFIED" or "REGISTRY_VERIFIED" ? previous : Codes.Of(VerificationStatusCode.SelfDeclared);
            suggestions.AddRange(result.Findings);
        }
        else
        {
            row.VerificationStatus = Codes.Of(VerificationStatusCode.VerificationFailed);
            suggestions.AddRange(result.Errors.Select(e => e.Code));
        }

        row.VerificationSource = Source;
        row.VerifiedAt = now;
        row.ValidatorVersion = result.ValidatorVersion;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(IdentifierVerifiedV1.Descriptor),
            "Party",
            partyId.Value.ToString(),
            new IdentifierVerifiedV1 { PartyId = partyId, Scheme = scheme, Status = row.VerificationStatus, Source = Source, VerifiedAt = now },
            BusinessKeys.Empty.With("partyId", partyId.Value.ToString())));

        return new IdentifierVerifyResponse
        {
            Status = row.VerificationStatus switch
            {
                "DOCUMENT_VERIFIED" => IdentifierVerifyResponse.StatusValue.DocumentVerified,
                "REGISTRY_VERIFIED" => IdentifierVerifyResponse.StatusValue.RegistryVerified,
                "VERIFICATION_FAILED" => IdentifierVerifyResponse.StatusValue.VerificationFailed,
                _ => IdentifierVerifyResponse.StatusValue.SelfDeclared,
            },
            Valid = result.IsAccepted,
            Suggestions = suggestions,
        };
    }
}

/// <summary>Audit facts of <c>pty.Identifier.verify</c>: the party and the scheme; never the value (REQ-PTY-060).</summary>
internal sealed class VerifyIdentifierAuditor : ICommandAuditor<VerifyIdentifier, IdentifierVerifyResponse>
{
    public CommandAuditFacts Describe(VerifyIdentifier command, Result<IdentifierVerifyResponse>? result) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.PTY, "Party", command.Request.PartyId),
        BusinessKeys = BusinessKeys.Empty.With("partyId", command.Request.PartyId.Value.ToString()),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { scheme = command.Request.Scheme, status = ok.Value.Status.ToString(), valid = ok.Value.Valid })
            : [],
    };
}
