using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Services.Dsar;

/// <summary>
/// CHN's part in a data-subject request (CHN-304). Withdrawal declarations are the evidence that a statutory right was
/// exercised: restrict-only with retention end UNSET_F404 (D-SL6-07). Withdrawal rows carry no party id, so they are found
/// through the subject's known identifiers <c>policyId</c> and <c>email</c>.
/// </summary>
internal sealed class ChannelsDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    internal const string WithdrawalClass = "WITHDRAWAL_REQUEST";
    private const string Basis = "Evidence of the exercised right of withdrawal (GDPR Art. 17(3)(b)/(e); retention period unset, F-404)";

    public string Module => "CHN";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var rows = await CollectAsync(scope.ServiceProvider, db, subject, ct).ConfigureAwait(false);
        var outcome = rows.Count == 0
            ? DsarRecordOutcome.NotHeld(WithdrawalClass)
            : DsarRecordOutcome.Restricted(WithdrawalClass, Basis, rows.Count) with { Detail = "exported" };
        return new DsarModuleResult(Module, [outcome], JsonSerializer.Serialize(new Dictionary<string, object> { [WithdrawalClass] = rows }));
    }

    public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default) =>
        ApplyAsync(subject, reason, legalBasis, retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
        ApplyAsync(subject, "DSAR erase request", Basis, null, ct);

    private async Task<DsarModuleResult> ApplyAsync(SubjectRef subject, string reason, string legalBasis, string? retentionEnd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var count = (await CollectAsync(scope.ServiceProvider, db, subject, ct).ConfigureAwait(false)).Count;
        var outcome = await DsarRestrictions.RestrictAsync(db, scope.ServiceProvider.GetRequiredService<IClock>(), subject.PartyId, WithdrawalClass, count, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, [outcome]);
    }

    private static async Task<List<object>> CollectAsync(IServiceProvider services, ChannelsDbContext db, SubjectRef subject, CancellationToken ct)
    {
        // TODO(hardening): withdrawal rows hold no party id; match by the party's other identifiers once CHN stores the party.
        var policyIds = new List<PolicyId>();
        foreach (var text in Identifiers(subject, "policyId"))
        {
            if (Guid.TryParse(text, out var guid))
            {
                policyIds.Add(new PolicyId(guid));
            }
        }

        // The fan-out knows the party only: the withdrawals are found through the policies the party holds (read-only lookup in POL).
        if (policyIds.Count == 0)
        {
            var session = services.GetRequiredService<DbSession>();
            var connection = await session.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = new Npgsql.NpgsqlCommand("SELECT policy_id FROM pol.policy WHERE policyholder_party_id = @p", connection, session.Transaction);
            command.Parameters.AddWithValue("p", NpgsqlTypes.NpgsqlDbType.Uuid, subject.PartyId.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                policyIds.Add(new PolicyId(reader.GetGuid(0)));
            }
        }

        // TODO(hardening): e-mail match is exact (case-sensitive); normalise at write time.
        var emails = Identifiers(subject, "email").ToList();
        if (policyIds.Count == 0 && emails.Count == 0)
        {
            return [];
        }

        var rows = await db.WithdrawalRequests.AsNoTracking()
            .Where(w => policyIds.Contains(w.PolicyId) || emails.Contains(w.DeclarantEmail))
            .OrderBy(w => w.DeclaredAt)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(w => (object)new
        {
            id = w.WithdrawalRequestId,
            requestNo = w.RequestNumber,
            policyId = w.PolicyId.Value,
            declarantName = w.DeclarantName,
            declarantEmail = w.DeclarantEmail,
            reason = w.Reason,
            channel = w.Channel,
            status = w.Status,
            declaredAt = w.DeclaredAt.ToString(),
            confirmedAt = w.ConfirmedAt?.ToString(),
            declineCode = w.DeclineCode,
        }).ToList();
    }

    private static IReadOnlyList<string> Identifiers(SubjectRef subject, string kind) =>
        subject.KnownIdentifiers.TryGetValue(kind, out var values) ? values : [];
}
