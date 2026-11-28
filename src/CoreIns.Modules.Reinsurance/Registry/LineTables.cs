using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>Reads the line table versions of the caller's legal entity (BO-RI-05, REQ-RI-036).</summary>
internal sealed class LineTableReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One version by id; <c>current</c> is the latest version.</summary>
    public async Task<Result<LineTableGetResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        LineTableRow? row;
        if (string.Equals(id, "current", StringComparison.OrdinalIgnoreCase))
        {
            row = (await db.LineTables.AsNoTracking().Where(t => t.LegalEntityId == entity).ToListAsync(cancellationToken).ConfigureAwait(false)).OrderByDescending(t => t.VersionNo).FirstOrDefault();
        }
        else if (Guid.TryParse(id, out var tableId))
        {
            row = await db.LineTables.AsNoTracking().SingleOrDefaultAsync(t => t.TableId == tableId && t.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return RiErrors.Validation("id", "ID_INVALID", "The id is a line table id or 'current'.");
        }

        return row is null ? DomainError.Of(ModuleCode.RI, "NOT-FOUND", "The line table does not exist.") : new LineTableGetResponse { TableVersion = ToView(row) };
    }

    public static RiLineTableVersion ToView(LineTableRow row) =>
        new()
        {
            TableId = row.TableId, VersionNo = row.VersionNo, CreatedAt = row.CreatedAt, CreatedBy = row.CreatedBy,
            Rows = JsonSerializer.Deserialize<RiLineTableRow[]>(row.Rows, Json) ?? [],
        };

    public static string Serialize(IEnumerable<RiLineTableRow> rows) => JsonSerializer.Serialize(rows.ToArray(), Json);
}

/// <summary><c>ri.LineTable.set</c> as a command of the platform pipeline.</summary>
internal sealed record SetLineTable(LineTableSetRequest Request) : ICommand<LineTableSetResponse>;

/// <summary>Shape rules of one band: amounts non-negative with at most four decimals, a positive retention and at least one line.</summary>
internal sealed class SetLineTableValidator : AbstractValidator<SetLineTable>
{
    public SetLineTableValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Rows).NotEmpty().When(c => c.Request is not null);
        RuleForEach(c => c.Request.Rows).ChildRules(r =>
        {
            r.RuleFor(x => x.SumInsuredFrom).GreaterThanOrEqualTo(0m).Must(v => Scale(v)).WithErrorCode("AMOUNT_SCALE");
            r.RuleFor(x => x.SumInsuredTo).Must(Scale).WithErrorCode("AMOUNT_SCALE").When(x => x.SumInsuredTo is not null);
            r.RuleFor(x => x.Retention).GreaterThan(0m).Must(v => Scale(v)).WithErrorCode("AMOUNT_SCALE");
            r.RuleFor(x => x.MaxLines).GreaterThanOrEqualTo(1);
            r.RuleFor(x => x).Must(x => x.SumInsuredTo is not { } to || to > x.SumInsuredFrom).WithErrorCode("BAND_EMPTY");
        }).When(c => c.Request?.Rows is not null);
    }

    private static bool Scale(decimal? value) => value is null || decimal.Round(value.Value, ContractContent.MoneyScale, MidpointRounding.ToZero) == value.Value;
}

/// <summary>
/// Sets the line table of the legal entity as a new version (append-only; the previous version stays as history). Bands are
/// half-open <c>[from, to)</c> and must not overlap: that is <c>RI-ERR-OVERLAP</c>. Only the last band may be open-ended.
/// </summary>
internal sealed class SetLineTableHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<SetLineTable, LineTableSetResponse>
{
    public async Task<Result<LineTableSetResponse>> HandleAsync(SetLineTable command, CancellationToken cancellationToken)
    {
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var rows = command.Request.Rows.OrderBy(r => r.SumInsuredFrom).ToList();
        for (var index = 1; index < rows.Count; index++)
        {
            var previous = rows[index - 1];
            if (previous.SumInsuredTo is not { } end || end > rows[index].SumInsuredFrom)
            {
                return new DomainError(ErrorCode.For(ModuleCode.RI, "OVERLAP"), "The bands of a line table must not overlap.")
                {
                    FieldErrors = [new FieldError(FormattableString.Invariant($"rows[{index}].sumInsuredFrom"), "BANDS_OVERLAP", "ri.bands_overlap")],
                };
            }
        }

        var versionNo = await db.LineTables.Where(t => t.LegalEntityId == entity).Select(t => (int?)t.VersionNo).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var row = new LineTableRow
        {
            TableId = Guid.CreateVersion7(), LegalEntityId = entity, VersionNo = versionNo + 1, Rows = LineTableReader.Serialize(rows),
            CreatedAt = clock.Now, CreatedBy = context.Actor.ToString(),
        };
        db.LineTables.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return RiErrors.Stale("The line table changed meanwhile.");
        }

        return new LineTableSetResponse { TableVersion = LineTableReader.ToView(row) };
    }
}

/// <summary>Audit facts of <c>ri.LineTable.set</c>.</summary>
internal sealed class SetLineTableAuditor : ICommandAuditor<SetLineTable, LineTableSetResponse>
{
    public CommandAuditFacts Describe(SetLineTable command, Result<LineTableSetResponse>? result)
    {
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.RI, "LineTable", Guid.Empty.ToString("D")) };
        }

        var table = ok.Value.TableVersion;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.RI, "LineTable", table.TableId.ToString("D")),
            BusinessKeys = BusinessKeys.Empty.With("versionNo", table.VersionNo.ToString(CultureInfo.InvariantCulture)),
            Changes = AuditDiff.Compute(new { versionNo = table.VersionNo - 1 }, new { versionNo = table.VersionNo, rows = table.Rows.Count }),
        };
    }
}
