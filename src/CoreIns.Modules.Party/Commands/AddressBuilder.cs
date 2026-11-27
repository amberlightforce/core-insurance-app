using CoreIns.Modules.Market.Contracts.Localisation;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Party.Commands;

/// <summary>
/// Validates (country <c>AddressFormatter</c>, postcode format) and formats a typed address input into a <see cref="PartyAddressRow"/> with
/// generated Latin forms (REQ-PTY-074..076). Shared by <c>pty.Party.create</c> and <c>pty.Location.*</c> (BO-PTY-02).
/// </summary>
internal sealed class AddressBuilder(IEnumerable<IAddressFormatter> addressFormatters, NameForms nameForms)
{
    public async Task<Result<PartyAddressRow>> BuildAsync(
        AddressInput input, PartyId partyId, LegalEntityId legalEntity, BusinessDate today, Instant now, string actor, CancellationToken cancellationToken)
    {
        var address = new PostalAddress
        {
            Country = input.Country,
            Street = Clean(input.Street),
            Number = Clean(input.Number),
            Building = Clean(input.Building),
            Floor = Clean(input.Floor),
            Unit = Clean(input.Unit),
            Postcode = Clean(input.Postcode),
            Locality = Clean(input.Locality),
            Municipality = Clean(input.Municipality),
            RegionalUnit = Clean(input.RegionalUnit),
            Region = Clean(input.Region),
            FreeLines = [.. (input.FreeLines ?? []).Select(l => l.Trim()).Where(l => l.Length > 0)],
        };

        var formatter = addressFormatters.FirstOrDefault(f => f.Countries.Contains(input.Country, StringComparer.Ordinal));
        var state = "UNVALIDATED";
        string? ruleSet = null;
        IReadOnlyList<string> lines = [.. new[] { Join(" ", address.Street, address.Number), Join(" ", address.Postcode, address.Locality) }.OfType<string>(), .. address.FreeLines];
        IReadOnlyList<string> latinLines = lines;
        if (formatter is not null)
        {
            var validation = await formatter.ValidateAsync(address, cancellationToken).ConfigureAwait(false);
            if (validation.Errors.Any(e => e.Code == AddressErrorCodes.PostcodeFormat))
            {
                return DomainError.Of(ModuleCode.PTY, "POSTCODE-FORMAT", "The postcode does not have the country's format.");
            }

            if (validation.Status == AddressValidationStatus.Invalid)
            {
                return new DomainError(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), "The address is not valid.")
                {
                    FieldErrors = [.. validation.Errors.Select(e => new FieldError("addresses." + (e.Field ?? "address"), e.Code, "pty.address." + e.Code.ToLowerInvariant()))],
                };
            }

            address = validation.Normalised;
            state = validation.Status == AddressValidationStatus.Valid ? "VALIDATED" : "UNVALIDATED";
            var nativeScript = NameForms.ScriptOf(string.Concat(address.Street, address.Locality));
            var native = await formatter.FormatAsync(address, AddressPurpose.Postal, nativeScript, cancellationToken).ConfigureAwait(false);
            var latin = await formatter.FormatAsync(address, AddressPurpose.Postal, ScriptCodes.Latin, cancellationToken).ConfigureAwait(false);
            lines = native.Lines;
            latinLines = latin.Lines;
            ruleSet = latin.RuleSetId;
        }

        var latinFreeLines = new List<string>();
        foreach (var line in address.FreeLines)
        {
            latinFreeLines.Add(await nameForms.LatinAsync(line, cancellationToken).ConfigureAwait(false) ?? line);
        }

        return new PartyAddressRow
        {
            AddressId = Guid.CreateVersion7(), PartyId = partyId, LegalEntityId = legalEntity,
            Types = [.. input.Types.Distinct().Select(t => Codes.Of(t))], IsPrimary = input.Primary == true, Country = input.Country,
            Street = address.Street, Number = address.Number, Building = address.Building, Floor = address.Floor, Unit = address.Unit,
            Postcode = address.Postcode, Locality = address.Locality, Municipality = address.Municipality, RegionalUnit = address.RegionalUnit,
            Region = address.Region, FreeLines = [.. address.FreeLines],
            LatinStreet = await nameForms.LatinAsync(address.Street, cancellationToken).ConfigureAwait(false),
            LatinBuilding = await nameForms.LatinAsync(address.Building, cancellationToken).ConfigureAwait(false),
            LatinLocality = await nameForms.LatinAsync(address.Locality, cancellationToken).ConfigureAwait(false),
            LatinMunicipality = await nameForms.LatinAsync(address.Municipality, cancellationToken).ConfigureAwait(false),
            LatinRegionalUnit = await nameForms.LatinAsync(address.RegionalUnit, cancellationToken).ConfigureAwait(false),
            LatinRegion = await nameForms.LatinAsync(address.Region, cancellationToken).ConfigureAwait(false),
            LatinFreeLines = [.. latinFreeLines],
            FormattedLines = [.. lines], FormattedLinesLatin = [.. latinLines], ValidationState = state, RuleSetId = ruleSet,
            Description = Clean(input.Description), ValidFrom = today, RecordedFrom = now, CreatedBy = actor,
        };
    }

    internal static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.PTY, "VALIDATION"), message) { FieldErrors = [new FieldError(field, code, "pty." + code.ToLowerInvariant(), message)] };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}
