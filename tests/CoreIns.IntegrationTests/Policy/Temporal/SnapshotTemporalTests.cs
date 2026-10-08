using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Queries;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Temporal;

/// <summary>
/// Reads under the record-time watermark (D-SL3-03 a, c): knownAt clamping, forged references, byte-stable re-reads across an
/// uncommitted and then committed write, live supersession, and the Athens end-of-day resolution of a date-form validAt.
/// </summary>
public sealed class SnapshotTemporalTests(PostgresFixture database) : TemporalTestBase(database)
{
    private static string Json(object value) => JsonSerializer.Serialize(value, SharedKernelJson.Options);

    private static SnapshotQuery ByPolicy(Bound p, DateTime validAt, DateTime? knownAt = null) =>
        new(p.PolicyId, null, null, Instant.FromUtcDateTime(validAt), knownAt is { } k ? Instant.FromUtcDateTime(k) : null);

    private static SnapshotQuery ByRef(string snapshotRef) => new(null, null, snapshotRef, null, null);

    // ---- knownAt is clamped to the watermark; a future knownAt is never accepted -------------------------------------------------

    [Fact]
    public async Task REQ_POL_007_a_knownAt_above_the_watermark_is_clamped_to_it()
    {
        var p = await BindAsync();
        var watermark = await WatermarkAsync(p.PolicyId);
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var snapshots = Snapshots(scope);
        var inside = p.Start.AddDays(30);

        // "Now" and a point between the watermark and now both mean "as known at the watermark".
        foreach (var requested in new DateTime?[] { null, watermark.ToUtcDateTime().AddTicks(10) })
        {
            var result = await snapshots.GetDetailedAsync(ByPolicy(p, inside, requested), Ct);
            result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
            result.Value.EffectiveKnownAt.ShouldBe(watermark);
            result.Value.Snapshot.KnownAt.ShouldBe(watermark);
            result.Value.Snapshot.SnapshotRef.ShouldEndWith("." + ((long)((watermark.ToUtcDateTime() - DateTime.UnixEpoch).Ticks / 10)).ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task REQ_POL_007_a_later_commit_moves_the_effective_knownAt_to_the_new_watermark()
    {
        var p = await BindAsync();
        var w0 = await WatermarkAsync(p.PolicyId);
        var written = await WriteAsync(p.PolicyId, new FakeClock(Instant.FromUtcDateTime(w0.ToUtcDateTime().AddSeconds(-5))), TimeSpan.FromSeconds(5));
        written.Value.ShouldBe(w0 + TimeSpan.FromTicks(10));

        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var result = await Snapshots(scope).GetDetailedAsync(ByPolicy(p, p.Start.AddDays(30)), Ct);

        result.Value.EffectiveKnownAt.ShouldBe(written.Value);
    }

    [Fact]
    public async Task REQ_POL_007_PITFALL_13_no_future_knownAt_is_accepted_from_any_input()
    {
        var p = await BindAsync();
        var future = DateTime.UtcNow.AddDays(1);
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var snapshots = Snapshots(scope);

        var direct = await snapshots.GetDetailedAsync(ByPolicy(p, p.Start.AddDays(30), future), Ct);
        direct.IsFailure.ShouldBeTrue();
        direct.Error!.Code.ToString().ShouldBe("POL-ERR-VALIDATION");

        var (response, raw) = await RawAsync($"/api/pol/v1/snapshots/get?policyId={p.PolicyId}&knownAt={Q(Iso(future))}");
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, raw);
        JsonNode.Parse(raw).Text("code").ShouldBe("POL-ERR-VALIDATION");

        // Searching by policy number clamps too: the future cannot be asked of any read.
        var (byNumber, rawByNumber) = await RawAsync($"/api/pol/v1/snapshots/get?policyNumber={p.PolicyNumber}&knownAt={Q(Iso(future))}");
        byNumber.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, rawByNumber);
    }

    [Fact]
    public async Task REQ_POL_007_a_forged_snapshot_ref_above_the_watermark_is_refused_and_one_at_the_watermark_is_not()
    {
        var p = await BindAsync();
        var inside = p.Start.AddDays(30);
        var (_, good) = await RawAsync($"/api/pol/v1/snapshots/get?policyId={p.PolicyId}&validAt={Q(Iso(inside))}");
        var goodRef = JsonNode.Parse(good).Text("snapshotRef");
        var watermark = await WatermarkMicrosAsync(p.PolicyId);

        string WithKnownAt(long micros)
        {
            var parts = goodRef.Split('.');
            parts[4] = micros.ToString(CultureInfo.InvariantCulture);
            return string.Join('.', parts);
        }

        // Exactly the watermark: what POL issues.
        var atWatermark = await RawAsync($"/api/pol/v1/snapshots/get?snapshotRef={Q(WithKnownAt(watermark))}");
        atWatermark.Response.StatusCode.ShouldBe(HttpStatusCode.OK, atWatermark.Raw);

        // One microsecond above, and far above, though still before "now" for the one-microsecond case: never issued by POL.
        foreach (var forged in new[] { watermark + 1, watermark + 1_000, watermark + 3_600_000_000L })
        {
            var (response, raw) = await RawAsync($"/api/pol/v1/snapshots/get?snapshotRef={Q(WithKnownAt(forged))}");
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableContent, $"knownAt {forged}: {raw}");
            JsonNode.Parse(raw).Text("code").ShouldBe("POL-ERR-VALIDATION");
        }

        // In process (what CLM calls) the same.
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var result = await Snapshots(scope).GetDetailedAsync(ByRef(WithKnownAt(watermark + 1)), Ct);
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ToString().ShouldBe("POL-ERR-VALIDATION");
    }

