using CoreIns.Modules.Claims.Commands.FriendlySettlement;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Queries;

/// <summary>Reads a Friendly Settlement statement (clm.FriendlySettlement.getStatement): the month's lines, exceptions and nets.</summary>
internal sealed class FsStatementReader(ClaimsDbContext db)
{
    public Task<FsStatementView?> GetAsync(LegalEntityId entity, Guid id, CancellationToken cancellationToken) =>
        FsStatements.ViewAsync(db, entity, id, cancellationToken);
}
