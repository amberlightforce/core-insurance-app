using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using System.Text.Json;

namespace CoreIns.Modules.Work.Notes;

/// <summary><c>wrk.Note.create</c>.</summary>
internal sealed record CreateNote(NoteCreateBody Request) : ICommand<NoteCreateResponse>;

internal sealed class CreateNoteHandler(NoteService notes) : ICommandHandler<CreateNote, NoteCreateResponse>
{
    public async Task<Result<NoteCreateResponse>> HandleAsync(CreateNote command, CancellationToken cancellationToken)
    {
        var row = await notes.CreateAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new NoteCreateResponse { Note = (await notes.ViewAsync(row.Value, cancellationToken).ConfigureAwait(false)).ToElement() };
    }
}

internal sealed class CreateNoteAuditor : ICommandAuditor<CreateNote, NoteCreateResponse>
{
    public CommandAuditFacts Describe(CreateNote command, Result<NoteCreateResponse>? result) =>
        new() { ObjectRef = NoteService.ParseObject(command.Request.ObjectRef) is { IsSuccess: true } parsed ? parsed.Value : null };
}

/// <summary><c>wrk.Note.edit</c>: adds a version; earlier text stays in the history.</summary>
internal sealed record EditNote(NoteEditBody Request) : ICommand<NoteEditResponse>;

internal sealed class EditNoteHandler(NoteService notes) : ICommandHandler<EditNote, NoteEditResponse>
{
    public async Task<Result<NoteEditResponse>> HandleAsync(EditNote command, CancellationToken cancellationToken)
    {
        var row = await notes.EditAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new NoteEditResponse { Note = (await notes.ViewAsync(row.Value, cancellationToken).ConfigureAwait(false)).ToElement() };
    }
}

internal sealed class EditNoteAuditor : ICommandAuditor<EditNote, NoteEditResponse>
{
    public CommandAuditFacts Describe(EditNote command, Result<NoteEditResponse>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.WRK, "Note", (command.Request.Id ?? Guid.Empty).ToString("D")) };
}

/// <summary><c>wrk.Note.delete</c>: soft delete.</summary>
internal sealed record DeleteNote(NoteDeleteBody Request) : ICommand<NoteDeleteResponse>;

internal sealed class DeleteNoteHandler(NoteService notes) : ICommandHandler<DeleteNote, NoteDeleteResponse>
{
    public async Task<Result<NoteDeleteResponse>> HandleAsync(DeleteNote command, CancellationToken cancellationToken)
    {
        var row = await notes.DeleteAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new NoteDeleteResponse { Note = (await notes.ViewAsync(row.Value, cancellationToken).ConfigureAwait(false)).ToElement() };
    }
}

internal sealed class DeleteNoteAuditor : ICommandAuditor<DeleteNote, NoteDeleteResponse>
{
    public CommandAuditFacts Describe(DeleteNote command, Result<NoteDeleteResponse>? result) =>
        new() { ObjectRef = new ObjectRef(ModuleCode.WRK, "Note", (command.Request.Id ?? Guid.Empty).ToString("D")) };
}

/// <summary><c>wrk.Note.export</c>: CSV of one object's notes, returned as <c>note = {fileName, contentType, rows, content}</c>.</summary>
internal sealed record ExportNotes(NoteExportBody Request) : ICommand<NoteExportResponse>;

internal sealed class ExportNotesHandler(NoteService notes) : ICommandHandler<ExportNotes, NoteExportResponse>
{
    public async Task<Result<NoteExportResponse>> HandleAsync(ExportNotes command, CancellationToken cancellationToken)
    {
        var export = await notes.ExportAsync(command.Request, cancellationToken).ConfigureAwait(false);
        if (export.IsFailure)
        {
            return export.Error!;
        }

        var element = JsonSerializer.SerializeToElement(new { fileName = "notes.csv", contentType = "text/csv", rows = export.Value.Rows, content = export.Value.Csv });
        return new NoteExportResponse { Note = element };
    }
}

internal sealed class ExportNotesAuditor : ICommandAuditor<ExportNotes, NoteExportResponse>
{
    public CommandAuditFacts Describe(ExportNotes command, Result<NoteExportResponse>? result) =>
        new() { ObjectRef = NoteService.ParseObject(command.Request.ObjectRef) is { IsSuccess: true } parsed ? parsed.Value : null };
}
