using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Localisation;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Work.Notes;

/// <summary>Create body: <c>objectRef</c> is <c>MOD/Type/id</c>.</summary>
internal sealed record NoteCreateBody(string? ObjectRef, string? Body, string? Visibility);

/// <summary>Edit body: a new text and/or visibility; <c>recordVersion</c> (optional) guards against editing a stale copy.</summary>
internal sealed record NoteEditBody(Guid? Id, string? Body, string? Visibility, int? RecordVersion);

/// <summary>Delete body: soft delete; a reason is required when deleting someone else's note.</summary>
internal sealed record NoteDeleteBody(Guid? Id, string? Reason);

/// <summary>Export body: the notes of one object, optionally filtered by a search text.</summary>
internal sealed record NoteExportBody(string? ObjectRef, string? Query);

/// <summary>A note as the staff UI sees it.</summary>
internal sealed record NoteView(
    Guid Id, string ObjectRef, string Visibility, string Body, int Version, string CreatedBy, Instant CreatedAt, Instant UpdatedAt,
    bool Edited, bool Deleted, Instant? DeletedAt, string? DeletedBy, string? DeleteReason, int RecordVersion, bool CanEdit, bool CanDelete)
{
    public JsonElement ToElement() => JsonSerializer.SerializeToElement(this, SharedKernelJson.Options);
}

/// <summary>One stored version of a note's text.</summary>
internal sealed record NoteVersionView(int Version, string Body, string Visibility, string EditedBy, Instant EditedAt)
{
    public JsonElement ToElement() => JsonSerializer.SerializeToElement(this, SharedKernelJson.Options);
}

