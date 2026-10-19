using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Channels.Queries;

/// <summary><c>chn.Withdrawal.get</c> and <c>list</c> (REQ-CHN-168), filtered by legal entity.</summary>
internal sealed class WithdrawalReader(ChannelsDbContext db)
{
    public async Task<WithdrawalGetResponse?> GetAsync(LegalEntityId legalEntity, string idOrNumber, CancellationToken cancellationToken)
    {
        var query = db.WithdrawalRequests.AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        var row = Guid.TryParse(idOrNumber, out var id)
            ? await query.SingleOrDefaultAsync(r => r.WithdrawalRequestId == id, cancellationToken).ConfigureAwait(false)
            : await query.SingleOrDefaultAsync(r => r.RequestNumber == idOrNumber, cancellationToken).ConfigureAwait(false);
        return row is null
            ? null
            : new WithdrawalGetResponse
            {
                WithdrawalRequestId = row.WithdrawalRequestId,
                RequestNumber = row.RequestNumber,
                PolicyId = row.PolicyId,
                Status = WithdrawalStatus(row),
                RefundStatus = RefundStatus(row),
                DeclaredAt = row.DeclaredAt,
                ConfirmedAt = row.ConfirmedAt,
                DeadlineAt = row.DeadlineAt,
                DeclineCode = row.DeclineCode,
                Channel = row.Channel,
            };
    }

    /// <summary>Offset cursor: the number of rows already returned, newest first.</summary>
    public async Task<WithdrawalListPage> ListAsync(LegalEntityId legalEntity, string? requestNumber, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var skip = int.TryParse(cursor, out var parsed) && parsed > 0 ? parsed : 0;
        var query = db.WithdrawalRequests.AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (!string.IsNullOrWhiteSpace(requestNumber))
        {
            query = query.Where(r => r.RequestNumber == requestNumber);
        }

        var rows = await query.OrderByDescending(r => r.DeclaredAt).ThenBy(r => r.WithdrawalRequestId).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new WithdrawalListPage
        {
            Items =
            [
                .. rows.Take(take).Select(row => new WithdrawalListItem
                {
                    WithdrawalRequestId = row.WithdrawalRequestId,
                    RequestNumber = row.RequestNumber,
                    PolicyId = row.PolicyId,
                    Status = WithdrawalStatus(row),
                    RefundStatus = RefundStatus(row),
                    DeclaredAt = row.DeclaredAt,
                    ConfirmedAt = row.ConfirmedAt,
                }),
            ],
            NextCursor = rows.Count > take ? (skip + take).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Limit = take,
        };
    }

    private static WithdrawalStatusCode WithdrawalStatus(WithdrawalRequestRow row) => Commands.WithdrawalSupport.ToStatus(row.Status);

    private static string RefundStatus(WithdrawalRequestRow row) => row.Status switch
    {
        WithdrawalStatuses.Processed => "PROCESSED",
        WithdrawalStatuses.Rejected => "REJECTED",
        _ => "NONE",
    };
}
