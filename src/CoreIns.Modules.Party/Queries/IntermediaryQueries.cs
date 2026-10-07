using System.Data;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using ApiStatus = CoreIns.Modules.Party.Contracts.Api.IntermediaryStatus;
using DomainStatus = CoreIns.Modules.Party.Domain.IntermediaryStatus;

namespace CoreIns.Modules.Party.Queries;

/// <summary>Reads of intermediaries and producer codes (REQ-PTY-008, REQ-PTY-207, REQ-PTY-208), Dapper on the scope's connection.</summary>
internal sealed class IntermediaryQueries(DbSession session, RequestContext context, PartyProtection protection)
{
    private const string Select =
        """
        SELECT i.intermediary_id AS IntermediaryId, i.party_id AS PartyId, p.party_number AS PartyNumber,
               coalesce(n.organisation_name, concat_ws(' ', n.given_names, n.family_name)) AS Name,
               i.intermediary_type AS IntermediaryType, i.status AS Status, i.register_number AS RegisterNumber, i.register_status AS RegisterStatus,
               c.code AS ProducerCode, c.status AS ProducerCodeStatus, c.collect_premium AS CollectPremium, c.issue_cover_notes AS IssueCoverNotes,
               c.bind_within_authority AS BindWithinAuthority, c.service_only AS ServiceOnly, c.valid_from::text AS ValidFrom,
               c.valid_to::text AS ValidTo, i.record_version AS RecordVersion
          FROM pty.intermediary i
          JOIN pty.party p ON p.party_id = i.party_id
          JOIN pty.producer_code c ON c.intermediary_id = i.intermediary_id
          LEFT JOIN LATERAL (SELECT * FROM pty.party_name x WHERE x.party_id = i.party_id AND x.form = 'NATIVE' AND x.recorded_to IS NULL LIMIT 1) n ON true
        """;

    public async Task<IntermediaryView?> ViewAsync(IntermediaryId id, CancellationToken cancellationToken)
    {
        var row = await QueryAsync($"{Select} WHERE i.legal_entity_id = @le AND i.intermediary_id = @id ORDER BY c.created_at LIMIT 1",
            new { le = Le(), id = id.Value }, cancellationToken).ConfigureAwait(false) is [var first, ..] ? first : null;
        return row is null ? null : ToView(row);
    }

    /// <summary>
    /// <c>pty.ProducerCode.validate</c> as a pure read at a date (REQ-PTY-208): code status and validity, intermediary
    /// status (REQ-PTY-190) and register status (REQ-PTY-189), each failed check as a reason code. Appointment, licence
    /// and CPD checks arrive with their entities (W2-PTY-04).
    /// </summary>
    public async Task<ProducerCodeValidateResponse> ValidateAsync(ProducerCodeValidateRequest request, BusinessDate validAt, CancellationToken cancellationToken)
    {
        var row = await QueryAsync($"{Select} WHERE i.legal_entity_id = @le AND c.code = @code", new { le = Le(), code = request.ProducerCode }, cancellationToken)
            .ConfigureAwait(false) is [var first, ..] ? first : null;
        if (row is null)
        {
            return new ProducerCodeValidateResponse { Valid = false, Reasons = [ProducerCodeReasons.UnknownCode] };
        }

        var reasons = new List<string>();
        var validFrom = BusinessDate.Parse(row.ValidFrom);
        if (row.ProducerCodeStatus != Codes.Of(ProducerCodeStatus.Active) || validAt < validFrom || (row.ValidTo is { } to && validAt >= BusinessDate.Parse(to)))
        {
            reasons.Add(ProducerCodeReasons.CodeNotActive);
        }

        if (row.Status != Codes.Of(DomainStatus.Active))
        {
            reasons.Add(ProducerCodeReasons.IntermediaryNotActive);
        }

        if (row.RegisterStatus != "ACTIVE")
        {
            reasons.Add(ProducerCodeReasons.RegisterInactive);
        }

        return new ProducerCodeValidateResponse
        {
            Valid = reasons.Count == 0,
            Reasons = reasons,
            IntermediaryType = row.IntermediaryType,
            RegisterNumber = row.RegisterNumber,
            RegisterStatus = row.RegisterStatus,
            Authorities = new ProducerCodeValidateResponse.AuthoritiesDetail
            {
                CollectPremium = row.CollectPremium,
                IssueCoverNotes = row.IssueCoverNotes,
                BindWithinAuthority = row.BindWithinAuthority,
            },
        };
    }

