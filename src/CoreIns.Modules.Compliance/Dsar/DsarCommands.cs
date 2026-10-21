using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Dsar;

internal sealed record CreateDsar(DsarCreateRequest Request) : ICommand<DsarCreateResponse>;

internal sealed record VerifyDsarIdentity(DsarVerifyIdentityRequest Request) : ICommand<DsarVerifyIdentityResponse>;

internal sealed record StartDsarFanOut(DsarStartFanOutRequest Request) : ICommand<DsarStartFanOutResponse>;

internal sealed record RetryDsarTask(DsarRetryTaskRequest Request) : ICommand<DsarRetryTaskResponse>;

internal sealed record DecideDsarItem(DsarDecideItemRequest Request) : ICommand<DsarDecideItemResponse>;

internal sealed record ApproveDsar(DsarApproveRequest Request) : ICommand<DsarApproveResponse>;

internal sealed record ExtendDsar(DsarExtendRequest Request) : ICommand<DsarExtendResponse>;

internal sealed record RefuseDsar(DsarRefuseRequest Request) : ICommand<DsarRefuseResponse>;

internal sealed record ExportDsar(DsarExportRequest Request) : ICommand<DsarExportResponse>;

internal sealed record RestrictDsar(DsarRestrictRequest Request) : ICommand<DsarRestrictResponse>;

internal sealed record EraseDsar(DsarEraseRequest Request) : ICommand<DsarEraseResponse>;

internal sealed record RecordDisclosure(DisclosureLogRecordRequest Request) : ICommand<DisclosureLogRecordResponse>;

internal sealed class CreateDsarValidator : AbstractValidator<CreateDsar>
{
    public CreateDsarValidator()
    {
        RuleFor(c => c.Request.SubjectPartyId.Value).NotEmpty();
        RuleFor(c => c.Request.Notes).MaximumLength(512);
    }
}

