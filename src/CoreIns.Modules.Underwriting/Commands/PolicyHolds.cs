using System.Text.Json;
using System.Text.RegularExpressions;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using FluentValidation;
using Microsoft.Extensions.Configuration;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary><c>uw.PolicyHold.create</c>: a Draft hold on business by place, product, line or coverage (REQ-UW-007).</summary>
internal sealed record CreatePolicyHold(PolicyHoldCreateRequest Request) : ICommand<PolicyHoldCreateResponse>;

/// <summary><c>uw.PolicyHold.update</c>: change a Draft hold.</summary>
internal sealed record UpdatePolicyHold(Guid Id, PolicyHoldUpdateRequest Request) : ICommand<PolicyHoldUpdateResponse>;

/// <summary><c>uw.PolicyHold.copy</c>: a new Draft hold from an existing one.</summary>
internal sealed record CopyPolicyHold(PolicyHoldCopyRequest Request) : ICommand<PolicyHoldCopyResponse>;

/// <summary><c>uw.PolicyHold.activate</c>: a Draft hold starts to apply.</summary>
internal sealed record ActivatePolicyHold(PolicyHoldActivateRequest Request) : ICommand<PolicyHoldActivateResponse>;

/// <summary><c>uw.PolicyHold.release</c>: an Active hold stops applying.</summary>
internal sealed record ReleasePolicyHold(PolicyHoldReleaseRequest Request) : ICommand<PolicyHoldReleaseResponse>;

