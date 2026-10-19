using CoreIns.SharedKernel.Dsar;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Services.Dsar;

/// <summary>
/// Singleton face of the module's DSAR participant (the registry is a singleton). Every step runs in its own DI scope, so the
/// database session and transaction belong to that step alone and the module commits or rolls back by itself.
/// </summary>
internal sealed class PolicyDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    public string Module => "POL";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).ExportAsync(subject, context, ct).ConfigureAwait(false);
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).RestrictAsync(subject, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).EraseAsync(subject, context, ct).ConfigureAwait(false);
    }

    private static PolicyDsarWork Work(AsyncServiceScope scope) =>
        ActivatorUtilities.CreateInstance<PolicyDsarWork>(scope.ServiceProvider);
}