    // ---- stability across an uncommitted, then committed write ---------------------------------------------------------------

    [Fact]
    public async Task REQ_POL_007_P5_a_read_during_an_uncommitted_write_is_byte_identical_when_re_read_by_ref_after_the_commit()
    {
        var p = await BindAsync();
        var inside = p.Start.AddDays(30);
        var watermark = await WatermarkAsync(p.PolicyId);
        var clock = new FakeClock(Instant.FromUtcDateTime(watermark.ToUtcDateTime().AddHours(1)));
        var writerHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        string duringRaw = null!;
        SnapshotResult? during = null;
        var writer = Task.Run(() => WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(30),
            body: (db, t) => RecutSegmentAsync(db, p.TermId, t.ToUtcDateTime(), split: false, changeContent: true, sequence: 2),
            whileOpen: async _ =>
            {
                writerHasLock.SetResult();
                await readDone.Task; // the lock, the new watermark and the new segment exist but are not committed
            }), Ct);
        await writerHasLock.Task;

        try
        {
            // Readers take no lock: this does not wait for the writer, and sees the world before it.
            var (response, raw) = await RawAsync($"/api/pol/v1/snapshots/get?policyId={p.PolicyId}&validAt={Q(Iso(inside))}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
            duringRaw = raw;
            await using var scope = Slice.Factory.Services.CreateAsyncScope();
            during = (await Snapshots(scope).GetDetailedAsync(ByPolicy(p, inside), Ct)).Value;
        }
        finally
        {
            readDone.SetResult();
        }

        var written = await writer;
        written.IsSuccess.ShouldBeTrue();
        during!.EffectiveKnownAt.ShouldBe(watermark);
        during.Snapshot.Content!.Coverages.Count.ShouldBe(2);
        during.Supersession.Superseded.ShouldBeFalse();

        var duringNode = JsonNode.Parse(duringRaw)!;
        var snapshotRef = duringNode.Text("snapshotRef");
        var (afterResponse, afterRaw) = await RawAsync($"/api/pol/v1/snapshots/get?snapshotRef={Q(snapshotRef)}");
        afterResponse.StatusCode.ShouldBe(HttpStatusCode.OK, afterRaw);
        var afterNode = JsonNode.Parse(afterRaw)!;

        // The immutable part is the same bytes. (supersession, a live block beside it, is allowed to differ: compared below.)
        afterNode.Text("snapshotRef").ShouldBe(snapshotRef);
        afterNode["content"]!.ToJsonString().ShouldBe(duringNode["content"]!.ToJsonString());
        afterNode["contentHash"]?.ToJsonString().ShouldBe(duringNode["contentHash"]?.ToJsonString());
        afterNode.Text("knownAt").ShouldBe(duringNode.Text("knownAt"));

        // A fresh read now shows the new world, under the writer's record time, with a different ref.
        await using var after = Slice.Factory.Services.CreateAsyncScope();
        var fresh = (await Snapshots(after).GetDetailedAsync(ByPolicy(p, inside), Ct)).Value;
        fresh.EffectiveKnownAt.ShouldBe(written.Value);
        fresh.Snapshot.Content!.Coverages.Count.ShouldBe(0);
        fresh.Snapshot.SnapshotRef.ShouldNotBe(snapshotRef);

        // The old reference re-read in process: same snapshot bytes, now reported as superseded by exactly the writer's change.
        var reread = (await Snapshots(after).GetDetailedAsync(ByRef(snapshotRef), Ct)).Value;
        Json(reread.Snapshot).ShouldBe(Json(during.Snapshot));
        reread.Supersession.Superseded.ShouldBeTrue();
        reread.Supersession.SuccessorRef.ShouldBe(fresh.Snapshot.SnapshotRef);
        reread.Supersession.SupersededAt.ShouldBe(written.Value);
    }