internal static partial class PolicyHoldInput
{
    [GeneratedRegex("^[A-Z][A-Z0-9_-]{1,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex CodePattern();

    public static string Dimension(HoldScopeEntry.DimensionValue d) => d switch
    {
        HoldScopeEntry.DimensionValue.Postcode => HoldDimension.Postcode,
        HoldScopeEntry.DimensionValue.HazardZone => HoldDimension.HazardZone,
        HoldScopeEntry.DimensionValue.Product => HoldDimension.Product,
        HoldScopeEntry.DimensionValue.LineOfBusiness => HoldDimension.LineOfBusiness,
        _ => HoldDimension.Coverage,
    };

    /// <summary>Distinct scope values, upper-cased and trimmed (postcodes without spaces).</summary>
    public static IReadOnlyList<(string Dimension, string Value)> Scopes(IEnumerable<HoldScopeEntry> scopes) =>
        [.. scopes.Select(s => (Dimension(s.Dimension), Dimension(s.Dimension) == HoldDimension.Postcode ? HoldMatcher.Normalise(s.Value.Trim()) : s.Value.Trim().ToUpperInvariant())).Distinct()];

    public static string[] JobTypes(IEnumerable<string> types) => [.. types.Select(t => t.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal)];
}

internal sealed class CreatePolicyHoldValidator : AbstractValidator<CreatePolicyHold>
{
    public CreatePolicyHoldValidator()
    {
        RuleFor(c => c.Request.HoldCode).NotEmpty().Must(t => PolicyHoldInput.CodePattern().IsMatch(t ?? string.Empty)).WithMessage("holdCode must be an upper-case code.");
        RuleFor(c => c.Request.Description).NotEmpty().Must(t => !string.IsNullOrWhiteSpace(t)).MaximumLength(1000);
        RuleFor(c => c.Request.Scopes).NotEmpty().WithMessage("A hold needs at least one scope value.").Must(s => s.Count <= 200);
        RuleForEach(c => c.Request.Scopes).ChildRules(s =>
        {
            s.RuleFor(x => x.Dimension).IsInEnum();
            s.RuleFor(x => x.Value).NotEmpty().Must(v => !string.IsNullOrWhiteSpace(v)).MaximumLength(64);
        });
        RuleFor(c => c.Request).Must(r => r.EffectiveTo is null || r.EffectiveTo.Value.Value >= r.EffectiveFrom.Value).WithMessage("effectiveTo cannot be before effectiveFrom.");
    }
}

internal sealed class UpdatePolicyHoldValidator : AbstractValidator<UpdatePolicyHold>
{
    public UpdatePolicyHoldValidator()
    {
        RuleFor(c => c.Id).NotEqual(Guid.Empty);
        RuleFor(c => c.Request).Must(r => r.Description is not null || r.EffectiveFrom is not null || r.EffectiveTo is not null || r.JobTypes is not null || r.Scopes is not null)
            .WithMessage("Give at least one field to change.");
        RuleFor(c => c.Request.Description).MaximumLength(1000).Must(t => t is null || !string.IsNullOrWhiteSpace(t));
        RuleFor(c => c.Request.Scopes).Must(s => s is null || (s.Count is > 0 and <= 200)).WithMessage("scopes cannot be empty.");
        RuleForEach(c => c.Request.Scopes!).ChildRules(s =>
        {
            s.RuleFor(x => x.Dimension).IsInEnum();
            s.RuleFor(x => x.Value).NotEmpty().Must(v => !string.IsNullOrWhiteSpace(v)).MaximumLength(64);
        }).When(c => c.Request.Scopes is not null);
    }
}

internal sealed class CopyPolicyHoldValidator : AbstractValidator<CopyPolicyHold>
{
    public CopyPolicyHoldValidator()
    {
        RuleFor(c => c.Request.HoldId.Value).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.HoldCode).NotEmpty().Must(t => PolicyHoldInput.CodePattern().IsMatch(t ?? string.Empty)).WithMessage("holdCode must be an upper-case code.");
    }
}

internal sealed class ActivatePolicyHoldValidator : AbstractValidator<ActivatePolicyHold>
{
    public ActivatePolicyHoldValidator() => RuleFor(c => c.Request.HoldId.Value).NotEqual(Guid.Empty);
}

internal sealed class ReleasePolicyHoldValidator : AbstractValidator<ReleasePolicyHold>
{
    public ReleasePolicyHoldValidator()
    {
        RuleFor(c => c.Request.HoldId.Value).NotEqual(Guid.Empty);
        RuleFor(c => c.Request.Reason).NotEmpty().Must(r => !string.IsNullOrWhiteSpace(r)).MaximumLength(1000).WithErrorCode("REASON");
    }
}

/// <summary>Shared steps of the hold commands: the legal entity, the JSON views, the impact count and the events.</summary>
internal sealed class PolicyHoldSupport(PolicyHoldStore store, RequestContext context, ILegalEntityDirectory legalEntities, IPolicyInForceQuery inForce, IEventPublisher events)
{
    public PolicyHoldStore Store => store;

    public string Me => context.Actor.ToString();

    public Guid LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    public DomainError? RequireHuman(string what) =>
        context.Actor.Kind == ActorKind.User ? null : DomainError.Of(ModuleCode.UW, "HUMAN-DECISION-REQUIRED", $"Only a person may {what}.");

    public static DomainError NotFound(Guid id) => DomainError.Of(ModuleCode.UW, "HOLD-NOT-FOUND", $"Policy hold {id:D} was not found.");

    public static DomainError State(string message) => DomainError.Of(ModuleCode.UW, "HOLD-STATE", message);

    public static DomainError Duplicate(string code) => DomainError.Of(ModuleCode.UW, "CODE-DUPLICATE", $"A policy hold with code {code} exists already.");

    public static DomainError Stale(Guid id) =>
        new(ErrorCode.For(ModuleCode.UW, "STALE"), "The hold changed since it was read; reload it and try again.")
        { Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["holdId"] = id.ToString("D") } };

    public async Task<(HoldWithScopes? Row, DomainError? Error)> LockAsync(Guid id, string? requiredStatus, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(LegalEntity, id, forUpdate: true, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return (null, NotFound(id));
        }

        return requiredStatus is null || row.Hold.Status == requiredStatus ? (row, null) : (null, State($"The hold is {row.Hold.Status}; this needs a {requiredStatus} hold."));
    }

