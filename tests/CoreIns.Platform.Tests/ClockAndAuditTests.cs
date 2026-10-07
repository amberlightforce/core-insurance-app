using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Platform.Tests;

public sealed class ClockTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = "/";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void Production_always_uses_the_system_clock()
    {
        ClockConfiguration.Create(Config(), new Environment("Production")).ShouldBeSameAs(SystemClock.Instance);
        Should.Throw<InvalidOperationException>(() =>
            ClockConfiguration.Create(Config((ClockConfiguration.ModeKey, "Shiftable")), new Environment("Production")));
        Should.Throw<InvalidOperationException>(() =>
            ClockConfiguration.Create(Config((ClockConfiguration.ModeKey, "Fast")), new Environment("Development")));
    }

    [Fact]
    public void A_shiftable_clock_moves_and_freezes_outside_production()
    {
        var clock = ClockConfiguration.Create(
            Config((ClockConfiguration.ModeKey, "Shiftable"), (ClockConfiguration.OffsetKey, "30.00:00:00")), new Environment("Development"))
            .ShouldBeOfType<ShiftableClock>();

        (clock.Now - SystemClock.Instance.Now).ShouldBeGreaterThan(TimeSpan.FromDays(29));
        clock.Freeze(Instant.Parse("2027-01-01T00:00:00Z"));
        clock.Advance(TimeSpan.FromHours(1));
        clock.Now.ShouldBe(Instant.Parse("2027-01-01T01:00:00Z"));
        clock.Reset();
        (clock.Now - SystemClock.Instance.Now).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }
}

public sealed class AuditHashTests
{
    private static AuditRecord Record(string reason = "because") => new()
    {
        Actor = ActorRef.User("user-1"),
        Roles = ["Claims.Handler"],
        Operation = OperationName.Parse("clm.Reserve.change"),
        ObjectRef = new ObjectRef(ModuleCode.CLM, "Claim", Guid.CreateVersion7().ToString()),
        Changes =
        [
            new AuditChange("$.amount", JsonValue.Create("100.00"), JsonValue.Create("150.00")),
            new AuditChange("$.payee.iban", JsonValue.Create("GR16..."), JsonValue.Create("GR17..."), DataClassification.P2),
        ],
        Reason = reason,
        CorrelationId = CorrelationId.New(),
        BusinessKeys = BusinessKeys.Empty.With("claimId", "c-1"),
        LegalEntity = LegalEntityId.Parse("GR-TEST"),
        Jurisdiction = Jurisdiction.Parse("GR"),
        OccurredAt = Instant.Parse("2026-10-07T08:00:00Z"),
        RecordedAt = Instant.Parse("2026-10-07T08:00:01Z"),
    };

    [Fact]
    public void Personal_sensitive_values_are_redacted_in_the_stored_changes()
    {
        var changes = JsonNode.Parse(Record().ChangesJson())!.AsArray();

        changes[0]!["after"]!.GetValue<string>().ShouldBe("150.00");
        changes[1]!["after"]!["redacted"]!.GetValue<bool>().ShouldBeTrue();
        changes[1]!["pdClass"]!.GetValue<string>().ShouldBe("P2");
        Record().ChangesJson().ShouldNotContain("GR17");
    }

    [Fact]
    public void The_hash_covers_every_field_and_the_link()
    {
        var record = Record();
        var day = new DateOnly(2026, 10, 7);
        var a = StoredAudit.From(record, day, 1, AuditStore.Genesis(day));

        StoredAudit.From(record, day, 1, AuditStore.Genesis(day)).Hash.ShouldBe(a.Hash);
        StoredAudit.From(record with { Reason = "other" }, day, 1, AuditStore.Genesis(day)).Hash.ShouldNotBe(a.Hash);
        StoredAudit.From(record, day, 2, AuditStore.Genesis(day)).Hash.ShouldNotBe(a.Hash);
        StoredAudit.From(record, day, 1, a.Hash).Hash.ShouldNotBe(a.Hash);
        (a with { Outcome = "Rejected" }).ComputeHash().ShouldNotBe(a.Hash);
        AuditStore.Genesis(day).ShouldNotBe(AuditStore.Genesis(day.AddDays(1)));
    }

    [Fact]
    public void Diffs_list_changed_leaf_fields()
    {
        var changes = AuditDiff.Compute(new { a = 1, b = new { c = "x", d = "y" } }, new { a = 1, b = new { c = "z", d = "y" }, e = true });

        changes.Select(c => c.Field).ShouldBe(["$.b.c", "$.e"]);
        AuditDiff.Compute(null, new { a = 1 }).ShouldHaveSingleItem().Field.ShouldBe("$");
    }
}
