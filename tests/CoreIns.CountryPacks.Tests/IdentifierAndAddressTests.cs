using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR;
using CoreIns.CountryPacks.GR.Addresses;
using CoreIns.CountryPacks.GR.Identifiers;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.Tests;

/// <summary>Plates (REQ-MKT-339, REQ-MKT-325), other GR schemes, CY stub TIC (REQ-MKT-264) and addresses (REQ-MKT-092, REQ-PTY-076).</summary>
public sealed class IdentifierAndAddressTests
{
    private readonly GreekIdValidator _greek = new();
    private readonly CyIdValidator _cyprus = new();

    [Fact]
    public async Task Latin_lookalike_plate_normalises_to_the_Greek_series_with_the_same_search_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var latin = await _greek.NormaliseAsync("ikx-1234", IdValidationContext.None, ct);
        var greek = await _greek.NormaliseAsync("ΙΚΧ1234", IdValidationContext.None, ct);

        latin.Normalised.ShouldBe("ΙΚΧ1234");
        latin.Findings.ShouldBe([PlateFindings.LookalikeNormalised]);
        greek.Normalised.ShouldBe("ΙΚΧ1234");
        greek.Findings.ShouldBeEmpty();
        (await _greek.SearchKeyAsync("ikx-1234", ct)).ShouldBe(await _greek.SearchKeyAsync("ΙΚΧ 1234", ct));
        latin.RuleSetId.ShouldBe(GrPack.PlateRuleSetId);
    }

    [Fact]
    public async Task Legacy_plate_typed_with_Latin_IKB_normalises_to_the_Greek_series()
    {
        // REQ-MKT-325 acceptance: ΙΚΒ1234 typed with Latin "IKB".
        var result = await _greek.NormaliseAsync("IKB1234", IdValidationContext.None, TestContext.Current.CancellationToken);
        result.Normalised.ShouldBe("ΙΚΒ1234");
        result.Findings.ShouldContain(PlateFindings.LookalikeNormalised);
    }

    [Theory]
    [InlineData("ΛΣΔ1234")] // Greek letters without a Latin look-alike
    [InlineData("QWE1234")] // Latin letters without a Greek counterpart
    public async Task Letters_outside_the_series_are_reported_without_blocking(string plate)
    {
        var result = await _greek.ValidateAsync(IdScheme.VehiclePlate, plate, IdValidationContext.None, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(IdValidationStatus.Unverified);
        result.Findings.ShouldContain(PlateFindings.InvalidSeries);
    }

    [Theory]
    [InlineData(GrSchemes.Passport, " ab 1234567 ", "AB1234567")]
    [InlineData(GrSchemes.NationalId, "ΑΚ 123456", "ΑΚ123456")]
    [InlineData(GrSchemes.Gemi, "123456789000", "123456789000")]
    public async Task Schemes_without_a_stated_format_are_normalised_but_not_validated(string scheme, string value, string normalised)
    {
        var result = await _greek.ValidateAsync(new IdScheme(scheme), value, IdValidationContext.None, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(IdValidationStatus.Unverified);
        result.Normalised.ShouldBe(normalised);
        result.Findings.ShouldBe([GreekIdValidator.FormatNotStated]);
    }

    [Fact]
    public async Task Unbound_scheme_is_not_applicable_and_verify_is_not_available()
    {
        var ct = TestContext.Current.CancellationToken;
        var error = await Should.ThrowAsync<SpiException>(async () =>
            await _greek.ValidateAsync(new IdScheme(CyPack.TicScheme), "60000001A", IdValidationContext.None, ct));
        error.Category.ShouldBe(SpiErrorCategory.NotApplicable);

        var verify = await _greek.VerifyAsync(new IdScheme(GrSchemes.Afm), "090000045", new IdEvidenceContext(), ct);
        verify.Status.ShouldBe(IdVerificationStatus.NotAvailable);
    }

    [Theory]
    [InlineData("60000001A", IdValidationStatus.ValidFormat)] // REQ-MKT-264
    [InlineData("12345678L", IdValidationStatus.ValidFormat)] // REQ-MKT-090 acceptance
    [InlineData("6000001A", IdValidationStatus.Invalid)] // REQ-MKT-264
    [InlineData("600000011", IdValidationStatus.Invalid)]
    [InlineData("A00000011", IdValidationStatus.Invalid)]
    public async Task Cyprus_TIC_is_format_only(string value, IdValidationStatus status)
    {
        var result = await _cyprus.ValidateAsync(new IdScheme(CyPack.TicScheme), value, IdValidationContext.None, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(status);
        result.ValidatorVersion.ShouldBe(CyPack.IdValidatorVersion);
    }

    [Fact]
    public async Task Cyprus_stub_does_not_bind_AFM_and_uses_the_core_default_for_plates()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<SpiException>(async () =>
            await _cyprus.ValidateAsync(new IdScheme(GrSchemes.Afm), "090000045", IdValidationContext.None, ct));

        var plate = await _cyprus.ValidateAsync(IdScheme.VehiclePlate, " kxa 123 ", IdValidationContext.None, ct);
        plate.Status.ShouldBe(IdValidationStatus.Unverified);
        plate.Normalised.ShouldBe("KXA 123");
    }

    [Fact]
    public async Task Greek_address_is_parsed_validated_and_formatted_in_both_scripts()
    {
        var ct = TestContext.Current.CancellationToken;
        var formatter = new GreekAddressFormatter();

        var address = await formatter.ParseAsync(["Λεωφ. Κηφισίας 124, 11526 Αθήνα"], "GR", ct);
        address.Street.ShouldBe("Λεωφ. Κηφισίας");
        address.Number.ShouldBe("124");
        address.Postcode.ShouldBe("11526");
        address.Locality.ShouldBe("Αθήνα");

        (await formatter.ValidateAsync(address, ct)).Status.ShouldBe(AddressValidationStatus.Valid);

        var greek = await formatter.FormatAsync(address, AddressPurpose.SingleLine, ScriptCodes.Greek, ct);
        greek.Lines.ShouldBe(["Λεωφ. Κηφισίας 124, 11526 Αθήνα"]);

        // REQ-PTY-012 acceptance: Latin form "Leof. Kifisias 124, 11526 Athina".
        var latin = await formatter.FormatAsync(address, AddressPurpose.SingleLine, ScriptCodes.Latin, ct);
        latin.Lines.ShouldBe(["Leof. Kifisias 124, 11526 Athina"]);

        var postal = await formatter.FormatAsync(address, AddressPurpose.Postal, ScriptCodes.Greek, ct);
        postal.Lines.ShouldBe(["Λεωφ. Κηφισίας 124", "11526 Αθήνα"]);
    }

    [Theory]
    [InlineData("10557", "Αθήνα", AddressValidationStatus.Valid, null)] // REQ-MKT-092
    [InlineData("106 74", "Αθήνα", AddressValidationStatus.Valid, null)]
    [InlineData("1055", "Αθήνα", AddressValidationStatus.Invalid, AddressErrorCodes.PostcodeFormat)] // REQ-MKT-092
    [InlineData("1152", "Αθήνα", AddressValidationStatus.Invalid, AddressErrorCodes.PostcodeFormat)] // REQ-PTY-076
    [InlineData("10557", " ", AddressValidationStatus.Invalid, AddressErrorCodes.MissingLocality)]
    public async Task Greek_postcodes_are_five_digits_and_a_locality_is_required(
        string postcode, string locality, AddressValidationStatus status, string? code)
    {
        var address = new PostalAddress { Country = "GR", Street = "Πανεπιστημίου", Number = "1", Postcode = postcode, Locality = locality };
        var result = await new GreekAddressFormatter().ValidateAsync(address, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(status);
        if (code is null)
        {
            result.Errors.ShouldBeEmpty();
            result.Normalised.Postcode!.Length.ShouldBe(5);
        }
        else
        {
            result.Errors.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
    }

    [Fact]
    public async Task Cyprus_stub_uses_four_digit_postcodes_and_English_first_formatting()
    {
        var ct = TestContext.Current.CancellationToken;
        var formatter = new CyAddressFormatter();
        var address = await formatter.ParseAsync(["Makariou Avenue 12", "1065 Nicosia"], "CY", ct);

        (await formatter.ValidateAsync(address, ct)).Status.ShouldBe(AddressValidationStatus.Valid);
        (await formatter.ValidateAsync(address with { Postcode = "10557" }, ct)).Errors.ShouldHaveSingleItem().Code.ShouldBe(AddressErrorCodes.PostcodeFormat);
        (await formatter.FormatAsync(address, AddressPurpose.SingleLine, ScriptCodes.Latin, ct)).Lines.ShouldBe(["Makariou Avenue 12, NICOSIA 1065"]);
        await Should.ThrowAsync<SpiException>(async () => await formatter.FormatAsync(address, AddressPurpose.Postal, ScriptCodes.Greek, ct));
    }
}
