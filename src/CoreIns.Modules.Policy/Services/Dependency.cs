using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Services;

/// <summary>
/// An in-process contract of a module built in parallel (PFC, RAT, UW, MKT in slice batch S1, D-SLC-01), resolved when
/// POL first uses it. Until the owning module registers its implementation (S2 wiring), the Host still starts (the
/// container validates on build in Development) and the POL operation that needs it fails with
/// POL-ERR-DEPENDENCY-UNAVAILABLE (503) instead of the whole api failing. Once every owner registers, this indirection
/// can be replaced by plain constructor injection.
/// </summary>
/// <typeparam name="T">The generated <c>*.Contracts</c> interface.</typeparam>
internal sealed class Dependency<T>(IServiceProvider services)
    where T : class
{
    /// <summary>The registered implementation, or POL-ERR-DEPENDENCY-UNAVAILABLE.</summary>
    public T Value => services.GetService<T>()
                      ?? throw new DomainException(DomainError.Of(
                          ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", $"{typeof(T).Name} is not registered in this deployment yet."));

    /// <summary>The registered implementation, or null for an optional step.</summary>
    public T? TryValue => services.GetService<T>();
}