    public static JsonElement View(HoldWithScopes h) => JsonSerializer.SerializeToElement(new
    {
        id = h.Hold.HoldId,
        holdCode = h.Hold.HoldCode,
        description = h.Hold.Description,
        status = h.Hold.Status,
        effectiveFrom = h.Hold.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        effectiveTo = h.Hold.To?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        jobTypes = h.Hold.JobTypes,
        scopes = h.Scopes.Select(s => new { dimension = s.Dimension, value = s.Value }),
        copiedFrom = h.Hold.CopiedFrom,
        recordVersion = h.Hold.RecordVersion,
    }, SharedKernelJson.Options);

    /// <summary>In-force policies the hold touches now, by product (POL capability "in-force by location and product").</summary>
    public async Task<JsonElement> ImpactAsync(HoldWithScopes h, CancellationToken cancellationToken)
    {
        var products = h.Scopes.Where(s => s.Dimension == HoldDimension.Product).Select(s => s.Value).ToList();
        var postcodes = h.Scopes.Where(s => s.Dimension == HoldDimension.Postcode).Select(s => s.Value).ToList();
        var uncounted = h.Scopes.Select(s => s.Dimension).Where(d => d is HoldDimension.HazardZone or HoldDimension.LineOfBusiness or HoldDimension.Coverage).Distinct().ToList();
        // TODO(hardening): hazard zones, lines and coverages are not on POL's current rows yet; they stay out of the count and are named in uncountedDimensions.
        var count = await inForce.CountAsync(new InForceCriteria(products, postcodes), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            inForcePolicies = count.Policies,
            byProduct = count.ByProduct,
            uncountedDimensions = uncounted,
            asOf = store.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }, SharedKernelJson.Options);
    }

    public static BusinessKeys Keys(Guid holdId) => BusinessKeys.Empty.With("holdId", holdId.ToString("D"));

    private static Instant Day(DateOnly d) => Instant.FromUtcDateTime(DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));

    public void PublishActivated(HoldWithScopes h) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyHoldActivatedV1.Descriptor), "PolicyHold", h.Hold.HoldId.ToString("D"),
            new PolicyHoldActivatedV1
            {
                HoldId = new PolicyHoldId(h.Hold.HoldId),
                HoldCode = h.Hold.HoldCode,
                Scope = new CoreIns.Platform.Contracts.Common.HoldScope
                {
                    Lines = [.. h.Scopes.Where(s => s.Dimension == HoldDimension.LineOfBusiness).Select(s => s.Value)],
                    JobTypes = h.Hold.JobTypes,
                    RegionCodes = [.. h.Scopes.Where(s => s.Dimension is HoldDimension.Postcode or HoldDimension.HazardZone).Select(s => s.Value)],
                },
                Start = Day(h.Hold.From),
                End = h.Hold.To is { } to ? Day(to) : null,
            },
            Keys(h.Hold.HoldId)));

    public void PublishReleased(HoldWithScopes h) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyHoldReleasedV1.Descriptor), "PolicyHold", h.Hold.HoldId.ToString("D"),
            new PolicyHoldReleasedV1 { HoldId = new PolicyHoldId(h.Hold.HoldId), HoldCode = h.Hold.HoldCode, ReleasedAt = Instant.FromUtcDateTime(store.Now) },
            Keys(h.Hold.HoldId)));
}

