using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>An open balance of a clearing account: debits minus credits, so a non-zero value is an open item or a break.</summary>
internal sealed record ClearingBalance(Guid? ClaimPaymentId, Guid? RecoveryId, Guid? CounterpartyPartyId, string? StatementRef, decimal Balance);

/// <summary>
/// The clearing invariants of SL4-FIN-V4, each returning only the groups whose balance is not zero (an empty result means the
/// invariant holds). Reversals count, because they are journals with opposite sides. Lines without the grouping dimension
/// show up under a null key, so an unattributable posting is a visible break, never a silently ignored line.
/// <list type="bullet">
/// <item><see cref="ClaimPaymentClearingAsync"/>: GL-2510 per claim payment (REQ-FIN-037). Friendly Settlement payables never touch it.</item>
/// <item><see cref="RecoveryClearingAsync"/>: the claim recovery clearing GL-1340 per recovery id, once BIL posted the receipt (REQ-FIN-160).</item>
/// <item><see cref="FsClearingAsync"/>: GL-2515 per (counterparty, statement), once BIL posted the net (REQ-FIN-299, BR-FIN-097).</item>
/// </list>
/// </summary>
internal sealed class ClearingInvariants(DbSession session)
{
    private const string Signed = "sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END)";

    public async Task<IReadOnlyList<ClearingBalance>> ClaimPaymentClearingAsync(LegalEntityId legalEntity, string book, CancellationToken cancellationToken) =>
        await QueryAsync($"""
            SELECT claim_payment_id AS ClaimPaymentId, NULL::uuid AS RecoveryId, NULL::uuid AS CounterpartyPartyId, NULL::text AS StatementRef, {Signed} AS Balance
              FROM fin.journal_line WHERE legal_entity_id = @le AND book = @book AND account_code = 'GL-2510'
             GROUP BY claim_payment_id HAVING {Signed} <> 0
            """, legalEntity, book, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ClearingBalance>> RecoveryClearingAsync(LegalEntityId legalEntity, string book, CancellationToken cancellationToken) =>
        await QueryAsync($"""
            SELECT NULL::uuid AS ClaimPaymentId, recovery_id AS RecoveryId, NULL::uuid AS CounterpartyPartyId, NULL::text AS StatementRef, {Signed} AS Balance
              FROM fin.journal_line WHERE legal_entity_id = @le AND book = @book AND account_code = 'GL-1340'
             GROUP BY recovery_id HAVING {Signed} <> 0
            """, legalEntity, book, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ClearingBalance>> FsClearingAsync(LegalEntityId legalEntity, string book, CancellationToken cancellationToken) =>
        await QueryAsync($"""
            SELECT NULL::uuid AS ClaimPaymentId, NULL::uuid AS RecoveryId, counterparty_party_id AS CounterpartyPartyId, fs_statement_ref AS StatementRef, {Signed} AS Balance
              FROM fin.journal_line WHERE legal_entity_id = @le AND book = @book AND account_code = 'GL-2515'
             GROUP BY counterparty_party_id, fs_statement_ref HAVING {Signed} <> 0
            """, legalEntity, book, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<ClearingBalance>> QueryAsync(string sql, LegalEntityId legalEntity, string book, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ClearingBalance>(new CommandDefinition(
            sql, new { le = legalEntity.Value, book }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows];
    }
}
