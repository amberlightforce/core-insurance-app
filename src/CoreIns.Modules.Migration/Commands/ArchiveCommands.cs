using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Services.Dsar;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Migration.Commands;

/// <summary>Body shared by the DSAR operations on the migration archive (mig.Archive.*). Scope carries optional <c>reason</c>, <c>retentionEnd</c> and <c>note</c>.</summary>
internal sealed record ArchiveDsarBody(Guid DsarId, Guid TaskId, IReadOnlyList<Guid>? SubjectIds, JsonElement? Scope);

/// <summary>One record class outcome for one subject.</summary>
internal sealed record ArchiveSubjectOutcome(Guid SubjectId, string RecordClass, string Outcome, int Count, string? LegalBasis, string? RetentionEnd, string? Detail);

/// <summary>An exported subject's portable data.</summary>
internal sealed record ArchiveSubjectExport(Guid SubjectId, string? Payload);

/// <summary>Response of the archive DSAR operations.</summary>
internal sealed record ArchiveDsarResponse(IReadOnlyList<ArchiveSubjectOutcome> PerItemOutcomes, IReadOnlyList<ArchiveSubjectExport>? Exports = null);

internal enum ArchiveDsarKind
{
    Export,
    RectificationNote,
    Restrict,
    Erase,
}

/// <summary>mig.Archive.subjectExport.</summary>
internal sealed record ArchiveSubjectExportCommand(ArchiveDsarBody Body) : ICommand<ArchiveDsarResponse>;

/// <summary>mig.Archive.rectificationNote.</summary>
internal sealed record ArchiveRectificationNoteCommand(ArchiveDsarBody Body) : ICommand<ArchiveDsarResponse>;

/// <summary>mig.Archive.restrict.</summary>
internal sealed record ArchiveRestrictCommand(ArchiveDsarBody Body) : ICommand<ArchiveDsarResponse>;

/// <summary>mig.Archive.erase: fails closed, the migration archive is never physically erased (audit archive, D-SL6-07); it is restricted.</summary>
internal sealed record ArchiveEraseCommand(ArchiveDsarBody Body) : ICommand<ArchiveDsarResponse>;

