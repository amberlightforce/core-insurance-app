using System.Text.Json;
using CoreIns.Modules.Reinsurance.Authority;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>
/// The approval binding and the period-boundary transitions of a contract (REQ-RI-056..058). One place builds the PLT
/// request, verifies it and moves a contract to Active / Expired, so the approving command and the scanner behave alike.
/// </summary>
internal sealed class ContractLifecycleService(
    ReinsuranceDbContext db,
    IPlatformApprovalService approvals,
    IEventPublisher events)
{
    /// <summary>The PLT approval type and the authority type of a treaty approval.</summary>
    public const string ApprovalType = "RI.CONTRACT_APPROVE";

    /// <summary>The aggregate type of the events (matches the generated descriptors).</summary>
    public const string AggregateType = "RiContract";

    /// <summary>The role a referral for a treaty approval is addressed to (PRD-08 §11, D-SL4-16).</summary>
    public const string ReferralRole = "Staff.ReinsuranceManager";

    /// <summary>The PLT subject of a contract's approval.</summary>
    public static ObjectRef SubjectOf(RiContractId contractId) => ObjectRef.For(ModuleCode.RI, "Contract", contractId);

    /// <summary>The PLT approval request for <paramref name="content"/> (type, subject, hash and authority are set by RI only: PITFALLS 3, 4).</summary>
    public static ApprovalRequestRequest BuildRequest(ContractRow contract, ContractVersionRow version, ContractContent content, Sha256Hash hash) =>
        new()
        {
            Type = ApprovalType,
            ObjectRef = SubjectOf(contract.ContractId),
            PayloadHash = hash,
            Authority = new ApprovalAuthority
            {
                Type = ReinsuranceAuthorityTypes.ContractApprove.Value,
                Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [ReinsuranceAuthorityTypes.ContractTypeDimension] = contract.ContractType },
            },
            ReferralRole = ReferralRole,
            Reason = $"Reinsurance contract {contract.ContractNumber} ({contract.ContractType} {contract.ContractYear}) submitted for approval.",
            Diff = JsonSerializer.SerializeToElement(new
            {
                contractNumber = contract.ContractNumber,
                stableTreatyId = contract.StableTreatyId,
                contractYear = contract.ContractYear,
                contractType = contract.ContractType,
                versionNo = version.VersionNo,
                contentHash = hash.Value,
                period = new { from = content.ValidFrom.ToString(), to = content.ValidTo.ToString() },
                placedPct = ContractContent.Fixed(content.PlacedPct, ContractContent.PercentScale),
                scope = new { productCodes = content.ProductCodes, coverageCodes = content.CoverageCodes },
                clause = new { content.AlaeIncluded, content.StatutoryInterestIncluded, content.RecoveriesInure },
                layers = content.Layers.OrderBy(l => l.LayerNo).Select(l => new
                {
                    l.LayerNo,
                    attachment = ContractContent.Fixed(l.Attachment, ContractContent.MoneyScale),
                    limit = ContractContent.Fixed(l.Limit, ContractContent.MoneyScale),
                    aad = ContractContent.Fixed(l.Aad, ContractContent.MoneyScale),
                    aal = l.Aal is { } aal ? ContractContent.Fixed(aal, ContractContent.MoneyScale) : null,
                }),
                participations = content.Lines.OrderByDescending(l => l.Lead).ThenBy(l => l.ReinsurerPartyId).Select(l => new
                {
                    reinsurerPartyId = l.ReinsurerPartyId,
                    brokerPartyId = l.BrokerPartyId,
                    signedLinePct = ContractContent.Fixed(l.SignedLinePct, ContractContent.PercentScale),
                    l.Lead,
                }),
            }),
        };

    /// <summary>
    /// Null when the stored content still is what the checker approved: the hash recomputed from the stored rows equals
    /// the version's hash, and PLT confirms (<c>verifyForExecution</c>) that its approved request is for exactly this
    /// type, subject and hash with the authority RI asked for (PITFALLS 3). Otherwise <c>RI-ERR-STALE</c>.
    /// </summary>
    public async Task<DomainError?> VerifyApprovalAsync(ContractRow contract, ContractVersionRow version, ContractContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(content);
        var hash = content.Hash(contract.ContractId, version.VersionNo);
        if (!string.Equals(hash.Value, version.ContentHash, StringComparison.Ordinal))
        {
            return RiErrors.Stale("The stored content differs from the content that was submitted for approval.");
        }

        if (contract.ApprovalRequestId is not { } requestId || version.ApprovalRequestId != requestId)
        {
            return RiErrors.Stale("The contract has no approval request bound to this version.");
        }

        ApprovalVerifyForExecutionResponse verified;
        try
        {
            verified = await approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = ApprovalType, ObjectRef = SubjectOf(contract.ContractId) },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return RiErrors.Stale($"PLT refused execution ({ex.Error.Code}).");
        }

        if (!verified.Ok)
        {
            return RiErrors.Stale($"Approval request {requestId} is {verified.Status}.");
        }

        var authority = verified.Authority;
        return authority.Type == ReinsuranceAuthorityTypes.ContractApprove.Value
            && authority.Codes?.GetValueOrDefault(ReinsuranceAuthorityTypes.ContractTypeDimension) == contract.ContractType
            ? null
            : RiErrors.Stale("The approved authority does not match the contract.");
    }

    /// <summary>Approved → Active at the period start; publishes <c>RIContractActivated</c> in the same transaction (REQ-RI-058).</summary>
    public async Task<Result<Unit>> ActivateAsync(
        ContractRow contract, ContractVersionRow version, ContractContent content, Instant now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(content);
        var active = ContractStateModel.Machine.Fire(ContractStateModel.Parse(contract.Status), ContractTrigger.Activate);
        if (active.IsFailure)
        {
            return RiErrors.State(ContractStateModel.Parse(contract.Status), "activated");
        }

        contract.Status = ContractStateModel.Code(active.Value);
        contract.ActivatedAt = now;
        contract.UpdatedAt = now;
        contract.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RIContractActivatedV1.Descriptor),
            AggregateType,
            contract.ContractId.Value.ToString("D"),
            ActivatedPayload(contract, version, content),
            BusinessKeys.Empty.With("riContractId", contract.ContractId.Value.ToString("D"))));
        return Unit.Value;
    }

    /// <summary>Active → Expired at the period end; publishes <c>RIContractExpired</c>.</summary>
    public async Task<Result<Unit>> ExpireAsync(ContractRow contract, ContractVersionRow version, Instant now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(version);
        var expired = ContractStateModel.Machine.Fire(ContractStateModel.Parse(contract.Status), ContractTrigger.Expire);
        if (expired.IsFailure)
        {
            return RiErrors.State(ContractStateModel.Parse(contract.Status), "expired");
        }

        contract.Status = ContractStateModel.Code(expired.Value);
        contract.ExpiredAt = now;
        contract.UpdatedAt = now;
        contract.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(RIContractExpiredV1.Descriptor),
            AggregateType,
            contract.ContractId.Value.ToString("D"),
            new RIContractExpiredV1 { RiContractId = contract.ContractId, State = "EXPIRED", Date = version.ValidTo },
            BusinessKeys.Empty.With("riContractId", contract.ContractId.Value.ToString("D"))));
        return Unit.Value;
    }

    private static RIContractActivatedV1 ActivatedPayload(ContractRow contract, ContractVersionRow version, ContractContent content)
    {
        var eur = Currency.FromCode(content.Currency);
        var layers = content.Layers.OrderBy(l => l.LayerNo).Select(l => new RiLayer
        {
            LayerNo = l.LayerNo,
            Attachment = new Money(l.Attachment, eur),
            Limit = new Money(l.Limit, eur),
            Aad = new Money(l.Aad, eur),
            Aal = l.Aal is { } aal ? new Money(aal, eur) : null,
        }).ToList();
        return new RIContractActivatedV1
        {
            RiContractId = contract.ContractId,
            ContractNumber = contract.ContractNumber,
            StableTreatyId = contract.StableTreatyId,
            Year = contract.ContractYear,
            Version = version.VersionNo,
            ContractType = contract.ContractType,
            Period = DateRange.Of(content.ValidFrom, content.ValidTo),
            Currency = eur,
            SectionsAndLayers =
            [
                .. content.Layers.OrderBy(l => l.LayerNo).Select(l => JsonSerializer.SerializeToElement(new
                {
                    sectionNo = 1,
                    layerNo = l.LayerNo,
                    attachment = ContractContent.Fixed(l.Attachment, ContractContent.MoneyScale),
                    limit = ContractContent.Fixed(l.Limit, ContractContent.MoneyScale),
                    aad = ContractContent.Fixed(l.Aad, ContractContent.MoneyScale),
                    aal = l.Aal is { } aal ? ContractContent.Fixed(aal, ContractContent.MoneyScale) : null,
                })),
            ],
            Participants = [.. content.Lines.OrderByDescending(l => l.Lead).ThenBy(l => l.ReinsurerPartyId).Select(l => new Participant { ReinsurerPartyId = new PartyId(l.ReinsurerPartyId), SignedLine = l.SignedLinePct })],
            Layers = layers,
            PlacedPct = content.PlacedPct,
            RecoveriesInure = content.RecoveriesInure,
            AlaeIncluded = content.AlaeIncluded,
            StatutoryInterestIncluded = content.StatutoryInterestIncluded,
            ProductCodes = content.ProductCodes,
            CoverageCodes = content.CoverageCodes,
        };
    }
}
