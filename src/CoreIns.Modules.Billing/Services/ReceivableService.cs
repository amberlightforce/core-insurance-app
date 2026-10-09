using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Services;

internal sealed class ReceivableService(RequestContext context, ILegalEntityDirectory entities, ICommandHandler<RegisterReceivable, ReceivableRegisterResponse> register, ReceivableReader reader) : IBillingReceivableService
{
    public Task<ReceivableRegisterResponse> RegisterAsync(ReceivableRegisterRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, register, new RegisterReceivable(request, context.ExecutingCommandModule), options, cancellationToken);

    public async Task<ReceivableGetResponse> GetAsync(string id, CancellationToken cancellationToken = default) => new()
    {
        Receivable = InProcess.Found(Guid.TryParse(id, out var guid) ? await reader.GetAsync(InProcess.LegalEntity(context, entities), guid, cancellationToken).ConfigureAwait(false) : null, "receivable"),
    };

    public Task<ReceivableListPage> ListAsync(BillingAccountId? billingAccountId = null, PartyId? counterpartyPartyId = null, ClaimId? claimId = null, Guid? recoveryId = null, string? statementRef = null, string? paymentReference = null, ReceivableSourceType? sourceType = null, ReceivableStatus? status = null, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        reader.ListAsync(InProcess.LegalEntity(context, entities), billingAccountId, counterpartyPartyId, claimId, recoveryId, statementRef, paymentReference, sourceType, status, cursor, limit, cancellationToken);
}
