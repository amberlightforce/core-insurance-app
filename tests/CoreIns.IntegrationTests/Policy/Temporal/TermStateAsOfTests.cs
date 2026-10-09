using System.Net;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Temporal;

/// <summary>
/// The as-of status of a cancelled term: Cancelled only from its cancellation date (<c>cancelled_at</c>), the state the period
/// gives before it. The knownAt dimension is separate: before the cancellation was recorded, it is not cancelled at all.
/// </summary>
public sealed class TermStateAsOfTests(PostgresFixture database) : TemporalTestBase(database)
{
    private async Task<JsonNode> PolicyAtAsync(Bound p, DateTime validAt, DateTime? knownAt = null)
    {
        var path = $"/api/pol/v1/policies/{p.PolicyId}?validAt={Q(Iso(validAt))}" + (knownAt is { } k ? $"&knownAt={Q(Iso(k))}" : string.Empty);
        var (response, raw) = await RawAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        return JsonNode.Parse(raw)!;
    }

    [Fact]
    public async Task REQ_POL_131_a_cancelled_term_is_cancelled_only_from_its_cancellation_date()
    {
        var p = await BindAsync();
        var cancelledAt = p.Start.AddDays(10);
        var before = await WatermarkAsync(p.PolicyId);

        var written = await WriteAsync(p.PolicyId, new FakeClock(Instant.FromUtcDateTime(DateTime.UtcNow)), TimeSpan.FromSeconds(5),
            body: async (db, t) =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE pol.policy_term SET recorded_to = {1} WHERE term_id = {0} AND recorded_to IS NULL;
                    INSERT INTO pol.policy_term (term_version_id, term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, recorded_from, state,
                        product_version, artefact_hash, resolution_hash, configuration_hash, currency, payment_plan_ref, written_date, head_transaction_id, created_by,
                        cancelled_at)
                    SELECT gen_random_uuid(), term_id, policy_id, legal_entity_id, term_number, valid_from, valid_to, {1}, 'CANCELLED',
                        product_version, artefact_hash, resolution_hash, configuration_hash, currency, payment_plan_ref, written_date, head_transaction_id, created_by,
                        {2}
                      FROM pol.policy_term WHERE term_id = {0} AND recorded_to = {1};
                    """, [p.TermId, t.ToUtcDateTime(), cancelledAt]);
            });
        written.IsSuccess.ShouldBeTrue();

        // Before the term starts: not yet in force (the cancellation lies in the term's future).
        (await PolicyAtAsync(p, p.Start.AddDays(-1))).Text("term.state").ShouldBe("SCHEDULED");

        // From the term start up to the cancellation date: in force, as it was then.
        foreach (var validAt in new[] { p.Start, p.Start.AddDays(5), cancelledAt.AddTicks(-10) })
        {
            var body = await PolicyAtAsync(p, validAt);
            body.Text("term.state").ShouldBe("IN_FORCE", Iso(validAt));
            body.Text("policy.status").ShouldBe("IN_FORCE", Iso(validAt));
        }

        // From the cancellation date on: cancelled.
        foreach (var validAt in new[] { cancelledAt, p.Start.AddDays(20) })
        {
            var body = await PolicyAtAsync(p, validAt);
            body.Text("term.state").ShouldBe("CANCELLED", Iso(validAt));
            body.Text("policy.status").ShouldBe("CANCELLED", Iso(validAt));
        }

        // The knownAt dimension is untouched: before the cancellation was recorded, the term is not cancelled for any validAt.
        (await PolicyAtAsync(p, p.Start.AddDays(20), before.ToUtcDateTime())).Text("term.state").ShouldBe("IN_FORCE");
    }
}
