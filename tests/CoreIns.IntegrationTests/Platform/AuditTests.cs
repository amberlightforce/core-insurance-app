using CoreIns.Platform.Audit;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>Insert-only, hash-chained audit on a real PostgreSQL 17 (D-ARC-15, ADR §2 rule 9).</summary>
public sealed class AuditTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly BusinessDate Day = new(2026, 10, 7);

    [Fact]
    public async Task Every_command_is_audited_and_the_day_chain_verifies()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var before = await harness.CountAsync("SELECT count(*) FROM plt.audit_event");

        var ok = await harness.SendAsync(new CreateWidget("audited", 7m));
        var refused = await harness.SendAsync(new CreateWidget("refused", 7m, Fail: "result"));
        await Should.ThrowAsync<InvalidOperationException>(() => harness.SendAsync(new CreateWidget("thrown", 7m, Fail: "throw")));
        await harness.SendAsync(new CreateWidget("dry", 7m), dryRun: true);

        ok.IsSuccess.ShouldBeTrue();
        refused.IsFailure.ShouldBeTrue();
        (await harness.CountAsync("SELECT count(*) FROM plt.audit_event") - before).ShouldBe(3, "success, rejection and failure; no dry run");
        (await harness.CountAsync("SELECT count(*) FROM plt.audit_event WHERE outcome = 'Rejected' AND error_code = 'WRK-ERR-WIDGET-REFUSED'")).ShouldBeGreaterThanOrEqualTo(1);
        (await harness.CountAsync("SELECT count(*) FROM plt.audit_event WHERE outcome = 'Failed' AND error_code = 'PLT-ERR-INTERNAL'")).ShouldBeGreaterThanOrEqualTo(1);
        (await harness.CountAsync(
            "SELECT count(*) FROM plt.audit_event WHERE operation = 'wrk.Widget.create' AND actor_kind = 'USER' AND actor_id = 'user-1' "
            + "AND role_codes = ARRAY['Tester'] AND legal_entity = 'GR-TEST' AND jurisdiction = 'GR' AND origin = 'LIVE'")).ShouldBeGreaterThanOrEqualTo(3);

        var verification = await Verifier(harness).VerifyAsync(Day, TestContext.Current.CancellationToken);
        verification.IsValid.ShouldBeTrue(verification.Problem);
        verification.Records.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [InlineData("UPDATE plt.audit_event SET reason = 'edited'")]
    [InlineData("DELETE FROM plt.audit_event")]
    [InlineData("TRUNCATE plt.audit_event")]
    public async Task The_application_role_cannot_change_or_remove_audit_records(string sql)
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        await harness.SendAsync(new CreateWidget("protected", 1m));

        await using var command = harness.DataSource.CreateCommand(sql);
        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Even_the_owner_is_refused_by_the_trigger()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        await harness.SendAsync(new CreateWidget("owner", 1m));

        var error = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsMigratorAsync(
            "UPDATE plt.audit_event SET reason = 'edited by owner'", TestContext.Current.CancellationToken));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.ShouldContain("insert-only");
    }

    [Fact]
    public async Task Tampering_by_a_superuser_is_detected()
    {
        // A day of its own, so the other tests' chain stays intact.
        await using var harness = await PlatformHarness.CreateAsync(database);
        harness.Clock.Set(Instant.Parse("2026-11-01T09:00:00Z"));
        var day = new BusinessDate(2026, 11, 1);
        for (var i = 0; i < 4; i++)
        {
            await harness.SendAsync(new CreateWidget($"t{i}", i));
        }

        var verifier = Verifier(harness);
        var ct = TestContext.Current.CancellationToken;
        (await verifier.VerifyAsync(day, ct)).IsValid.ShouldBeTrue();
        const string where = "chain_date = '2026-11-01'";

        await Tamper($"UPDATE plt.audit_event SET reason = 'rewritten' WHERE {where} AND sequence = 2", ct);
        var edited = await verifier.VerifyAsync(day, ct);
        edited.IsValid.ShouldBeFalse();
        edited.BrokenAtSequence.ShouldBe(2);
        await Tamper($"UPDATE plt.audit_event SET reason = NULL WHERE {where} AND sequence = 2", ct);
        (await verifier.VerifyAsync(day, ct)).IsValid.ShouldBeTrue("restoring the original value restores the chain");

        await Tamper($"UPDATE plt.audit_event SET occurred_at = occurred_at + interval '1 second' WHERE {where} AND sequence = 3", ct);
        (await verifier.VerifyAsync(day, ct)).BrokenAtSequence.ShouldBe(3);
        await Tamper($"UPDATE plt.audit_event SET occurred_at = occurred_at - interval '1 second' WHERE {where} AND sequence = 3", ct);

        await Tamper($"DELETE FROM plt.audit_event WHERE {where} AND sequence = 4", ct);
        var truncated = await verifier.VerifyAsync(day, ct);
        truncated.IsValid.ShouldBeFalse();
        truncated.Problem!.ShouldContain("head");

        await Tamper($"DELETE FROM plt.audit_event WHERE {where} AND sequence = 1", ct);
        (await verifier.VerifyAsync(day, ct)).BrokenAtSequence.ShouldBe(1);
    }

    private static AuditChainVerifier Verifier(PlatformHarness harness) => harness.Services.GetRequiredService<AuditChainVerifier>();

    /// <summary>Superuser with triggers disabled for the session: the attack the hash chain must reveal.</summary>
    private Task Tamper(string sql, CancellationToken cancellationToken) =>
        database.ExecuteAsSuperuserAsync($"SET session_replication_role = replica; {sql}; SET session_replication_role = origin;", cancellationToken);
}
