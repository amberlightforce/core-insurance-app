using CoreIns.Modules.Claims.Services.Ai;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>NX-HD-13: structured identifiers are removed before FNOL text reaches an AI provider (no database needed).</summary>
public sealed class FnolRedactorTests
{
    [Theory]
    [InlineData("mail me at nikos.p@example.gr please", "nikos.p@example.gr")]
    [InlineData("pay to GR16 0110 1250 0000 0001 2300 695 thanks", "GR16 0110 1250 0000 0001 2300 695")]
    [InlineData("card 4111 1111 1111 1111 was used", "4111 1111 1111 1111")]
    [InlineData("call 6912345678 now", "6912345678")]
    [InlineData("call +30 2101234567 now", "2101234567")]
    [InlineData("my AFM is 123456789.", "123456789")]
    public void Structured_identifiers_are_replaced(string input, string secret)
    {
        var output = FnolRedactor.Redact(input);
        output.ShouldNotContain(secret);
        output.ShouldContain("[");
    }

    [Fact]
    public void Plate_and_dates_needed_for_drafting_are_kept()
    {
        const string text = "Collision on 2026-03-14 at 14/03/2026, plate ABC-1234, car stopped.";
        FnolRedactor.Redact(text).ShouldBe(text);
    }
}
