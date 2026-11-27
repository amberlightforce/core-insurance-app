using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Services;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Errors;

namespace CoreIns.Modules.Channels.Queries;

/// <summary>The customer's own current contact details (<c>chn.PortalContact.get</c>), read from PTY in-process; never another party's.</summary>
internal sealed class PortalContactReader(PortalScope scope, IServiceProvider services)
{
    public async Task<PortalContactView?> GetAsync(CancellationToken cancellationToken)
    {
        if (await scope.ResolveAsync(cancellationToken).ConfigureAwait(false) is not { } caller || services.GetService(typeof(IPartyPartyService)) is not IPartyPartyService parties)
        {
            return null;
        }

        try
        {
            var party = (await parties.GetAsync(caller.PartyId.Value.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false)).Party;
            return new PortalContactView
            {
                RecordVersion = party.RecordVersion,
                Addresses =
                [
                    .. party.Addresses.Where(a => a.ValidTo is null).Select(a => new PortalContactAddress
                    {
                        AddressId = a.AddressId, Types = [.. a.Types.Select(t => t.ToString().ToUpperInvariant())], Primary = a.Primary, Country = a.Country,
                        Street = a.Native.Street, Number = a.Native.Number, Building = a.Native.Building, Floor = a.Native.Floor, Unit = a.Native.Unit, Postcode = a.Native.Postcode,
                        Locality = a.Native.Locality, Municipality = a.Native.Municipality, RegionalUnit = a.Native.RegionalUnit, Region = a.Native.Region, FormattedLines = a.FormattedLines,
                    }),
                ],
                ContactPoints =
                [
                    .. party.ContactPoints.Select(p => new PortalContactPoint
                    {
                        ContactPointId = p.ContactPointId, Type = Map(p.Type), Value = p.Value, Purpose = p.Purpose.ToString().ToUpperInvariant(), Primary = p.Primary,
                        Verified = p.VerificationStatus == ContactPointView.VerificationStatusValue.Verified,
                    }),
                ],
            };
        }
        catch (DomainException)
        {
            return null;
        }
    }

    private static PortalContactPoint.TypeValue Map(ContactPointType type) => type switch
    {
        ContactPointType.Mobile => PortalContactPoint.TypeValue.Mobile,
        ContactPointType.Landline => PortalContactPoint.TypeValue.Landline,
        ContactPointType.WorkPhone => PortalContactPoint.TypeValue.WorkPhone,
        _ => PortalContactPoint.TypeValue.Email,
    };
}