internal sealed class CreateDsarHandler(DsarEngine engine) : ICommandHandler<CreateDsar, DsarCreateResponse>
{
    public async Task<Result<DsarCreateResponse>> HandleAsync(CreateDsar command, CancellationToken cancellationToken) =>
        (await engine.CreateAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarCreateResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class VerifyDsarIdentityHandler(DsarEngine engine) : ICommandHandler<VerifyDsarIdentity, DsarVerifyIdentityResponse>
{
    public async Task<Result<DsarVerifyIdentityResponse>> HandleAsync(VerifyDsarIdentity command, CancellationToken cancellationToken) =>
        (await engine.VerifyIdentityAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarVerifyIdentityResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class StartDsarFanOutHandler(DsarEngine engine) : ICommandHandler<StartDsarFanOut, DsarStartFanOutResponse>
{
    public async Task<Result<DsarStartFanOutResponse>> HandleAsync(StartDsarFanOut command, CancellationToken cancellationToken) =>
        (await engine.StartFanOutAsync(command.Request.Id, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarStartFanOutResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class RetryDsarTaskHandler(DsarEngine engine) : ICommandHandler<RetryDsarTask, DsarRetryTaskResponse>
{
    public async Task<Result<DsarRetryTaskResponse>> HandleAsync(RetryDsarTask command, CancellationToken cancellationToken) =>
        (await engine.RetryTaskAsync(command.Request.Id, command.Request.TaskId, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarRetryTaskResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class DecideDsarItemHandler(DsarEngine engine) : ICommandHandler<DecideDsarItem, DsarDecideItemResponse>
{
    public async Task<Result<DsarDecideItemResponse>> HandleAsync(DecideDsarItem command, CancellationToken cancellationToken) =>
        (await engine.DecideItemAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarDecideItemResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class ApproveDsarHandler(DsarEngine engine) : ICommandHandler<ApproveDsar, DsarApproveResponse>
{
    public async Task<Result<DsarApproveResponse>> HandleAsync(ApproveDsar command, CancellationToken cancellationToken) =>
        (await engine.ApproveAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarApproveResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class ExtendDsarHandler(DsarEngine engine) : ICommandHandler<ExtendDsar, DsarExtendResponse>
{
    public async Task<Result<DsarExtendResponse>> HandleAsync(ExtendDsar command, CancellationToken cancellationToken) =>
        (await engine.ExtendAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarExtendResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class RefuseDsarHandler(DsarEngine engine) : ICommandHandler<RefuseDsar, DsarRefuseResponse>
{
    public async Task<Result<DsarRefuseResponse>> HandleAsync(RefuseDsar command, CancellationToken cancellationToken) =>
        (await engine.RefuseAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (request, tasks, package) = DsarViews.Of(c);
            return new DsarRefuseResponse { Request = request, Tasks = tasks, Package = package };
        });
}

internal sealed class ExportDsarHandler(DsarEngine engine) : ICommandHandler<ExportDsar, DsarExportResponse>
{
    public async Task<Result<DsarExportResponse>> HandleAsync(ExportDsar command, CancellationToken cancellationToken) =>
        (await engine.ExportAsync(command.Request.DsarId.Value, cancellationToken).ConfigureAwait(false)).Map(a =>
        {
            var (request, _, package) = DsarViews.Of(a.Case);
            return new DsarExportResponse { Request = request, Package = package, PerItemOutcome = a.Items };
        });
}

internal sealed class RestrictDsarHandler(DsarEngine engine) : ICommandHandler<RestrictDsar, DsarRestrictResponse>
{
    public async Task<Result<DsarRestrictResponse>> HandleAsync(RestrictDsar command, CancellationToken cancellationToken) =>
        (await engine.ActAsync(command.Request.DsarId.Value, erase: false, command.Request.LegalBasis, cancellationToken).ConfigureAwait(false)).Map(a =>
        {
            var (request, _, package) = DsarViews.Of(a.Case);
            return new DsarRestrictResponse { Request = request, Package = package, PerItemOutcome = a.Items };
        });
}

internal sealed class EraseDsarHandler(DsarEngine engine) : ICommandHandler<EraseDsar, DsarEraseResponse>
{
    public async Task<Result<DsarEraseResponse>> HandleAsync(EraseDsar command, CancellationToken cancellationToken) =>
        (await engine.ActAsync(command.Request.DsarId.Value, erase: true, null, cancellationToken).ConfigureAwait(false)).Map(a =>
        {
            var (request, _, package) = DsarViews.Of(a.Case);
            return new DsarEraseResponse { Request = request, Package = package, PerItemOutcome = a.Items };
        });
}

/// <summary>
/// cmp.DisclosureLog.record. <c>entry</c> = { recipient, purpose, legalBasis, categories[], dsarId? }, <c>subject</c> = { partyId }.
/// </summary>
internal sealed class RecordDisclosureHandler(ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, Clocks.ClockEngine clocks)
    : ICommandHandler<RecordDisclosure, DisclosureLogRecordResponse>
{
    public async Task<Result<DisclosureLogRecordResponse>> HandleAsync(RecordDisclosure command, CancellationToken cancellationToken)
    {
        var entry = command.Request.Entry;
        var subject = command.Request.Subject;
        string Text(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        var recipient = Text(entry, "recipient");
        var purpose = Text(entry, "purpose");
        var basis = Text(entry, "legalBasis");
        if (recipient.Length is 0 or > 200 || purpose.Length is 0 or > 200 || basis.Length is 0 or > 256)
        {
            return DomainError.Of(ModuleCode.CMP, "VALIDATION", "A disclosure needs a recipient, a purpose and a legal basis.");
        }

        PartyId? party = Guid.TryParse(Text(subject, "partyId"), out var p) ? new PartyId(p) : null;
        DsarId? dsar = Guid.TryParse(Text(entry, "dsarId"), out var d) ? new DsarId(d) : null;
        var categories = entry is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
            ? cats.GetRawText()
            : "[]";
        var row = new DisclosureLogRow
        {
            EntryId = Guid.CreateVersion7(),
            LegalEntityId = Clocks.ClockLookup.LegalEntity(context, legalEntities),
            SubjectPartyId = party,
            DsarId = dsar,
            Recipient = recipient,
            Purpose = purpose,
            LegalBasis = basis,
            Categories = categories,
            RecordedBy = context.Actor.Id,
            RecordedAt = clocks.Now,
        };
        db.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DisclosureLogRecordResponse { Entries = [DsarReader.Entry(row)] };
    }
}

/// <summary>Reads cases and the disclosure log for the REST facade.</summary>
internal sealed class DsarReader(ComplianceDbContext db, DsarEngine engine, RequestContext context, ILegalEntityDirectory legalEntities)
{
    /// <summary>The opaque cursor's id; a malformed cursor is refused (fail closed), never read as the first page.</summary>
    internal static Guid? Cursor(string? cursor)
    {
        if (!KeysetPaging.TryDecode(cursor, 1, out var parts) || (parts is not null && !Guid.TryParse(parts[0], out _)))
        {
            throw new DomainException(DomainError.Of(ModuleCode.CMP, "VALIDATION", "The cursor is malformed."));
        }

        return parts is null ? null : Guid.Parse(parts[0]);
    }

    public Task<DsarCase?> GetAsync(Guid id, CancellationToken ct) => engine.FindAsync(id, ct);

    public async Task<(List<DsarCase> Items, string? Next, int Limit)> QueryAsync(string? cursor, int? limit, CancellationToken ct)
    {
        var take = KeysetPaging.Clamp(limit);
        var after = Cursor(cursor);
        var legalEntity = Clocks.ClockLookup.LegalEntity(context, legalEntities);
        var query = db.Set<DsarRow>().AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        var allIds = (await query.Select(r => r.DsarId).ToListAsync(ct).ConfigureAwait(false)).Select(i => i.Value);
        var pageIds = KeysetPaging.IdsAfter(allIds, after, take + 1).Select(g => new DsarId(g)).ToList();
        var rows = (await query.Where(r => pageIds.Contains(r.DsarId)).ToListAsync(ct).ConfigureAwait(false)).OrderBy(r => r.DsarId.Value).ToList();
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.DsarId.Value)));
        var ids = page.Select(r => r.DsarId).ToList();
        var tasks = await db.Set<DsarTaskRow>().AsNoTracking().Where(t => ids.Contains(t.DsarId)).ToListAsync(ct).ConfigureAwait(false);
        var items = page.Select(r => new DsarCase(r, [.. tasks.Where(t => t.DsarId == r.DsarId)])).ToList();
        return (items, next, take);
    }

    public async Task<(List<DisclosureLogRow> Items, string? Next, int Limit)> QueryDisclosuresAsync(string? cursor, int? limit, Guid? partyId, CancellationToken ct)
    {
        var take = KeysetPaging.Clamp(limit);
        var after = Cursor(cursor);
        var legalEntity = Clocks.ClockLookup.LegalEntity(context, legalEntities);
        var query = db.Set<DisclosureLogRow>().AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (partyId is { } party)
        {
            var id = new PartyId(party);
            query = query.Where(r => r.SubjectPartyId == id);
        }

        if (after is { } a)
        {
            query = query.Where(r => r.EntryId.CompareTo(a) > 0);
        }

        var rows = await query.OrderBy(r => r.EntryId).Take(take + 1).ToListAsync(ct).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(r.EntryId)));
        return (page, next, take);
    }

    public static JsonElement Entry(DisclosureLogRow r) => JsonSerializer.SerializeToElement(new
    {
        entryId = r.EntryId,
        subjectPartyId = r.SubjectPartyId?.Value,
        dsarId = r.DsarId?.Value,
        recipient = r.Recipient,
        purpose = r.Purpose,
        legalBasis = r.LegalBasis,
        categories = JsonDocument.Parse(r.Categories).RootElement,
        recordedBy = r.RecordedBy,
        recordedAt = r.RecordedAt.ToString(),
    });
}