    // ---- supersession ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REQ_POL_007_c_a_split_with_identical_content_is_not_superseded_and_a_content_change_is()
    {
        var p = await BindAsync();
        var inside = p.Start.AddDays(30);
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var snapshots = Snapshots(scope);

        var original = (await snapshots.GetDetailedAsync(ByPolicy(p, inside), Ct)).Value;
        original.Supersession.Superseded.ShouldBeFalse();
        original.Supersession.SuccessorRef.ShouldBeNull();
        original.Supersession.SupersededAt.ShouldBeNull();
        var originalJson = Json(original.Snapshot);
        var originalHash = PolicySnapshots.SnapshotContentHash(original.Snapshot);
        var originalSegment = original.Snapshot.Content!.Segment.SegmentId;

        var clock = new FakeClock(Instant.FromUtcDateTime(DateTime.UtcNow));

        // 1. A mid-term split with identical content: new segment ids, same content, so not superseded.
        var split = await WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(5),
            body: (db, t) => RecutSegmentAsync(db, p.TermId, t.ToUtcDateTime(), split: true, changeContent: false, sequence: 2));
        split.IsSuccess.ShouldBeTrue();
        var afterSplit = (await snapshots.GetDetailedAsync(ByRef(original.Snapshot.SnapshotRef), Ct)).Value;
        (await snapshots.GetDetailedAsync(ByPolicy(p, inside), Ct)).Value.Snapshot.Content!.Segment.SegmentId.ShouldNotBe(originalSegment);
        afterSplit.Supersession.Superseded.ShouldBeFalse();
        afterSplit.Supersession.SuccessorRef.ShouldBeNull();
        Json(afterSplit.Snapshot).ShouldBe(originalJson);
        PolicySnapshots.SnapshotContentHash(afterSplit.Snapshot).ShouldBe(originalHash);

