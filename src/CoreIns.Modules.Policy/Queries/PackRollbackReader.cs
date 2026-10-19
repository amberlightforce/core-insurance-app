using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>pol.PackRollbackException.list / get (REQ-POL-354 subset): the queue of bound transactions found under a rolled-back pack version.</summary>
internal sealed class PackRollbackReader(PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public async Task<Result<PackRollbackExceptionListPage>> ListAsync(string? status, string? activationId, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        // TODO(hardening): keyset cursor; an offset cursor can skip or repeat rows while the queue changes.
        var skip = 0;
        if (limit is < 1 or > 200 || (cursor is not null && (!int.TryParse(cursor, out skip) || skip < 0)))
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        if (status is not null && status is not ("OPEN" or "REVIEWED"))
        {
            return DomainError.Of(ModuleCode.POL, "VALIDATION", "status must be OPEN or REVIEWED.");
        }

        Guid? activation = null;
        if (activationId is not null)
        {
            if (!Guid.TryParse(activationId, out var parsed))
            {
                return DomainError.Of(ModuleCode.POL, "VALIDATION", "activationId must be a uuid.");
            }

            activation = parsed;
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var query = db.PackRollbackExceptions.AsNoTracking().Where(e => e.LegalEntityId == legalEntity);
        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        if (activation is { } a)
        {
            query = query.Where(e => e.ActivationId == a);
        }

        var take = limit ?? 25;
        var rows = await query.OrderByDescending(e => e.IdentifiedAt).ThenBy(e => e.ExceptionId).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(take).ToList();
        return new PackRollbackExceptionListPage
        {
            Items = await ToViewsAsync(page, cancellationToken).ConfigureAwait(false),
            NextCursor = rows.Count > take ? (skip + take).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Limit = take,
        };
    }

    public async Task<Result<PackRollbackExceptionView>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        if (!Guid.TryParse(id, out var exceptionId))
        {
            return JobSupport.NotFound("pack rollback exception");
        }

        var row = await db.PackRollbackExceptions.AsNoTracking().SingleOrDefaultAsync(e => e.ExceptionId == exceptionId && e.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? JobSupport.NotFound("pack rollback exception") : await ToViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PackRollbackExceptionView> ToViewAsync(PackRollbackExceptionRow row, CancellationToken cancellationToken) =>
        (await ToViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];

    private async Task<IReadOnlyList<PackRollbackExceptionView>> ToViewsAsync(IReadOnlyList<PackRollbackExceptionRow> rows, CancellationToken cancellationToken)
    {
        var policyIds = rows.Select(r => r.PolicyId).Distinct().ToList();
        var numbers = await db.Policies.AsNoTracking().Where(p => policyIds.Contains(p.PolicyId)).Select(p => new { p.PolicyId, p.PolicyNumber })
            .ToDictionaryAsync(p => p.PolicyId, p => p.PolicyNumber, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new PackRollbackExceptionView
        {
            ExceptionId = r.ExceptionId,
            ActivationId = r.ActivationId,
            Pack = r.Pack,
            FromVersion = r.FromVersion,
            ToVersion = r.ToVersion,
            PolicyId = r.PolicyId,
            PolicyNumber = numbers[r.PolicyId],
            TermId = r.TermId,
            TransactionId = r.TransactionId,
            TransactionKind = r.TransactionKind,
            ConfigurationHash = ConfigurationHash.Parse(r.ConfigurationHash),
            ProductVersion = ProductVersionNumber.Parse(r.ProductVersion),
            IdentifiedAt = r.IdentifiedAt,
            Status = r.Status == "OPEN" ? PackRollbackExceptionStatus.Open : PackRollbackExceptionStatus.Reviewed,
            WrkActivity = PackRollbackExceptionView.WrkActivityValue.NotCreatedWrkNotBuilt,
            Review = r.ReviewOutcome is null
                ? null
                : new PackRollbackExceptionReview
                {
                    Outcome = r.ReviewOutcome == "NO_ACTION" ? PackRollbackExceptionReview.OutcomeValue.NoAction : PackRollbackExceptionReview.OutcomeValue.CorrectionRequired,
                    Reason = r.ReviewReason ?? string.Empty,
                    ReviewedBy = r.ReviewedBy ?? string.Empty,
                    ReviewedAt = r.ReviewedAt!.Value,
                },
        })];
    }
}
