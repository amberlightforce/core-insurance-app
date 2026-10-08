using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>
/// pol.Policy.search / pol.Policy.searchByCriteria (REQ-POL-014, the subset FNOL needs): exact policy number and/or insured
/// (policyholder) party id, in the caller's legal entity. Criteria that could be personal travel in a POST body (D-SLC-05).
/// </summary>
internal sealed class PolicySearch(PolicyReader reader, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    public async Task<Result<PolicySearchPage>> SearchAsync(
        string? policyNumber, Guid? insuredPartyId, Instant? validAt, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (policyNumber is null && insuredPartyId is null)
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "Give a policy number or an insured party id.");
        }

        if (policyNumber is not null && !PolicyNumber.TryParse(policyNumber, out _))
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "The policy number is malformed.");
        }

        if (limit is < 1 or > 200 || !PolicyReader.TryDecodeCursor(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var now = clock.Now;
        var take = limit ?? 25;
        var (items, next) = await reader.SearchAsync(
            JobSupport.LegalEntity(context, legalEntities), policyNumber, insuredPartyId, after, take, validAt ?? now, now, cancellationToken).ConfigureAwait(false);
        return new PolicySearchPage { Items = items, NextCursor = next, Limit = take };
    }
}
