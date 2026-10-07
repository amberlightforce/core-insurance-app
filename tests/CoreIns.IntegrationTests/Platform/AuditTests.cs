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
    public async Task The_application_role_cannot_corrupt_the_chain_head_or_skip_a_link()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        harness.Clock.Set(Instant.Parse("2026-11-20T09:00:00Z"));
        await harness.SendAsync(new CreateWidget("head", 1m));
        var ct = TestContext.Current.CancellationToken;

        foreach (var sql in new[]
                 {
                     "UPDATE plt.audit_chain_head SET last_sequence = 0",
                     "DELETE FROM plt.audit_chain_head",
                     "INSERT INTO plt.audit_chain_head (chain_date, last_sequence, last_hash) VALUES ('2030-01-01', 0, repeat('a', 64))",
                 })
        {
            await using var command = harness.DataSource.CreateCommand(sql);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // A forged row that does not extend the chain (wrong sequence, or right sequence with a wrong prev_hash) is refused.
        foreach (var (sequence, prev) in new[] { ("99", "repeat('0', 64)"), ("2", "repeat('0', 64)") })
        {
            await using var forged = harness.DataSource.CreateCommand(
                $$"""
                INSERT INTO plt.audit_event (audit_id, chain_date, sequence, prev_hash, hash, actor_kind, actor_id, role_codes, operation,
                    outcome, changes, correlation_id, business_keys, origin, legal_entity, jurisdiction, occurred_at, recorded_at)
                VALUES (gen_random_uuid(), '2026-11-20', {{sequence}}, {{prev}}, repeat('b', 64), 'USER', 'x', '{}', 'wrk.Widget.create',
                    'Succeeded', '[]', repeat('1', 32), '{}', 'LIVE', 'GR-TEST', 'GR', now(), now())
                """);
            (await Should.ThrowAsync<PostgresException>(() => forged.ExecuteNonQueryAsync(ct))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        (await Verifier(harness).VerifyAsync(new BusinessDate(2026, 11, 20), ct)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task A_rejection_is_audited_even_when_the_outer_commit_fails()
    {
        await using var harness = await PlatformHarness.CreateAsync(database, configure: s =>
            s.AddScoped<CoreIns.Platform.Persistence.ITransactionParticipant, FailOnceParticipant>());
        harness.Clock.Set(Instant.Parse("2026-11-21T09:00:00Z"));
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CoreIns.Platform.Context.RequestContext>();
            context.ConfigurationHash = PlatformHarness.Hash;
            context.IdempotencyKey = CoreIns.SharedKernel.Identifiers.IdempotencyKey.New();
            context.LegalEntity = CoreIns.SharedKernel.Identifiers.LegalEntityCode.Parse("GR-TEST");
            context.Jurisdiction = CoreIns.SharedKernel.Identifiers.Jurisdiction.Parse("GR");
            var session = scope.ServiceProvider.GetRequiredService<CoreIns.Platform.Persistence.DbSession>();
            var outer = await session.BeginTransactionAsync(ct);
            await using (outer)
            {
                // A joined command is refused (its audit record must survive), then the outer commit fails.
                var refused = await scope.ServiceProvider.GetRequiredService<CoreIns.Platform.Commands.ICommandHandler<CreateWidget, WidgetCreatedPayload>>()
                    .HandleAsync(new CreateWidget("nested-refused", 1m, Fail: "result"), ct);
                refused.IsFailure.ShouldBeTrue();
                FailOnceParticipant.Armed = true;
                await Should.ThrowAsync<InvalidOperationException>(() => outer.CommitAsync(ct));
            }
        }

        (await harness.CountAsync("SELECT count(*) FROM plt.audit_event WHERE chain_date = '2026-11-21' AND outcome = 'Rejected' AND error_code = 'WRK-ERR-WIDGET-REFUSED'"))
            .ShouldBe(1);
        (await harness.CountAsync("SELECT count(*) FROM tst.widget WHERE name = 'nested-refused'")).ShouldBe(0);
        (await Verifier(harness).VerifyAsync(new BusinessDate(2026, 11, 21), ct)).IsValid.ShouldBeTrue();
    }

    /// <summary>Makes the next commit fail after every other participant has written its rows.</summary>
    private sealed class FailOnceParticipant : CoreIns.Platform.Persistence.ITransactionParticipant
    {
        public static bool Armed { get; set; }

        public int Order => 5000;

        public Task BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            if (Armed)
            {
                Armed = false;
                throw new InvalidOperationException("commit failure injected by the test");
            }

            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AfterRollbackAsync(CoreIns.Platform.Persistence.DbSession session, CancellationToken cancellationToken) => Task.CompletedTask;
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