/// <summary>
/// The notes rules (BO-WRK-07): text linked to a business object, an append-only history, three visibility levels, soft delete.
/// Mutations run in the command pipeline's transaction.
/// TODO(hardening): the edit window and size limits are constants, not configuration.
/// </summary>
internal sealed class NoteService(WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock,
    ILanguageRuleSet languageRules, WorkCatalogue catalogue)
{
    public const int MaxBodyLength = 8000;
    public const int MaxExportRows = 5000;
    public const int DefaultLimit = 25;
    public const int MaxLimit = 200;

    /// <summary>The author may edit or delete their own note for this long after creating it; managers may delete later.</summary>
    public static readonly TimeSpan EditWindow = TimeSpan.FromHours(24);

    private static readonly string[] Visibilities = [NoteRow.Staff, NoteRow.Managers, NoteRow.Private];

    private LegalEntityId LegalEntity =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public static Result<ObjectRef> ParseObject(string? text)
    {
        var parts = text?.Split('/', 3);
        if (parts is not { Length: 3 } || !Enum.TryParse<ModuleCode>(parts[0], ignoreCase: false, out var module) || !Enum.IsDefined(module)
            || parts[1].Length is 0 or > 128 || parts[2].Length is 0 or > 200 || parts.Any(p => p.Any(char.IsControl)))
        {
            return WorkErrors.Invalid("objectRef is MOD/Type/id.");
        }

        return new ObjectRef(module, parts[1], parts[2]);
    }

    public async Task<Result<NoteRow>> CreateAsync(NoteCreateBody request, CancellationToken cancellationToken)
    {
        var obj = ParseObject(request.ObjectRef);
        if (obj.IsFailure)
        {
            return obj.Error!;
        }

        var body = CleanBody(request.Body);
        var visibility = (request.Visibility ?? NoteRow.Staff).ToUpperInvariant();
        if (body is null || !Visibilities.Contains(visibility))
        {
            return WorkErrors.Invalid($"body is 1..{MaxBodyLength} characters and visibility is STAFF, MANAGERS or PRIVATE.");
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var row = new NoteRow
        {
            NoteId = Guid.NewGuid(),
            LegalEntityId = LegalEntity,
            ObjectModule = obj.Value.Module.ToString(),
            ObjectType = obj.Value.Type,
            ObjectId = obj.Value.Id,
            Visibility = visibility,
            Body = body,
            SearchKey = languageRules.SearchKey(body),
            CurrentVersion = 1,
            CreatedBy = actor.Actor.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(row);
        db.NoteVersions.Add(new NoteVersionRow { NoteId = row.NoteId, Version = 1, Body = body, Visibility = visibility, EditedBy = row.CreatedBy, EditedAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<Result<NoteRow>> EditAsync(NoteEditBody request, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindAsync(request.Id, actor, includeDeleted: false, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.CreatedBy != actor.Actor.Id)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only the author may edit a note.");
        }

        if (clock.Now.Since(row.CreatedAt) > EditWindow)
        {
            return WorkErrors.Of(WorkErrors.EditWindow, "A note can be edited for 24 hours after it was written.");
        }

        if (request.RecordVersion is { } expected && expected != row.RecordVersion)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The note changed meanwhile.");
        }

        var body = request.Body is null ? row.Body : CleanBody(request.Body);
        var visibility = (request.Visibility ?? row.Visibility).ToUpperInvariant();
        if (body is null || !Visibilities.Contains(visibility))
        {
            return WorkErrors.Invalid($"body is 1..{MaxBodyLength} characters and visibility is STAFF, MANAGERS or PRIVATE.");
        }

        if (body == row.Body && visibility == row.Visibility)
        {
            return row;
        }

        var now = clock.Now;
        row.CurrentVersion++;
        row.Body = body;
        row.SearchKey = languageRules.SearchKey(body);
        row.Visibility = visibility;
        row.UpdatedAt = now;
        row.RecordVersion++;

        // Append-only: the earlier versions stay as they were.
        db.NoteVersions.Add(new NoteVersionRow { NoteId = row.NoteId, Version = row.CurrentVersion, Body = body, Visibility = visibility, EditedBy = actor.Actor.Id, EditedAt = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The note changed meanwhile.");
        }

        return row;
    }

    public async Task<Result<NoteRow>> DeleteAsync(NoteDeleteBody request, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindAsync(request.Id, actor, includeDeleted: false, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        var mine = row.CreatedBy == actor.Actor.Id;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 500 })
        {
            return WorkErrors.Invalid("reason is at most 500 characters.");
        }

        if (!mine && !actor.IsManager)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only the author or a manager may delete a note.");
        }

        if (mine && !actor.IsManager && clock.Now.Since(row.CreatedAt) > EditWindow)
        {
            return WorkErrors.Of(WorkErrors.EditWindow, "A note can be deleted by its author for 24 hours; ask a manager afterwards.");
        }

        if (!mine && reason is null)
        {
            return WorkErrors.Of(WorkErrors.NoteRequired, "Deleting someone else's note needs a reason.");
        }

        var now = clock.Now;
        row.DeletedAt = now;
        row.DeletedBy = actor.Actor.Id;
        row.DeleteReason = reason;
        row.UpdatedAt = now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The note changed meanwhile.");
        }

        return row;
    }

    public async Task<Result<NoteView>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var row = Guid.TryParse(id, out var noteId) ? await FindAsync(noteId, actor, includeDeleted: true, cancellationToken).ConfigureAwait(false) : null;
        return row is null ? NotFound() : View(row, actor);
    }

    public async Task<Result<NotePage>> ListAsync(string? objectRef, string? text, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var skip = 0;
        if (limit is < 1 or > MaxLimit || (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out skip) || skip < 0)))
        {
            return WorkErrors.Invalid($"cursor is malformed or limit is outside 1..{MaxLimit}.");
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var query = Visible(objectRef, text, actor);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        // TODO(hardening): keyset cursor; an offset cursor can skip or repeat rows while notes are added.
        var take = limit ?? DefaultLimit;
        var rows = await query.Value.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.NoteId).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new NotePage([.. rows.Take(take).Select(r => View(r, actor))], rows.Count > take ? (skip + take).ToString(CultureInfo.InvariantCulture) : null, take);
    }

    public async Task<Result<IReadOnlyList<NoteVersionView>>> HistoryAsync(string? id, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var row = Guid.TryParse(id, out var noteId) ? await FindAsync(noteId, actor, includeDeleted: true, cancellationToken).ConfigureAwait(false) : null;
        if (row is null)
        {
            return NotFound();
        }

        var versions = await db.NoteVersions.AsNoTracking().Where(v => v.NoteId == row.NoteId).OrderBy(v => v.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<NoteVersionView> views = [.. versions.Select(v => new NoteVersionView(v.Version, v.Body, v.Visibility, v.EditedBy, v.EditedAt))];
        return Result.Success(views);
    }

    /// <summary>CSV of the notes of one object (managers and the admin); cells that start like a formula are neutralised.</summary>
    public async Task<Result<NoteExport>> ExportAsync(NoteExportBody request, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        if (!actor.IsManager)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only a manager may export notes.");
        }

        if (string.IsNullOrWhiteSpace(request.ObjectRef))
        {
            return WorkErrors.Invalid("objectRef is MOD/Type/id.");
        }

        var query = Visible(request.ObjectRef, request.Query, actor);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        var rows = await query.Value.OrderBy(n => n.CreatedAt).ThenBy(n => n.NoteId).Take(MaxExportRows + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > MaxExportRows)
        {
            return WorkErrors.Of(WorkErrors.LimitExceeded, $"An export holds at most {MaxExportRows} notes; narrow it with a query.");
        }

        var csv = new StringBuilder("noteId,object,visibility,author,createdAt,updatedAt,versions,body\r\n");
        foreach (var n in rows)
        {
            csv.Append(string.Join(
                ',',
                n.NoteId.ToString("D"), Cell($"{n.ObjectModule}/{n.ObjectType}/{n.ObjectId}"), n.Visibility, Cell(n.CreatedBy),
                n.CreatedAt.ToString(), n.UpdatedAt.ToString(), n.CurrentVersion.ToString(CultureInfo.InvariantCulture), Cell(n.Body))).Append("\r\n");
        }

        return new NoteExport(csv.ToString(), rows.Count);
    }

    public NoteView View(NoteRow row, WorkActor actor)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(actor);
        var mine = row.CreatedBy == actor.Actor.Id;
        var open = row.DeletedAt is null;
        var inWindow = clock.Now.Since(row.CreatedAt) <= EditWindow;
        return new NoteView(
            row.NoteId, $"{row.ObjectModule}/{row.ObjectType}/{row.ObjectId}", row.Visibility, row.Body, row.CurrentVersion, row.CreatedBy, row.CreatedAt,
            row.UpdatedAt, row.CurrentVersion > 1, !open, row.DeletedAt, row.DeletedBy, row.DeleteReason, row.RecordVersion,
            CanEdit: open && mine && inWindow, CanDelete: open && (actor.IsManager || (mine && inWindow)));
    }

    public async Task<NoteView> ViewAsync(NoteRow row, CancellationToken cancellationToken) =>
        View(row, await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false));

    private static DomainError NotFound() => DomainError.Of(ModuleCode.WRK, PlatformErrors.NotFound, "The note does not exist.");

    private static string? CleanBody(string? body)
    {
        var text = body?.Trim();
        return text is { Length: > 0 and <= MaxBodyLength } && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ? text : null;
    }

    private static string Cell(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return '"' + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private Result<IQueryable<NoteRow>> Visible(string? objectRef, string? text, WorkActor actor)
    {
        var legalEntity = LegalEntity;
        var user = actor.Actor.Id;
        var manager = actor.IsManager;
        IQueryable<NoteRow> query = db.Notes.AsNoTracking().Where(n => n.LegalEntityId == legalEntity && n.DeletedAt == null
            && (n.Visibility == NoteRow.Staff || n.CreatedBy == user || (manager && n.Visibility == NoteRow.Managers)));
        if (!string.IsNullOrWhiteSpace(objectRef))
        {
            var obj = ParseObject(objectRef);
            if (obj.IsFailure)
            {
                return obj.Error!;
            }

            var module = obj.Value.Module.ToString();
            var type = obj.Value.Type;
            var id = obj.Value.Id;
            query = query.Where(n => n.ObjectModule == module && n.ObjectType == type && n.ObjectId == id);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var key = languageRules.SearchKey(text);
            if (key.Length == 0)
            {
                return WorkErrors.Invalid("The search text has no letters or digits.");
            }

            query = query.Where(n => n.SearchKey.Contains(key));
        }

        return Result.Success(query);
    }

    private async Task<NoteRow?> FindAsync(Guid? id, WorkActor actor, bool includeDeleted, CancellationToken cancellationToken)
    {
        if (id is null || id == Guid.Empty)
        {
            return null;
        }

        var legalEntity = LegalEntity;
        var row = await db.Notes.SingleOrDefaultAsync(n => n.NoteId == id && n.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null || (row.DeletedAt is not null && !(includeDeleted && actor.IsManager))
            || !(row.CreatedBy == actor.Actor.Id || row.Visibility == NoteRow.Staff || (row.Visibility == NoteRow.Managers && actor.IsManager)))
        {
            return null;
        }

        return row;
    }
}

/// <summary>A page of notes.</summary>
internal sealed record NotePage(IReadOnlyList<NoteView> Items, string? NextCursor, int Limit);

/// <summary>A CSV export.</summary>
internal sealed record NoteExport(string Csv, int Rows);
