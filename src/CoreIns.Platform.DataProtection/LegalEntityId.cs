namespace CoreIns.Platform.DataProtection;

/// <summary>
/// Legal entity owning the protected data; keys are per legal entity (D-ARC-14).
/// </summary>
/// <remarks>Local minimal type until CoreIns.SharedKernel publishes its strongly typed id (F-1b); then replaced.</remarks>
/// <param name="Value">The legal entity's UUID.</param>
public readonly record struct LegalEntityId(Guid Value)
{
    public override string ToString() => Value.ToString("N");
}

/// <summary>Supplies the legal entity of the current unit of work (used by the EF Core value converters).</summary>
public interface ICurrentLegalEntity
{
    /// <summary>The current legal entity; throws when none is set.</summary>
    LegalEntityId Current { get; }
}

/// <summary>
/// <see cref="ICurrentLegalEntity"/> held in an <see cref="AsyncLocal{T}"/>: set once per request or job with
/// <see cref="Enter"/>, flows across awaits, restored on dispose.
/// </summary>
public sealed class AmbientLegalEntity : ICurrentLegalEntity
{
    private static readonly AsyncLocal<LegalEntityId?> Value = new();

    public LegalEntityId Current =>
        Value.Value ?? throw new InvalidOperationException("No legal entity is set for the current unit of work.");

    /// <summary>Sets the current legal entity until the returned scope is disposed.</summary>
    public static IDisposable Enter(LegalEntityId legalEntity)
    {
        var previous = Value.Value;
        Value.Value = legalEntity;
        return new Scope(previous);
    }

    private sealed class Scope(LegalEntityId? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}
