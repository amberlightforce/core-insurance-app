using Serilog;
using Serilog.Events;
using Xunit;

namespace CoreIns.Platform.DataProtection.Tests;

public sealed class PiiDestructuringPolicyTests
{
    private sealed record Party(
        string Name,
        string Afm,
        [property: DataClass(DataClass.P2)] string Secret,
        [property: DataClass(DataClass.P1)] string Email);

    [Fact]
    public void Masks_p2_and_identifier_named_properties_but_keeps_others()
    {
        var events = new List<LogEvent>();
        using var logger = new LoggerConfiguration()
            .Destructure.With<PiiDestructuringPolicy>()
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();

        logger.Information("Party {@Party} iban {Iban}", new Party("Maria", "123456789", "GR1601101250000000012300695", "m@x.gr"), "n/a");

        var text = ((StructureValue)events.Single().Properties["Party"]).Properties.ToDictionary(p => p.Name, p => p.Value.ToString());
        Assert.Equal("\"***\"", text["Afm"]);
        Assert.Equal("\"***\"", text["Secret"]);
        Assert.Equal("\"Maria\"", text["Name"]);
        Assert.Equal("\"m@x.gr\"", text["Email"]);
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
