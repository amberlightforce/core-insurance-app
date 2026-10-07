using CoreIns.CountryPacks.GR.Fiscal;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.Tests;

/// <summary>The slice's myDATA stub (REQ-CMP-001 shape only): synthetic, visibly marked, deterministic, no AADE codes.</summary>
public sealed class MyDataStubFiscalChannelTests
{
    private readonly MyDataStubFiscalChannel _channel = new();

    private static FiscalSource Source(string documentType = "UNMAPPED-OQ-012") => new()
    {
        LegalEntityId = Guid.CreateVersion7(),
        SourceType = "TRANSACTION",
        SourceId = Guid.CreateVersion7().ToString(),
        DocumentType = documentType,
        IssueDate = new DateOnly(2026, 10, 7),
        CounterpartyRef = Guid.CreateVersion7().ToString(),
        Lines = [new FiscalSourceLine("PREMIUM-NONLIFE", new SpiMoney(312.35m, "EUR"), [])],
    };

    [Fact]
    public async Task REQ_CMP_046_the_stub_registers_with_a_marked_synthetic_MARK_and_UID()
    {
        var document = await _channel.BuildAsync(Source(), TestContext.Current.CancellationToken);
        document.ContentType.ShouldContain("fiscal-stub");
        document.DocumentType.ShouldBe("UNMAPPED-OQ-012"); // passed through: the stub invents no document type

        var result = await _channel.SubmitAsync(document, "TRANSACTION|x|ISSUE|0", TestContext.Current.CancellationToken);
        result.Status.ShouldBe(FiscalSubmissionStatus.Registered);
        result.RegistrationId.ShouldNotBeNull().ShouldStartWith(MyDataStubFiscalChannel.StubPrefix);
        result.Uid.ShouldNotBeNull().ShouldStartWith(MyDataStubFiscalChannel.StubPrefix);
        result.Uid!.Length.ShouldBeLessThanOrEqualTo(64);
        result.Rejections.ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ_CMP_031_the_same_key_returns_the_same_identifiers_and_another_key_differs()
    {
        var document = await _channel.BuildAsync(Source(), TestContext.Current.CancellationToken);
        var first = await _channel.SubmitAsync(document, "k1", TestContext.Current.CancellationToken);
        var again = await _channel.SubmitAsync(document, "k1", TestContext.Current.CancellationToken);
        var other = await _channel.SubmitAsync(document, "k2", TestContext.Current.CancellationToken);
        again.ShouldBe(first with { Rejections = again.Rejections });
        other.RegistrationId.ShouldNotBe(first.RegistrationId);
        (await _channel.SeriesAsync("UNMAPPED-OQ-012", TestContext.Current.CancellationToken)).SeriesId.ShouldBe(MyDataStubFiscalChannel.StubSeries);
    }

    [Fact]
    public async Task A_source_without_lines_is_refused()
    {
        var empty = Source() with { Lines = [] };
        var error = await Should.ThrowAsync<SpiException>(() => _channel.BuildAsync(empty, TestContext.Current.CancellationToken));
        error.Category.ShouldBe(SpiErrorCategory.Validation);
    }
}