internal sealed class CreatePolicyHoldHandler(PolicyHoldSupport support) : ICommandHandler<CreatePolicyHold, PolicyHoldCreateResponse>
{
    public async Task<Result<PolicyHoldCreateResponse>> HandleAsync(CreatePolicyHold command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (await support.Store.CodeExistsAsync(support.LegalEntity, request.HoldCode, cancellationToken).ConfigureAwait(false))
        {
            return PolicyHoldSupport.Duplicate(request.HoldCode);
        }

        var row = new PolicyHoldRecord
        {
            HoldId = Guid.CreateVersion7(),
            LegalEntityId = support.LegalEntity,
            HoldCode = request.HoldCode,
            Description = request.Description.Trim(),
            Status = HoldStatus.Draft,
            EffectiveFrom = request.EffectiveFrom.Value.ToDateTime(TimeOnly.MinValue),
            EffectiveTo = request.EffectiveTo?.Value.ToDateTime(TimeOnly.MinValue),
            JobTypes = PolicyHoldInput.JobTypes(request.JobTypes ?? []),
            CreatedAt = support.Store.Now,
            CreatedBy = support.Me,
            RecordVersion = 1,
        };
        var scopes = PolicyHoldInput.Scopes(request.Scopes);
        await support.Store.InsertAsync(row, scopes, cancellationToken).ConfigureAwait(false);
        var hold = new HoldWithScopes(row, scopes);
        return new PolicyHoldCreateResponse { Hold = PolicyHoldSupport.View(hold), ImpactCounts = await support.ImpactAsync(hold, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class UpdatePolicyHoldHandler(PolicyHoldSupport support) : ICommandHandler<UpdatePolicyHold, PolicyHoldUpdateResponse>
{
    public async Task<Result<PolicyHoldUpdateResponse>> HandleAsync(UpdatePolicyHold command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var (row, error) = await support.LockAsync(command.Id, HoldStatus.Draft, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        if (request.RecordVersion is { } expected && expected != row.Hold.RecordVersion)
        {
            return PolicyHoldSupport.Stale(command.Id);
        }

        var from = request.EffectiveFrom?.Value ?? row.Hold.From;
        var to = request.EffectiveTo is { } t ? t.Value : row.Hold.To;
        if (to is { } end && end < from)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "effectiveTo cannot be before effectiveFrom.");
        }

        await support.Store.UpdateAsync(
            command.Id, request.Description?.Trim(), request.EffectiveFrom?.Value, request.EffectiveTo?.Value, request.EffectiveTo is not null,
            request.JobTypes is null ? null : PolicyHoldInput.JobTypes(request.JobTypes), request.Scopes is null ? null : PolicyHoldInput.Scopes(request.Scopes), cancellationToken).ConfigureAwait(false);
        var updated = (await support.Store.GetAsync(support.LegalEntity, command.Id, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        return new PolicyHoldUpdateResponse { Hold = PolicyHoldSupport.View(updated), ImpactCounts = await support.ImpactAsync(updated, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class CopyPolicyHoldHandler(PolicyHoldSupport support) : ICommandHandler<CopyPolicyHold, PolicyHoldCopyResponse>
{
    public async Task<Result<PolicyHoldCopyResponse>> HandleAsync(CopyPolicyHold command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var (source, error) = await support.LockAsync(request.HoldId.Value, null, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return error!;
        }

        if (await support.Store.CodeExistsAsync(support.LegalEntity, request.HoldCode, cancellationToken).ConfigureAwait(false))
        {
            return PolicyHoldSupport.Duplicate(request.HoldCode);
        }

        var row = new PolicyHoldRecord
        {
            HoldId = Guid.CreateVersion7(),
            LegalEntityId = support.LegalEntity,
            HoldCode = request.HoldCode,
            Description = source.Hold.Description,
            Status = HoldStatus.Draft,
            EffectiveFrom = source.Hold.EffectiveFrom,
            EffectiveTo = source.Hold.EffectiveTo,
            JobTypes = source.Hold.JobTypes,
            CopiedFrom = source.Hold.HoldId,
            CreatedAt = support.Store.Now,
            CreatedBy = support.Me,
            RecordVersion = 1,
        };
        await support.Store.InsertAsync(row, source.Scopes, cancellationToken).ConfigureAwait(false);
        var hold = new HoldWithScopes(row, source.Scopes);
        return new PolicyHoldCopyResponse { Hold = PolicyHoldSupport.View(hold), ImpactCounts = await support.ImpactAsync(hold, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ActivatePolicyHoldHandler(PolicyHoldSupport support) : ICommandHandler<ActivatePolicyHold, PolicyHoldActivateResponse>
{
    public async Task<Result<PolicyHoldActivateResponse>> HandleAsync(ActivatePolicyHold command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman("activate a policy hold") is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var (row, error) = await support.LockAsync(request.HoldId.Value, HoldStatus.Draft, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        if (request.RecordVersion is { } expected && expected != row.Hold.RecordVersion)
        {
            return PolicyHoldSupport.Stale(row.Hold.HoldId);
        }

        await support.Store.ActivateAsync(row.Hold.HoldId, support.Me, cancellationToken).ConfigureAwait(false);
        var active = (await support.Store.GetAsync(support.LegalEntity, row.Hold.HoldId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        support.PublishActivated(active);
        return new PolicyHoldActivateResponse { Hold = PolicyHoldSupport.View(active), ImpactCounts = await support.ImpactAsync(active, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ReleasePolicyHoldHandler(PolicyHoldSupport support) : ICommandHandler<ReleasePolicyHold, PolicyHoldReleaseResponse>
{
    public async Task<Result<PolicyHoldReleaseResponse>> HandleAsync(ReleasePolicyHold command, CancellationToken cancellationToken)
    {
        if (support.RequireHuman("release a policy hold") is { } notHuman)
        {
            return notHuman;
        }

        var request = command.Request;
        var (row, error) = await support.LockAsync(request.HoldId.Value, HoldStatus.Active, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return error!;
        }

        await support.Store.ReleaseAsync(row.Hold.HoldId, support.Me, request.Reason.Trim(), cancellationToken).ConfigureAwait(false);
        var released = (await support.Store.GetAsync(support.LegalEntity, row.Hold.HoldId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        support.PublishReleased(released);
        return new PolicyHoldReleaseResponse { Hold = PolicyHoldSupport.View(released), ImpactCounts = await support.ImpactAsync(released, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>
/// Hold reads: <c>uw.PolicyHold.check</c> (which Active holds apply to a quote), <c>uw.PolicyHold.impact</c> and <c>uw.Accumulation.check</c>.
/// The same matcher runs inside <c>uw.Rules.evaluate</c>, so POL's bind gate sees a hold as an open <c>UW_POLICY_HOLD</c> issue.
/// </summary>
internal sealed class PolicyHoldQueries(PolicyHoldStore store, PolicyHoldSupport support, IPolicyInForceQuery inForce, IConfiguration configuration)
{
    public async Task<PolicyHoldCheckResponse> CheckAsync(PolicyHoldCheckRequest request, CancellationToken cancellationToken)
    {
        var facts = new HoldFacts(
            request.JobType, request.Product, request.LineOfBusiness, request.Coverages,
            [.. request.RiskLocations.Where(l => !string.IsNullOrWhiteSpace(l.Postcode)).Select(l => l.Postcode!)],
            [.. request.RiskLocations.Where(l => !string.IsNullOrWhiteSpace(l.HazardZone)).Select(l => l.HazardZone!)],
            request.EffectiveDate.Value);
        var holds = await ApplicableAsync(facts, cancellationToken).ConfigureAwait(false);
        return new PolicyHoldCheckResponse
        {
            Holds = [.. holds.Select(h => new PolicyHoldCheckResponse.HoldItem { HoldId = new PolicyHoldId(h.HoldId), HoldCode = h.Code, Description = h.Description })],
            IssueType = holds.Count > 0 ? HoldIssue.IssueType : null,
        };
    }

    public async Task<IReadOnlyList<HoldDefinition>> ApplicableAsync(HoldFacts facts, CancellationToken cancellationToken) =>
        [.. (await store.ActiveAsync(support.LegalEntity, facts.EffectiveDate, cancellationToken).ConfigureAwait(false)).Select(h => h.Definition).Where(d => HoldMatcher.Applies(d, facts))];

    public async Task<Result<PolicyHoldImpactResponse>> ImpactAsync(PolicyHoldImpactRequest request, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(support.LegalEntity, request.HoldId.Value, forUpdate: false, cancellationToken).ConfigureAwait(false) is not { } hold)
        {
            return PolicyHoldSupport.NotFound(request.HoldId.Value);
        }

        return new PolicyHoldImpactResponse { Hold = PolicyHoldSupport.View(hold), ImpactCounts = await support.ImpactAsync(hold, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// The in-force exposure of a zone against the capacity configured for the peril (<c>Underwriting:Accumulation:Capacity:{PERIL}</c>, policies).
    /// Bands: GREEN below 80 percent, AMBER below 100, RED from 100. No configured capacity is UW-ERR-RI-UNAVAILABLE (503).
    /// TODO(hardening): take capacity and exposure from RI zone accumulation (BO-RI-19, sums insured) instead of policy counts.
    /// </summary>
    public async Task<Result<AccumulationCheckResponse>> AccumulationAsync(AccumulationCheckRequest request, CancellationToken cancellationToken)
    {
        if (configuration.GetValue<int?>($"Underwriting:Accumulation:Capacity:{request.Peril.ToUpperInvariant()}") is not { } limit || limit <= 0)
        {
            return DomainError.Of(ModuleCode.UW, "RI-UNAVAILABLE", $"No accumulation capacity is configured for peril {request.Peril}.");
        }

        if (string.IsNullOrWhiteSpace(request.Zone.Postcode))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "zone.postcode is required (hazard-zone accumulation needs RI zone data).");
        }

        var count = await inForce.CountAsync(
            new InForceCriteria(string.IsNullOrWhiteSpace(request.Product) ? [] : [request.Product], [HoldMatcher.Normalise(request.Zone.Postcode)]), cancellationToken).ConfigureAwait(false);
        var percent = (long)count.Policies * 100 / limit;
        return new AccumulationCheckResponse
        {
            Band = percent >= 100 ? AccumulationCheckResponse.BandValue.Red : percent >= 80 ? AccumulationCheckResponse.BandValue.Amber : AccumulationCheckResponse.BandValue.Green,
            Exposure = JsonSerializer.SerializeToElement(new { policyCount = count.Policies }, SharedKernelJson.Options),
            Capacity = JsonSerializer.SerializeToElement(new { policyCount = limit }, SharedKernelJson.Options),
            DataAs = Instant.FromUtcDateTime(store.Now),
        };
    }
}

/// <summary>Audit facts of the hold commands: the hold, the action and the reason given.</summary>
internal sealed class PolicyHoldAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result)
    {
        Guid? id = command switch
        {
            UpdatePolicyHold c => c.Id,
            CopyPolicyHold c => c.Request.HoldId.Value,
            ActivatePolicyHold c => c.Request.HoldId.Value,
            ReleasePolicyHold c => c.Request.HoldId.Value,
            _ => null,
        };
        JsonElement? view = result is { IsSuccess: true } ok
            ? ok.Value switch
            {
                PolicyHoldCreateResponse r => r.Hold,
                PolicyHoldCopyResponse r => r.Hold,
                _ => null,
            }
            : null;
        if (view is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("id", out var idProperty) && idProperty.TryGetGuid(out var known))
        {
            id = known;
        }

        var action = command switch
        {
            CreatePolicyHold => "CREATE",
            UpdatePolicyHold => "UPDATE",
            CopyPolicyHold => "COPY",
            ActivatePolicyHold => "ACTIVATE",
            _ => "RELEASE",
        };
        var reason = command is ReleasePolicyHold r2 ? r2.Request.Reason : null;
        return new CommandAuditFacts
        {
            ObjectRef = id is { } oid ? new ObjectRef(ModuleCode.UW, "PolicyHold", oid.ToString("D")) : null,
            BusinessKeys = id is { } key ? BusinessKeys.Empty.With("holdId", key.ToString("D")) : BusinessKeys.Empty,
            Changes = Platform.Audit.AuditDiff.Compute(new { }, new { action, reason }),
        };
    }
}
