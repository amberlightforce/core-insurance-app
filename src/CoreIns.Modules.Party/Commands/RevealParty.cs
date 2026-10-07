using CoreIns.Modules.Party.Api;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Authorization;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Party.Commands;

/// <summary>
/// An unmasked view of a party's P2 data with a purpose (REQ-PTY-044). Modelled as a command so the platform pipeline
/// audits every reveal (actor, roles, purpose as the reason, object) in its own transaction; no idempotency key needed.
/// </summary>
internal sealed record RevealParty(Guid? PartyId, string? PartyNumber, TimePoint At, string Purpose) : ICommand<PartyGetResponse>;

internal sealed class RevealPartyValidator : AbstractValidator<RevealParty>
{
    public RevealPartyValidator() => RuleFor(c => c.Purpose).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("PURPOSE");
}

internal sealed class RevealPartyHandler(PartyReader reader, PartyProtection protection, RequestContext context, IPermissionEvaluator permissions)
    : ICommandHandler<RevealParty, PartyGetResponse>
{
    public async Task<Result<PartyGetResponse>> HandleAsync(RevealParty command, CancellationToken cancellationToken)
    {
        if (!permissions.Has(context, PartyPermissions.RevealP2))
        {
            return DomainError.Of(ModuleCode.PTY, "AUTHORITY-DENIED", $"Showing P2 data needs permission {PartyPermissions.RevealP2}.");
        }

        var view = await reader.GetAsync(
            protection.Current(context), context.LegalEntity!.Value, command.PartyId, command.PartyNumber, command.At, revealP2: true, cancellationToken).ConfigureAwait(false);
        return view is null ? DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.") : new PartyGetResponse { Party = view };
    }
}

/// <summary>Audit facts of a reveal: which party; the purpose travels as the audit reason.</summary>
internal sealed class RevealPartyAuditor : ICommandAuditor<RevealParty, PartyGetResponse>
{
    public CommandAuditFacts Describe(RevealParty command, Result<PartyGetResponse>? result) => result is { IsSuccess: true } ok
        ? new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.PTY, "Party", ok.Value.Party.PartyId),
            ObjectNumber = ok.Value.Party.PartyNumber.Value,
            BusinessKeys = BusinessKeys.Empty.With("partyId", ok.Value.Party.PartyId.Value.ToString()),
        }
        : new CommandAuditFacts();
}
