namespace CoreIns.Modules.Market.Contracts.Spi;

/// <summary>One shipped, immutable version of a pack's data (REQ-MKT-129, D-SL5-07).</summary>
/// <param name="Version">Semantic version, for example <c>0.2.0</c>.</param>
/// <param name="Values">The complete value set of this version (never a delta).</param>
public sealed record PackVersionData(string Version, IReadOnlyList<PackConfigValue> Values);

/// <summary>
/// A pack source that ships several versions of its data (SL5-MKT-STATE, D-SL5-07). <see cref="IPackConfigurationSource.PackVersion"/>
/// and <see cref="IPackConfigurationSource.Values"/> are the newest version, so a consumer that only knows the base interface
/// keeps working. A version never changes once shipped: MKT records its content digest and refuses to start when the shipped
/// data of a recorded version differs.
/// </summary>
public interface IVersionedPackConfigurationSource : IPackConfigurationSource
{
    /// <summary>Every shipped version, oldest first; the last one equals <see cref="IPackConfigurationSource.PackVersion"/>.</summary>
    IReadOnlyList<PackVersionData> Versions { get; }
}
