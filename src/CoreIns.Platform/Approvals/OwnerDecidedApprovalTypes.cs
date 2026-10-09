using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform.Approvals;

/// <summary>
/// An approval type whose owning module decides it through its own operation (for refunds <c>bil.Refund.decide</c>), because
/// deciding it also executes the approved action (credit applied, ledger entries, payment). A decision in the generic approvals
/// inbox would leave the owner's object waiting forever, so the inbox refuses these (<c>PLT-ERR-OWNER-DECIDED</c>) and does not list
/// them; the owner calls <c>IPlatformApprovalService.DecideAsync</c> itself, which is not refused.
/// </summary>
/// <param name="Type">The approval type, e.g. <c>BIL.REFUND</c>.</param>
public sealed record OwnerDecidedApprovalType(string Type);

/// <summary>The registered owner-decided approval types.</summary>
public sealed class OwnerDecidedApprovalTypes(IEnumerable<OwnerDecidedApprovalType> registrations)
{
    private readonly HashSet<string> _types = new(registrations.Select(r => r.Type), StringComparer.Ordinal);

    /// <summary>True when the owning module decides requests of <paramref name="approvalType"/> itself.</summary>
    public bool Contains(string approvalType) => _types.Contains(approvalType);
}

/// <summary>Registration of owner-decided approval types.</summary>
public static class OwnerDecidedApprovalExtensions
{
    /// <summary>Declares that the module decides requests of <paramref name="approvalType"/> through its own operation (not the generic inbox).</summary>
    public static IServiceCollection AddOwnerDecidedApprovalType(this IServiceCollection services, string approvalType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalType);
        services.AddSingleton(new OwnerDecidedApprovalType(approvalType));
        return services;
    }
}
