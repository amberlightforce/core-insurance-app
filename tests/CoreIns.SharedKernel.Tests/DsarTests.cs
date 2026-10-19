using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.SharedKernel.Tests;

public sealed class DsarTests
{
    private sealed class Fake(string module) : IDsarParticipant
    {
        public string Module => module;

        public Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
            Task.FromResult(new DsarModuleResult(module, [DsarRecordOutcome.NotHeld("X")], "{}"));

        public Task<DsarModuleResult> RestrictAsync(
            SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default) =>
            Task.FromResult(new DsarModuleResult(module, [DsarRecordOutcome.Restricted("X", legalBasis)]));

        public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
            Task.FromResult(new DsarModuleResult(module, [DsarRecordOutcome.NotBuilt("X")]));
    }

    [Fact]
    public void Registry_resolves_participants_and_reports_missing()
    {
        var reg = new DsarParticipantRegistry([new Fake("BIL"), new Fake("PTY")]);
        Assert.Equal(["BIL", "PTY"], reg.All.Select(p => p.Module));
        Assert.NotNull(reg.Find("bil"));
        Assert.Equal(["WRK"], reg.Missing(["BIL", "WRK"]));
    }

    [Fact]
    public void Registry_rejects_duplicate_modules() =>
        Assert.Throws<InvalidOperationException>(() => new DsarParticipantRegistry([new Fake("BIL"), new Fake("BIL")]));

    [Fact]
    public void Retention_policy_marks_regulated_classes_restrict_only()
    {
        var p = new DsarRetentionPolicy();
        foreach (var c in new[] { "LEDGER", "INVOICE", "CLAIM_FILE", "POLICY_DOCUMENT", "FISCAL_DOCUMENT", "BUREAU_FACT" })
        {
            Assert.True(p.IsRestrictOnly(c));
        }
        Assert.False(p.IsRestrictOnly("MARKETING_PREFERENCE"));
        var o = p.RestrictInsteadOfErase("LEDGER", "LEGAL_OBLIGATION");
        Assert.Equal(DsarOutcomeKind.Restricted, o!.Kind);
        Assert.Equal(DsarRetention.UnsetF404, o.RetentionEnd);
        Assert.Null(p.RestrictInsteadOfErase("MARKETING_PREFERENCE", "x"));
    }

    [Fact]
    public async Task NotBuilt_blocks_and_subject_tostring_has_no_identifiers()
    {
        var s = new SubjectRef(PartyId.New(), new Dictionary<string, IReadOnlyList<string>> { ["policyNumber"] = ["P-SECRET"] });
        Assert.DoesNotContain("P-SECRET", s.ToString());
        var r = await new Fake("WRK").EraseAsync(s, new DsarContext(Guid.NewGuid(), "u", "dpo"), TestContext.Current.CancellationToken);
        Assert.True(r.HasNotBuilt);
    }
}
