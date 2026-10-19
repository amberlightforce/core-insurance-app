using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>Maps request DTOs to <see cref="ContractContent"/>; every amount must be EUR (D-SL4-04).</summary>
internal static class ContractInput
{
    /// <summary>The content of a create request, or the validation error (a non-EUR amount, an open period).</summary>
    public static Result<ContractContent> From(ContractCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Period.End is not { } end)
        {
            return RiErrors.Validation("period", "PERIOD_END_REQUIRED", "A treaty period has an end date.");
        }

        var layers = Layers(request.Layers);
        if (layers.IsFailure)
        {
            return layers.Error!;
        }

        return new ContractContent(
            request.ContractType == RiContractType.XolPerRisk ? ContractRules.XolPerRisk : request.ContractType.ToString(),
            request.ContractYear, request.Currency.Code, request.Period.Start, end,
            [.. request.Scope.ProductCodes], [.. request.Scope.CoverageCodes],
            request.Clause.AlaeIncluded, request.Clause.StatutoryInterestIncluded, Inure(request.Clause),
            layers.Value, Lines(request.Participations), request.PlacedPct);
    }

    /// <summary>The stored content with the fields an update request names replaced (REQ-RI-056: edits only while Draft).</summary>
    public static Result<ContractContent> Apply(ContractContent current, ContractUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = current;
        if (request.Period is { } period)
        {
            if (period.End is not { } end)
            {
                return RiErrors.Validation("period", "PERIOD_END_REQUIRED", "A treaty period has an end date.");
            }

            result = result with { ValidFrom = period.Start, ValidTo = end };
        }

        if (request.Scope is { } scope)
        {
            result = result with { ProductCodes = [.. scope.ProductCodes], CoverageCodes = [.. scope.CoverageCodes] };
        }

        if (request.Clause is { } clause)
        {
            result = result with { AlaeIncluded = clause.AlaeIncluded, StatutoryInterestIncluded = clause.StatutoryInterestIncluded, RecoveriesInure = Inure(clause) };
        }

        if (request.Layers is { } layers)
        {
            var mapped = Layers(layers);
            if (mapped.IsFailure)
            {
                return mapped.Error!;
            }

            result = result with { Layers = mapped.Value };
        }

        if (request.Participations is { } participations)
        {
            result = result with { Lines = Lines(participations) };
        }

        if (request.PlacedPct is { } placed)
        {
            result = result with { PlacedPct = placed };
        }

        return result;
    }

    private static string Inure(RiContractClause clause) =>
        clause.RecoveriesInure == RiContractClause.RecoveriesInureValue.RealisedOnly ? ContractRules.RealisedOnly : clause.RecoveriesInure.ToString();

    private static IReadOnlyList<ContentLine> Lines(IReadOnlyList<RiParticipationInput> participations) =>
        [.. participations.Select(p => new ContentLine(p.ReinsurerPartyId.Value, p.BrokerPartyId, p.SignedLinePct, p.Lead))];

    private static Result<IReadOnlyList<ContentLayer>> Layers(IReadOnlyList<RiLayer> layers)
    {
        var result = new List<ContentLayer>();
        foreach (var (layer, index) in layers.Select((l, i) => (l, i)))
        {
            var amounts = new[] { layer.Attachment, layer.Limit, layer.Aad }.Concat(layer.Aal is { } aal ? [aal] : []);
            if (amounts.Any(m => m.Currency.Code != ContractRules.Eur))
            {
                return RiErrors.Validation(FormattableString.Invariant($"layers[{index}]"), "CURRENCY_NOT_SUPPORTED", "Treaty amounts are EUR only (D-SL4-04).");
            }

            result.Add(new ContentLayer(layer.LayerNo, layer.Attachment.Amount, layer.Limit.Amount, layer.Aad.Amount, layer.Aal?.Amount));
        }

        return result;
    }
}

/// <summary>
/// Checks the parties of a panel through the PTY contract (REQ-RI-046): the reinsurer (and broker) must exist in the
/// caller's legal entity, be organisations and be usable (a prospect or active party). The masked read is enough; RI
/// never reveals P2 data and keeps party ids only.
/// </summary>
internal sealed class PartyDirectory(IPartyPartyService parties)
{
    private static readonly string[] UsableStatuses = ["PROSPECT", "ACTIVE"];

    /// <summary>Null when every party is usable; otherwise <c>RI-ERR-REINSURER-ID</c> naming the offending field.</summary>
    public async Task<DomainError?> CheckAsync(IReadOnlyList<ContentLine> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            var reinsurer = await CheckPartyAsync(line.ReinsurerPartyId, FormattableString.Invariant($"participations[{index}].reinsurerPartyId"), cancellationToken).ConfigureAwait(false);
            if (reinsurer is not null)
            {
                return reinsurer;
            }

            if (line.BrokerPartyId is { } broker)
            {
                var brokerError = await CheckPartyAsync(broker, FormattableString.Invariant($"participations[{index}].brokerPartyId"), cancellationToken).ConfigureAwait(false);
                if (brokerError is not null)
                {
                    return brokerError;
                }
            }
        }

        return null;
    }

    private async Task<DomainError?> CheckPartyAsync(Guid partyId, string field, CancellationToken cancellationToken)
    {
        PartyGetResponse response;
        try
        {
            response = await parties.GetAsync(partyId.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PTY && ex.Error.Code.Name == "NOT-FOUND")
        {
            return RiErrors.ReinsurerId(field, "PARTY_NOT_FOUND", "The party does not exist in your legal entity.");
        }

        if (response.Party.PartyType != PartyType.Organisation)
        {
            return RiErrors.ReinsurerId(field, "PARTY_NOT_ORGANISATION", "A reinsurer or broker must be an organisation party.");
        }

        return UsableStatuses.Contains(response.Party.Status, StringComparer.Ordinal)
            ? null
            : RiErrors.ReinsurerId(field, "PARTY_NOT_USABLE", "The party is not active.");
    }
}
