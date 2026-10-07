using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Addresses;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.CountryPacks.GR.Fiscal;
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
/// Every address formatter of the deployed packs is registered so addresses abroad are still formatted. The GR fiscal
/// channel is the slice's myDATA stub and is never bound in Production.
/// </summary>
internal static class CountryPackBinding
{
    public static IServiceCollection AddCountryPacks(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var country = configuration["Stamp:Country"] ?? GrPackCountry;
        switch (country)
        {
            case GrPackCountry:
                services.AddSingleton<IIdValidator>(sp => new GreekIdValidator(sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton<INameTransliterator, GreekNameTransliterator>();
                services.AddSingleton<IPackConfigurationSource, GrPackConfiguration>();

                // FiscalDocumentChannel: the myDATA STUB (no external call, synthetic MARK/UID) outside Production only.
                // Production has no channel until the real adapter (W5-CMP-01); CMP then fails closed
                // (CMP-ERR-TRANSPORT-UNAVAILABLE) and BIL records the fiscal request as failed without blocking billing.
                if (!environment.IsProduction())
                {
                    services.AddSingleton<IFiscalDocumentChannel, MyDataStubFiscalChannel>();
                }

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