        // 2. A content change: superseded, with a successor reference at the same valid instant and the record time of the change.
        var change = await WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(5),
            body: (db, t) => RecutSegmentAsync(db, p.TermId, t.ToUtcDateTime(), split: false, changeContent: true, sequence: 3));
        change.IsSuccess.ShouldBeTrue();
        var afterChange = (await snapshots.GetDetailedAsync(ByRef(original.Snapshot.SnapshotRef), Ct)).Value;
        afterChange.Supersession.Superseded.ShouldBeTrue();
        afterChange.Supersession.SupersededAt.ShouldBe(change.Value);
        afterChange.Supersession.SuccessorRef.ShouldNotBeNullOrEmpty();
        afterChange.Supersession.SuccessorRef.ShouldNotBe(original.Snapshot.SnapshotRef);

        // The supersession is outside the hashed content: the snapshot, its content hash and its ref are unchanged by the flip.
        Json(afterChange.Snapshot).ShouldBe(originalJson);
        PolicySnapshots.SnapshotContentHash(afterChange.Snapshot).ShouldBe(originalHash);
        afterChange.Snapshot.SnapshotRef.ShouldBe(original.Snapshot.SnapshotRef);

        // The successor reference is itself current and re-reads to the new content, not superseded.
        var successor = (await snapshots.GetDetailedAsync(ByRef(afterChange.Supersession.SuccessorRef!), Ct)).Value;
        successor.Supersession.Superseded.ShouldBeFalse();
        successor.Snapshot.Content!.Coverages.Count.ShouldBe(0);
    }

    [Fact]
    public async Task REQ_POL_007_c_supersession_is_per_valid_instant_a_change_that_leaves_the_other_dates_alone_does_not_supersede_them()
    {
        // The split at day 60 plus a content change only in the second half (valid from day 60) leaves day 30 untouched.
        var p = await BindAsync();
        var early = p.Start.AddDays(30);
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var snapshots = Snapshots(scope);
        var original = (await snapshots.GetDetailedAsync(ByPolicy(p, early), Ct)).Value;

        var clock = new FakeClock(Instant.FromUtcDateTime(DateTime.UtcNow));
        (await WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(5),
            body: (db, t) => RecutSegmentAsync(db, p.TermId, t.ToUtcDateTime(), split: true, changeContent: false, sequence: 2))).IsSuccess.ShouldBeTrue();
        (await WriteAsync(p.PolicyId, clock, TimeSpan.FromSeconds(5), body: async (db, t) =>
        {
            // Recut only the second half: close it at t and replace it with a content-changed copy.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                    configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
                SELECT gen_random_uuid(), policy_id, term_id, gen_random_uuid(), legal_entity_id, 'CHANGE', 3, effective_at, {1},
                    configuration_hash, artefact_hash, resolution_hash, intent, 0, 0, 0, currency, actor, correlation_id, origin
                  FROM pol.policy_transaction WHERE term_id = {0} AND sequence = 1;
                WITH closed AS (
                    UPDATE pol.segment SET recorded_to = {1}
                     WHERE term_id = {0} AND recorded_to IS NULL AND valid_from > (SELECT min(valid_from) FROM pol.segment WHERE term_id = {0} AND recorded_to IS NULL)
                 RETURNING *)
                INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
                SELECT gen_random_uuid(), c.term_id, c.policy_id, (SELECT transaction_id FROM pol.policy_transaction WHERE term_id = {0} AND sequence = 3),
                       c.legal_entity_id, c.valid_from, c.valid_to, {1}, repeat('c', 64), jsonb_set(c.snapshot, ARRAY['coverages'], '[]'::jsonb)
                  FROM closed c
                """, [p.TermId, t.ToUtcDateTime()]);
        })).IsSuccess.ShouldBeTrue();

        var earlyAfter = (await snapshots.GetDetailedAsync(ByRef(original.Snapshot.SnapshotRef), Ct)).Value;
        earlyAfter.Supersession.Superseded.ShouldBeFalse();
        var late = (await snapshots.GetDetailedAsync(ByPolicy(p, p.Start.AddDays(90)), Ct)).Value;
        late.Snapshot.Content!.Coverages.Count.ShouldBe(0);
        // A reference taken for day 90 before any of this (same knownAt as the original) is superseded.
        var lateOld = (await snapshots.GetDetailedAsync(ByPolicy(p, p.Start.AddDays(90), original.EffectiveKnownAt.ToUtcDateTime()), Ct)).Value;
        lateOld.Snapshot.Content!.Coverages.Count.ShouldBe(2);
        (await snapshots.GetDetailedAsync(ByRef(lateOld.Snapshot.SnapshotRef), Ct)).Value.Supersession.Superseded.ShouldBeTrue();
    }

    // ---- date-form validAt ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-25", 2026, 10, 25, 22)] // DST ends (25-hour day): Athens is UTC+2 at the next midnight
    [InlineData("2027-03-28", 2027, 3, 28, 21)] // DST starts (23-hour day): Athens is UTC+3 at the next midnight
    [InlineData("2027-01-15", 2027, 1, 15, 22)] // an ordinary winter day
    [InlineData("2027-07-15", 2027, 7, 15, 21)] // an ordinary summer day
    public async Task REQ_POL_007_PITFALL_14_a_date_form_validAt_is_the_last_microsecond_of_that_Athens_day_including_DST_days(string day, int year, int month, int dayOfMonth, int utcHourOfNextMidnight)
    {
        var p = await BindAsync();
        var date = new DateTime(year, month, dayOfMonth, 0, 0, 0, DateTimeKind.Utc);
        p.Start.ShouldBeLessThan(date.AddDays(-1), "the term must cover the day under test");
        p.End.ShouldBeGreaterThan(date.AddDays(2));

        var (response, raw) = await RawAsync($"/api/pol/v1/snapshots/get?policyId={p.PolicyId}&validAt={day}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        // Next Athens midnight expressed in UTC, minus one microsecond (the instants are microsecond-precise).
        var expected = new DateTime(year, month, dayOfMonth, utcHourOfNextMidnight, 0, 0, DateTimeKind.Utc);
        if (utcHourOfNextMidnight is 22 or 21)
        {
            expected = expected.AddTicks(-10);
        }

        Utc(JsonNode.Parse(raw).Text("validAt")).ShouldBe(expected);
        JsonNode.Parse(raw).Text("inForce").ShouldBe("true");
    }
}