    /// <summary><c>pty.ProducerCode.search</c> by code prefix or intermediary name (REQ-PTY-207 subset).</summary>
    public async Task<IReadOnlyList<ProducerCodeSearchItem>> SearchAsync(string? code, string? name, int limit, int offset, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            $"""
             {Select}
              WHERE i.legal_entity_id = @le
                AND (@code::text IS NULL OR c.code LIKE @code || '%')
                AND (@name::text IS NULL OR EXISTS (SELECT 1 FROM pty.party_search_key k
                     WHERE k.party_id = i.party_id AND k.recorded_to IS NULL AND k.key_kind = 'PART' AND k.search_key LIKE @name || '%'))
              ORDER BY c.code
              LIMIT @limit OFFSET @offset
             """,
            new { le = Le(), code, name, limit, offset }, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new ProducerCodeSearchItem
        {
            ProducerCode = r.ProducerCode,
            Status = Codes.Parse<ProducerCodeSearchItem.StatusValue>(r.ProducerCodeStatus),
            IntermediaryId = new IntermediaryId(r.IntermediaryId),
            IntermediaryName = r.Name ?? string.Empty,
            IntermediaryType = r.IntermediaryType,
            IntermediaryStatus = Codes.Parse<ApiStatus>(r.Status),
        })];
    }

    /// <summary>Authority codes of <c>ProducerCodeChanged.authoritiesSummary</c>.</summary>
    public static IReadOnlyList<string> AuthoritiesSummary(ProducerCodeRow code) =>
        [.. new[]
        {
            code.CollectPremium ? "COLLECT_PREMIUM" : null,
            code.IssueCoverNotes ? "ISSUE_COVER_NOTES" : null,
            code.BindWithinAuthority ? "BIND_WITHIN_AUTHORITY" : null,
            code.ServiceOnly ? "SERVICE_ONLY" : null,
        }.OfType<string>()];

    private Guid Le() => protection.Current(context).Value;

    private async Task<IReadOnlyList<Record>> QueryAsync(string sql, object args, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return [.. await connection.QueryAsync<Record>(new CommandDefinition(sql, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];
    }

    private static IntermediaryView ToView(Record r) => new()
    {
        IntermediaryId = new IntermediaryId(r.IntermediaryId),
        PartyId = new PartyId(r.PartyId),
        PartyNumber = PartyNumber.Parse(r.PartyNumber),
        Name = r.Name ?? string.Empty,
        IntermediaryType = r.IntermediaryType,
        Status = Codes.Parse<ApiStatus>(r.Status),
        RegisterNumber = r.RegisterNumber,
        RegisterStatus = Codes.Parse<RegisterStatus>(r.RegisterStatus),
        ProducerCode = r.ProducerCode,
        ProducerCodeStatus = Codes.Parse<IntermediaryView.ProducerCodeStatusValue>(r.ProducerCodeStatus),
        Authorities = new ProducerAuthorities
        {
            CollectPremium = r.CollectPremium, IssueCoverNotes = r.IssueCoverNotes, BindWithinAuthority = r.BindWithinAuthority, ServiceOnly = r.ServiceOnly,
        },
        ValidFrom = BusinessDate.Parse(r.ValidFrom),
        RecordVersion = r.RecordVersion,
    };

    private sealed record Record(
        Guid IntermediaryId, Guid PartyId, string PartyNumber, string? Name, string IntermediaryType, string Status, string RegisterNumber,
        string RegisterStatus, string ProducerCode, string ProducerCodeStatus, bool CollectPremium, bool IssueCoverNotes, bool BindWithinAuthority,
        bool ServiceOnly, string ValidFrom, string? ValidTo, int RecordVersion);
}

/// <summary>
/// Reason codes of <c>pty.ProducerCode.validate</c>. <c>REGISTER_INACTIVE</c> is the PRD's (REQ-PTY-008); the other two
/// name checks the PRD requires (REQ-PTY-190, REQ-PTY-208) without giving a code, so they are defined here.
/// </summary>
internal static class ProducerCodeReasons
{
    public const string RegisterInactive = "REGISTER_INACTIVE";
    public const string IntermediaryNotActive = "INTERMEDIARY_NOT_ACTIVE";
    public const string CodeNotActive = "CODE_NOT_ACTIVE";
    public const string UnknownCode = "UNKNOWN_CODE";
}
