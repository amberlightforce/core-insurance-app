using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Notes;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Work.Api;

/// <summary>REST facade of <c>wrk.Note.*</c> (contracts/openapi/wrk.yaml): notes linked to a business object, with history and CSV export.</summary>
[ApiController]
[Route("api/wrk/v1")]
internal sealed class NotesController : ControllerBase
{
    public const string CreatePermission = "wrk.Note.create";
    public const string ListPermission = "wrk.Note.list";
    public const string GetPermission = "wrk.Note.get";
    public const string SearchPermission = "wrk.Note.search";
    public const string EditPermission = "wrk.Note.edit";
    public const string DeletePermission = "wrk.Note.delete";
    public const string HistoryPermission = "wrk.Note.history";
    public const string ExportPermission = "wrk.Note.export";

    /// <summary>wrk.Note.create.</summary>
    [HttpPost("notes")]
    [Authorize(Policy = CreatePermission)]
    public async Task<IResult> CreateAsync(
        [FromBody] NoteCreateBody request, [FromServices] ICommandHandler<CreateNote, NoteCreateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CreateNote(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Note.list: the notes of one object (<c>objectRef</c> = MOD/Type/id), newest first.</summary>
    [HttpGet("notes")]
    [Authorize(Policy = ListPermission)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? objectRef, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] NoteService notes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectRef))
        {
            return Result.Failure<NoteListPage>(Domain.WorkErrors.Invalid("objectRef is MOD/Type/id.")).ToHttpResult(HttpContext);
        }

        var page = await notes.ListAsync(objectRef, null, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.Map(p => new NoteListPage { Items = [.. p.Items.Select(n => new NoteListItem { Note = n.ToElement() })], NextCursor = p.NextCursor, Limit = p.Limit })
            .ToHttpResult(HttpContext);
    }

    /// <summary>wrk.Note.search: notes whose text contains <c>q</c> (accent and case insensitive), optionally of one object.</summary>
    [HttpGet("notes/search")]
    [Authorize(Policy = SearchPermission)]
    public async Task<IResult> SearchAsync(
        [FromQuery] string? q, [FromQuery] string? objectRef, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] NoteService notes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Result.Failure<NoteSearchPage>(Domain.WorkErrors.Invalid("q is required.")).ToHttpResult(HttpContext);
        }

        var page = await notes.ListAsync(objectRef, q, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.Map(p => new NoteSearchPage { Items = [.. p.Items.Select(n => new NoteSearchItem { Note = n.ToElement() })], NextCursor = p.NextCursor, Limit = p.Limit })
            .ToHttpResult(HttpContext);
    }

    /// <summary>wrk.Note.history: every stored version of one note (<c>id</c>), oldest first.</summary>
    [HttpGet("notes/history")]
    [Authorize(Policy = HistoryPermission)]
    public async Task<IResult> HistoryAsync([FromQuery] string? id, [FromServices] NoteService notes, CancellationToken cancellationToken)
    {
        var versions = await notes.HistoryAsync(id, cancellationToken).ConfigureAwait(false);
        return versions.Map(v => new NoteHistoryPage { Items = [.. v.Select(x => new NoteHistoryItem { Note = x.ToElement() })], NextCursor = null, Limit = v.Count })
            .ToHttpResult(HttpContext);
    }

    /// <summary>wrk.Note.get.</summary>
    [HttpGet("notes/{id}")]
    [Authorize(Policy = GetPermission)]
    public async Task<IResult> GetAsync(string id, [FromServices] NoteService notes, CancellationToken cancellationToken) =>
        (await notes.GetAsync(id, cancellationToken).ConfigureAwait(false)).Map(n => new NoteGetResponse { Note = n.ToElement() }).ToHttpResult(HttpContext);

    /// <summary>wrk.Note.edit.</summary>
    [HttpPost("notes/edit")]
    [Authorize(Policy = EditPermission)]
    public async Task<IResult> EditAsync(
        [FromBody] NoteEditBody request, [FromServices] ICommandHandler<EditNote, NoteEditResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new EditNote(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Note.delete (soft).</summary>
    [HttpPost("notes/delete")]
    [Authorize(Policy = DeletePermission)]
    public async Task<IResult> DeleteAsync(
        [FromBody] NoteDeleteBody request, [FromServices] ICommandHandler<DeleteNote, NoteDeleteResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DeleteNote(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>wrk.Note.export: CSV of one object's notes.</summary>
    [HttpPost("notes/export")]
    [Authorize(Policy = ExportPermission)]
    public async Task<IResult> ExportAsync(
        [FromBody] NoteExportBody request, [FromServices] ICommandHandler<ExportNotes, NoteExportResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExportNotes(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
