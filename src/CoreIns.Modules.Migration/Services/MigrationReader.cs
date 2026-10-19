using System.Globalization;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services;

/// <summary>Read side of the Migration module: batches, outcomes, xrefs and the authoritative coexistence routing.</summary>
internal sealed class MigrationReader(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public static XrefView ToView(XrefRow x) => new(x.XrefId, x.Source, x.ObjectType, x.LegacyKey, x.CoreId, x.BatchId, x.Origin, x.Status);

    public static RoutingView ToView(RoutingRow r) =>
        new(r.MasterState, r.RouteId, r.ValidFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.ValidTo?.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Reason);

    public async Task<Result<BatchResponse>> GetBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(b => b.BatchId == batchId && b.LegalEntityId == LegalEntity, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return MigrationErrors.NotFound("The batch");
        }

        return new BatchResponse(await Batches.ViewAsync(db, batch, cancellationToken).ConfigureAwait(false), []);
    }

    public async Task<Result<OutcomesPage>> OutcomesAsync(Guid batchId, string? outcome, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!await db.Batches.AsNoTracking().AnyAsync(b => b.BatchId == batchId && b.LegalEntityId == LegalEntity, cancellationToken).ConfigureAwait(false))
        {
            return MigrationErrors.NotFound("The batch");
        }

        var size = Math.Clamp(limit ?? 50, 1, 200);
        var after = BatchCursor.Decode(cursor);
        var query = db.Items.AsNoTracking().Where(i => i.BatchId == batchId && i.Seq > after);
        if (!string.IsNullOrEmpty(outcome))
        {
            query = query.Where(i => i.Outcome == outcome);
        }

        var rows = await query.OrderBy(i => i.Seq).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new OutcomesPage([.. page.Select(Batches.Outcome)], rows.Count > size ? page[^1].Seq.ToString(CultureInfo.InvariantCulture) : null, size);
    }

    public async Task<Result<XrefView>> ResolveXrefAsync(XrefResolveBody body, CancellationToken cancellationToken)
    {
        var query = db.Xrefs.AsNoTracking().Where(x => x.LegalEntityId == LegalEntity && x.ObjectType == body.ObjectType && x.Status == "ACTIVE");
        if (body.CoreId is { } coreId)
        {
            query = query.Where(x => x.CoreId == coreId);
        }
        else if (!string.IsNullOrEmpty(body.LegacyKey))
        {
            query = query.Where(x => x.LegacyKey == body.LegacyKey);
        }
        else
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "Give a legacy key or a core id.");
        }

        if (!string.IsNullOrEmpty(body.Source))
        {
            var source = body.Source.ToUpperInvariant();
            query = query.Where(x => x.Source == source);
        }

        var found = await query.Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        return found.Count switch
        {
            0 => MigrationErrors.NotFound("The cross-reference"),
            1 => ToView(found[0]),
            _ => DomainError.Of(ModuleCode.MIG, "AMBIGUOUS-KEY", "The key matches more than one object; give the source."),
        };
    }

    public async Task<XrefPage> ListXrefsAsync(string? objectType, Guid? batchId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var offset = BatchCursor.Decode(cursor);
        var query = db.Xrefs.AsNoTracking().Where(x => x.LegalEntityId == LegalEntity);
        if (!string.IsNullOrEmpty(objectType))
        {
            query = query.Where(x => x.ObjectType == objectType);
        }

        if (batchId is { } batch)
        {
            query = query.Where(x => x.BatchId == batch);
        }

        var rows = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.XrefId).Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new XrefPage([.. rows.Take(size).Select(ToView)], rows.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null, size);
    }

    /// <summary>Authoritative master of one object on one date: NONE when no period covers it.</summary>
    public async Task<Result<RoutingView>> ResolveRouteAsync(string objectType, string key, BusinessDate date, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var route = await db.Routes.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.ObjectType == objectType && r.Key == key && r.ValidFrom <= date && (r.ValidTo == null || r.ValidTo > date))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return route is null ? new RoutingView(RouteState.None, null, null, null, null) : ToView(route);
    }
}
