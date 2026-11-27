using System.Text.Json;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Channels.Commands;

internal sealed record UpdatePortalContact(PortalContactUpdateRequest Request) : ICommand<PortalContactUpdateResponse>;

internal sealed class UpdatePortalContactValidator : AbstractValidator<UpdatePortalContact>
{
    public UpdatePortalContactValidator()
    {
        RuleFor(c => c.Request).Must(r =>
        {
            var needsInput = r.Action != PortalContactUpdateRequest.ActionValue.Remove;
            var hasInput = r.Kind == PortalContactUpdateRequest.KindValue.Address ? r.Address is not null && r.ContactPoint is null : r.ContactPoint is not null && r.Address is null;
            var needsId = r.Action != PortalContactUpdateRequest.ActionValue.Add;
            return (!needsInput || hasInput) && (needsInput || (r.Address is null && r.ContactPoint is null)) && (!needsId || (r.LocationId is { } id && id != Guid.Empty)) && (needsId || r.LocationId is null);
        }).WithMessage("ADD needs the input of its kind; REPLACE needs locationId and the input; REMOVE needs locationId only.");
    }
}

/// <summary>
/// The customer's own address, phone or e-mail change (matrix <c>contact.update</c>), through PTY in-process. The matrix is asked first (REFER
/// raises a ticket and changes nothing); only the caller's own party is ever touched, and a location id must belong to it (else 404). The PTY
/// service does no permission check of its own. Ids are per version: a replace returns the new id. A stale record version is PTY-ERR-STALE (409).
/// </summary>
internal sealed class UpdatePortalContactHandler(PortalServicing portal) : ICommandHandler<UpdatePortalContact, PortalContactUpdateResponse>
{
    public async Task<Result<PortalContactUpdateResponse>> HandleAsync(UpdatePortalContact command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (await portal.CallerAsync(cancellationToken).ConfigureAwait(false) is not { } caller)
        {
            return PortalServicing.NotFound();
        }

        var answer = await portal.EvaluateAsync("contact.update", null, JsonSerializer.SerializeToElement(new { partyId = caller.PartyId.Value.ToString("D") }), true, cancellationToken).ConfigureAwait(false);
        if (answer.Decision.Outcome == MatrixOutcomes.Deny)
        {
            return PortalServicing.NotPermitted();
        }

        var kind = request.Kind == PortalContactUpdateRequest.KindValue.Address ? PortalContactUpdateResponse.KindValue.Address : PortalContactUpdateResponse.KindValue.ContactPoint;
        var responseAction = request.Action switch
        {
            PortalContactUpdateRequest.ActionValue.Add => PortalContactUpdateResponse.ActionValue.Add,
            PortalContactUpdateRequest.ActionValue.Replace => PortalContactUpdateResponse.ActionValue.Replace,
            _ => PortalContactUpdateResponse.ActionValue.Remove,
        };
        if (answer.Decision.Outcome == MatrixOutcomes.Refer)
        {
            return new PortalContactUpdateResponse { Outcome = PortalContactUpdateResponse.OutcomeValue.Referred, Action = responseAction, Kind = kind, TicketId = answer.TicketId };
        }

        try
        {
            var locations = portal.Service<IPartyLocationService>();
            if (request.Action != PortalContactUpdateRequest.ActionValue.Add && !await OwnsLocationAsync(portal, caller.PartyId, request, cancellationToken).ConfigureAwait(false))
            {
                return PortalServicing.NotFound();
            }

            // The same member names as the PTY input: the customer can send nothing PTY would not take for a party.
            var address = request.Address is { } a ? JsonSerializer.Deserialize<AddressInput>(JsonSerializer.Serialize(a)) : null;
            var contactPoint = request.ContactPoint is { } c ? JsonSerializer.Deserialize<ContactPointInput>(JsonSerializer.Serialize(c)) : null;
            if (request.Action == PortalContactUpdateRequest.ActionValue.Add)
            {
                var added = await locations.AddAsync(
                    new LocationAddRequest { PartyId = caller.PartyId, Address = address, ContactPoint = contactPoint, ExpectedRecordVersion = request.ExpectedRecordVersion }, portal.Step("add"), cancellationToken)
                    .ConfigureAwait(false);
                return Applied(responseAction, kind, added.LocationId, added.RecordVersion);
            }

            if (request.Action == PortalContactUpdateRequest.ActionValue.Replace)
            {
                var replaced = await locations.UpdateAsync(
                    request.LocationId!.Value.ToString("D"), new LocationUpdateRequest { Address = address, ContactPoint = contactPoint, ExpectedRecordVersion = request.ExpectedRecordVersion },
                    portal.Step("replace"), cancellationToken).ConfigureAwait(false);
                return Applied(responseAction, kind, replaced.LocationId, replaced.RecordVersion);
            }

            var removed = await locations.RemoveAsync(
                new LocationRemoveRequest { LocationId = request.LocationId!.Value, ExpectedRecordVersion = request.ExpectedRecordVersion }, portal.Step("remove"), cancellationToken).ConfigureAwait(false);
            return Applied(responseAction, kind, removed.LocationId, removed.RecordVersion);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    private static PortalContactUpdateResponse Applied(PortalContactUpdateResponse.ActionValue action, PortalContactUpdateResponse.KindValue kind, Guid locationId, int recordVersion) =>
        new() { Outcome = PortalContactUpdateResponse.OutcomeValue.Applied, Action = action, Kind = kind, LocationId = locationId, RecordVersion = recordVersion };

    /// <summary>The location id must be a current address or contact point (by the stated kind) of the caller's own party.</summary>
    private static async Task<bool> OwnsLocationAsync(PortalServicing portal, PartyId party, PortalContactUpdateRequest request, CancellationToken cancellationToken)
    {
        var view = (await portal.Service<IPartyPartyService>().GetAsync(party.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Party;
        var id = request.LocationId!.Value;
        return request.Kind == PortalContactUpdateRequest.KindValue.Address
            ? view.Addresses.Any(a => a.AddressId == id && a.ValidTo is null)
            : view.ContactPoints.Any(p => p.ContactPointId == id);
    }
}

internal sealed class UpdatePortalContactAuditor : ICommandAuditor<UpdatePortalContact, PortalContactUpdateResponse>
{
    // Contact data is personal data: only the action and kind are audited, never the values.
    public CommandAuditFacts Describe(UpdatePortalContact command, Result<PortalContactUpdateResponse>? result) =>
        new() { BusinessKeys = BusinessKeys.Empty.With("action", command.Request.Action.ToString()).With("kind", command.Request.Kind.ToString()) };
}
