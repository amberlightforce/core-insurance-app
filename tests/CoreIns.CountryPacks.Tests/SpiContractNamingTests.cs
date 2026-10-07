using System.Reflection;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.Tests;

/// <summary>D-ARC-21 / D-API-08: valid-time parameters and fields of the SPI contracts are named <c>validAt</c>.</summary>
public sealed class SpiContractNamingTests
{
    private static readonly string[] ForbiddenNames = ["asOf", "asAt", "date", "versionOrAsAt"];

    private static IEnumerable<Type> SpiTypes =>
        typeof(IIdValidator).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(IIdValidator).Namespace);

    [Fact]
    public void No_SPI_method_parameter_uses_a_PRD_spelling_of_the_valid_time()
    {
        var offending = SpiTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters().Select(parameter => (method, parameter)))
            .Where(entry => ForbiddenNames.Contains(entry.parameter.Name, StringComparer.OrdinalIgnoreCase))
            .Select(entry => $"{entry.method.DeclaringType!.Name}.{entry.method.Name}({entry.parameter.Name})")
            .ToList();

        offending.ShouldBeEmpty();
    }

    [Fact]
    public void No_SPI_record_has_a_property_named_like_a_PRD_valid_time_spelling()
    {
        var offending = SpiTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => ForbiddenNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToList();

        offending.ShouldBeEmpty();
        typeof(NumberingContext).GetProperty(nameof(NumberingContext.ValidAt)).ShouldNotBeNull();
    }
}
