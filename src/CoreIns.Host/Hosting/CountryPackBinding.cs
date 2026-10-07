using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Addresses;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.CountryPacks.GR.Identifiers;
using CoreIns.CountryPacks.GR.Language;
using CoreIns.CountryPacks.GR.Transliteration;
using CoreIns.Modules.Market.Contracts.Localisation;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.Host.Hosting;

/// <summary>
/// Binds the country-pack SPI implementations of the stamp's country (<c>Stamp:Country</c>). Only the Host references
/// packs (architecture rule); modules see the SPIs from <c>CoreIns.Modules.Market.Contracts</c>. This is the interim
/// binding until MKT's <c>mkt.Spi.bind</c> resolves bindings per legal entity, axis and date (W1-MKT, REQ-MKT-002).
/// Every address formatter of the deployed packs is registered so addresses abroad are still formatted.
/// </summary>
internal static class CountryPackBinding
{
    public static IServiceCollection AddCountryPacks(this IServiceCollection services, IConfiguration configuration)
    {
        var country = configuration["Stamp:Country"] ?? GrPackCountry;
        switch (country)
        {
            case GrPackCountry:
                services.AddSingleton<IIdValidator>(sp => new GreekIdValidator(sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton<INameTransliterator, GreekNameTransliterator>();
                services.AddSingleton<IPackConfigurationSource, GrPackConfiguration>();
                break;
            case CyPack.Country:
                services.AddSingleton<IIdValidator>(sp => new CyIdValidator(sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton<INameTransliterator, CyNameTransliterator>();
                break;
            default:
                throw new InvalidOperationException($"No country pack is deployed for Stamp:Country '{country}'.");
        }

        // Greek is the language of both packs' customers (CY reuses the GR language data, D-ARC-20).
        services.AddSingleton<ILanguageRuleSet, GreekLanguageRules>();
        services.AddSingleton<IAddressFormatter>(_ => new GreekAddressFormatter());
        services.AddSingleton<IAddressFormatter, CyAddressFormatter>();
        return services;
    }

    private const string GrPackCountry = CoreIns.CountryPacks.GR.GrPack.Country;
}
