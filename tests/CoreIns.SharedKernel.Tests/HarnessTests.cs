using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FsCheck.Xunit;

namespace CoreIns.SharedKernel.Tests;

/// <summary>Proves the unit and property-based test harness runs against the SharedKernel assembly.</summary>
public sealed class HarnessTests
{
    [Fact]
    public void SharedKernel_assembly_has_no_internal_references()
    {
        // Read the metadata without loading the assembly for execution.
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "CoreIns.SharedKernel.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        metadata.GetString(metadata.GetAssemblyDefinition().Name).ShouldBe("CoreIns.SharedKernel");
        metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ShouldAllBe(name => !name.StartsWith("CoreIns.", StringComparison.Ordinal));
    }

    /// <summary>Decimal survives an invariant-culture text round trip exactly (money is never floating point).</summary>
    [Property]
    public bool Decimal_round_trips_through_invariant_text(decimal value) =>
        decimal.Parse(value.ToString(CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture) == value;
}