/// <summary>Handlers of the archive DSAR operations; all delegate to <see cref="MigrationDsarParticipant"/>.</summary>
internal sealed class ArchiveDsarHandler(IEnumerable<IDsarParticipant> participants, RequestContext context)
    : ICommandHandler<ArchiveSubjectExportCommand, ArchiveDsarResponse>,
      ICommandHandler<ArchiveRectificationNoteCommand, ArchiveDsarResponse>,
      ICommandHandler<ArchiveRestrictCommand, ArchiveDsarResponse>,
      ICommandHandler<ArchiveEraseCommand, ArchiveDsarResponse>
{
    private const int MaxSubjects = 100;
    private const int MaxNote = 2000;

    public Task<Result<ArchiveDsarResponse>> HandleAsync(ArchiveSubjectExportCommand command, CancellationToken cancellationToken) =>
        RunAsync(command.Body, ArchiveDsarKind.Export, cancellationToken);

    public Task<Result<ArchiveDsarResponse>> HandleAsync(ArchiveRectificationNoteCommand command, CancellationToken cancellationToken) =>
        RunAsync(command.Body, ArchiveDsarKind.RectificationNote, cancellationToken);

    public Task<Result<ArchiveDsarResponse>> HandleAsync(ArchiveRestrictCommand command, CancellationToken cancellationToken) =>
        RunAsync(command.Body, ArchiveDsarKind.Restrict, cancellationToken);

    public Task<Result<ArchiveDsarResponse>> HandleAsync(ArchiveEraseCommand command, CancellationToken cancellationToken) =>
        RunAsync(command.Body, ArchiveDsarKind.Erase, cancellationToken);

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, detail);

    private static string? ScopeText(JsonElement? scope, string name, int max, out bool bad)
    {
        bad = false;
        if (scope is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > max)
        {
            bad = true;
            return null;
        }

        return value.GetString();
    }

    private async Task<Result<ArchiveDsarResponse>> RunAsync(ArchiveDsarBody body, ArchiveDsarKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.DsarId == Guid.Empty || body.TaskId == Guid.Empty)
        {
            return Invalid("dsarId and taskId are required.");
        }

        if (body.SubjectIds is not { Count: > 0 } || body.SubjectIds.Count > MaxSubjects || body.SubjectIds.Any(s => s == Guid.Empty))
        {
            return Invalid($"Give between 1 and {MaxSubjects} subject ids.");
        }

        if (body.Scope is { } scopeElement && scopeElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return Invalid("scope must be an object.");
        }

        var reason = ScopeText(body.Scope, "reason", 500, out var badReason);
        var note = ScopeText(body.Scope, "note", MaxNote, out var badNote);
        var retentionText = ScopeText(body.Scope, "retentionEnd", 10, out var badRetention);
        DateOnly? retentionEnd = null;
        if (retentionText is not null)
        {
            if (!DateOnly.TryParseExact(retentionText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                badRetention = true;
            }
            else
            {
                retentionEnd = parsed;
            }
        }

        if (badReason || badNote || badRetention)
        {
            return Invalid("scope.reason, scope.note or scope.retentionEnd is not valid.");
        }

        if (kind == ArchiveDsarKind.RectificationNote && note is null)
        {
            return Invalid("scope.note is required for a rectification note.");
        }

        // Fail closed: without the MIG participant nothing is reported as done.
        var participant = participants.OfType<MigrationDsarParticipant>().SingleOrDefault();
        if (participant is null)
        {
            return Invalid("The migration archive participant is not available.");
        }

        var retention = retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var outcomes = new List<ArchiveSubjectOutcome>();
        var exports = new List<ArchiveSubjectExport>();
        var actor = context.Actor.ToString();
        foreach (var subjectId in body.SubjectIds.Distinct())
        {
            var subject = SubjectRef.ForParty(PartyId.From(subjectId));
            var dsar = new DsarContext(body.DsarId, actor, null);
            DsarModuleResult result;
            switch (kind)
            {
                case ArchiveDsarKind.Export:
                    result = await participant.ExportAsync(subject, dsar, cancellationToken).ConfigureAwait(false);
                    exports.Add(new ArchiveSubjectExport(subjectId, result.ExportPayload));
                    break;
                case ArchiveDsarKind.RectificationNote:
                    // TODO(hardening): persist the note against the archive records; today it is held by the audit trail of this command only.
                    result = await participant.PreviewAsync(subject, null, cancellationToken).ConfigureAwait(false);
                    break;
                case ArchiveDsarKind.Restrict:
                    result = context.DryRun
                        ? await participant.PreviewAsync(subject, retention, cancellationToken).ConfigureAwait(false)
                        : await participant.RestrictAsync(subject, reason ?? "DSAR restrict request", MigrationDsarParticipant.Basis, retentionEnd, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    // Retention holds win over erasure: the participant restricts and never deletes. A dry run writes nothing.
                    result = context.DryRun
                        ? await participant.PreviewAsync(subject, null, cancellationToken).ConfigureAwait(false)
                        : await participant.EraseAsync(subject, dsar, cancellationToken).ConfigureAwait(false);
                    break;
            }

            outcomes.AddRange(result.Outcomes.Select(o => new ArchiveSubjectOutcome(
                subjectId, o.RecordClass,
                kind == ArchiveDsarKind.RectificationNote && o.Kind != DsarOutcomeKind.NotHeld ? "NOTED" : o.Kind.ToString().ToUpperInvariant(),
                o.RecordCount, o.LegalBasis, o.RetentionEnd, o.Detail)));
        }

        return new ArchiveDsarResponse(outcomes, kind == ArchiveDsarKind.Export ? exports : null);
    }
}
