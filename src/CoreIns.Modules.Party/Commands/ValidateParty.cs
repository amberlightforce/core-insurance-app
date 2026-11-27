using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Party.Commands;

/// <summary>
/// <c>pty.Party.validate</c> (BO-PTY-01, REQ-PTY-003, REQ-PTY-131): dry-run of the create rules. Runs the same shape rules as
/// <c>pty.Party.create</c> (<see cref="CreatePartyValidator"/>), the bound IdValidator on every identifier (AFM check digit) and
/// the duplicate search by identifier and by name; saves nothing. Rule violations are returned as issues, not as an error.
/// </summary>
internal sealed class PartyValidationService(
    IValidator<CreateParty> shapeRules, IIdValidator idValidator, PartySearch search, PartyReader reader, PartyProtection protection, RequestContext context)
{
    public async Task<Result<PartyValidateResponse>> ValidateAsync(PartyValidateRequest request, CancellationToken cancellationToken)
    {
        var data = request.PartyData;
        var issues = new List<PartyValidationIssue>();
        var shape = await shapeRules.ValidateAsync(new CreateParty(data), cancellationToken).ConfigureAwait(false);
        issues.AddRange(shape.Errors.Select(e => new PartyValidationIssue
        {
            Field = Camel(e.PropertyName.Replace("Request.", string.Empty, StringComparison.Ordinal)),
            Code = string.IsNullOrEmpty(e.ErrorCode) ? "INVALID" : e.ErrorCode,
            Message = e.ErrorMessage,
        }));

        var jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.");
        var legalEntity = protection.Current(context);
        var hits = new Dictionary<Guid, SearchHit>();
        void Add(IEnumerable<SearchHit> found)
        {
            foreach (var hit in found)
            {
                if (!hits.TryGetValue(hit.PartyId, out var existing) || hit.Similarity > existing.Similarity)
                {
                    hits[hit.PartyId] = hit;
                }
            }
        }

        foreach (var (identifier, index) in (data.Identifiers ?? []).Select((i, n) => (i, n)))
        {
            IdValidationResult result;
            try
            {
                result = await idValidator.ValidateAsync(
                    new IdScheme(identifier.Scheme), identifier.Value,
                    new IdValidationContext(identifier.IssuingCountry ?? jurisdiction.Value, data.PartyType == PartyType.Person ? "Person" : "Organisation"),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SpiException ex) when (ex.Category == SpiErrorCategory.NotApplicable)
            {
                issues.Add(new PartyValidationIssue { Field = $"identifiers[{index}].scheme", Code = "SCHEME_NOT_SUPPORTED", Message = $"Identifier scheme {identifier.Scheme} is not in the identifier catalogue." });
                continue;
            }

            if (!result.IsAccepted)
            {
                foreach (var error in result.Errors)
                {
                    issues.Add(new PartyValidationIssue { Field = $"identifiers[{index}].value", Code = error.Code, Message = $"The {identifier.Scheme} value is not valid ({error.Code})." });
                }

                continue;
            }

            Add(await search.ByIdentifierAsync(legalEntity, identifier.Scheme, identifier.Value, cancellationToken).ConfigureAwait(false));
        }

        var name = data.Person is { } person ? NameForms.PersonDisplay(person.GivenNames, person.FamilyName) : data.Organisation?.LegalName;
        if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length >= PartySearch.MinimumQueryLength)
        {
            Add(await search.ByNameAsync(legalEntity, name, cancellationToken).ConfigureAwait(false));
        }

        var top = hits.Values.OrderByDescending(h => h.Tier).ThenByDescending(h => h.Similarity).Take(10).ToList();
        var candidates = await reader.HydrateAsync(legalEntity, top, cancellationToken).ConfigureAwait(false);
        return new PartyValidateResponse { Issues = issues, Candidates = candidates };
    }

    private static string Camel(string path) =>
        string.Join('.', path.Split('.').Select(part => part.Length == 0 ? part : char.ToLowerInvariant(part[0]) + part[1..]));
}
