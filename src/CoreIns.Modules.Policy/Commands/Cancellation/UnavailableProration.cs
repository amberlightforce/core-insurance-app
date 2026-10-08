using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary>
/// The production default of <see cref="IProration"/> until SL3-POL-WIRING binds RAT's real proration: fail closed with
/// <c>POL-ERR-DEPENDENCY-UNAVAILABLE</c>. <see cref="ReferenceProration"/> is test-only and is registered by tests alone.
/// Registered with <c>TryAdd</c>, so one registration survives next to POL-CHANGE's.
/// </summary>
internal sealed class UnavailableProration : IProration
{
    public ProrationFraction Fraction(DayCountConvention convention, int days, int termDays) =>
        throw new DomainException(DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "The proration service (rat.Proration.prorate) is not wired in this deployment yet."));
}
