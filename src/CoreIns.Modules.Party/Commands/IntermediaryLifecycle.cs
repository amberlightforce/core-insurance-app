using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using DomainStatus = CoreIns.Modules.Party.Domain.IntermediaryStatus;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Intermediary.suspend</c>: Active to Suspended (REQ-PTY-198); <c>pty.ProducerCode.validate</c> then refuses the codes.</summary>
internal sealed record SuspendIntermediary(IntermediarySuspendRequest Request) : ICommand<IntermediarySuspendResponse>;

/// <summary><c>pty.Intermediary.terminate</c>: ends every producer code (REQ-PTY-202) and emits <c>IntermediaryLicenceChanged</c>.</summary>
internal sealed record TerminateIntermediary(IntermediaryTerminateRequest Request) : ICommand<IntermediaryTerminateResponse>;

/// <summary><c>pty.Intermediary.importRegister</c>: idempotent register extract import keyed by register number.</summary>
internal sealed record ImportIntermediaryRegister(IntermediaryImportRegisterRequest Request) : ICommand<IntermediaryImportRegisterResponse>;

internal sealed class SuspendIntermediaryValidator : AbstractValidator<SuspendIntermediary>
{
    public SuspendIntermediaryValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class TerminateIntermediaryValidator : AbstractValidator<TerminateIntermediary>
{
    public TerminateIntermediaryValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class ImportIntermediaryRegisterValidator : AbstractValidator<ImportIntermediaryRegister>
{
    public ImportIntermediaryRegisterValidator() => RuleFor(c => c.Request.Content).NotEmpty().MaximumLength(5_000_000);
}

/// <summary>CPD compliance computed from the hours table (REQ-PTY-191).</summary>
internal static class CpdRules
{
    /// <summary>Annual CPD hours required when the register extract does not state them. TODO(hardening): country-pack parameter.</summary>
    public const decimal DefaultRequiredHours = 20m;

    public static IntermediaryView.CpdStatusValue Status(decimal? hours, decimal? required)
    {
        var need = required ?? DefaultRequiredHours;
        if (hours is null)
        {
            return IntermediaryView.CpdStatusValue.NonCompliant;
        }

        if (hours >= need)
        {
            return IntermediaryView.CpdStatusValue.Compliant;
        }

        return hours >= need / 2 ? IntermediaryView.CpdStatusValue.AtRisk : IntermediaryView.CpdStatusValue.NonCompliant;
    }

    public static string Code(IntermediaryView.CpdStatusValue status) => status switch
    {
        IntermediaryView.CpdStatusValue.Compliant => "COMPLIANT",
        IntermediaryView.CpdStatusValue.AtRisk => "AT_RISK",
        _ => "NON_COMPLIANT",
    };
}

/// <summary>Shared concurrency error and event publishing of the lifecycle commands.</summary>
internal static class IntermediaryLifecycleSupport
{
    public static DomainError Stale(IntermediaryRow row) =>
        new(ErrorCode.For(ModuleCode.PTY, "STALE"), "The intermediary changed meanwhile; reload it.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["currentRecordVersion"] = row.RecordVersion.ToString(CultureInfo.InvariantCulture) },
        };

    public static void PublishStatus(IEventPublisher events, IntermediaryRow row, string oldStatus) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(IntermediaryStatusChangedV1.Descriptor), "Intermediary", row.IntermediaryId.Value.ToString(),
            new IntermediaryStatusChangedV1 { IntermediaryId = row.IntermediaryId, OldStatus = oldStatus, NewStatus = row.Status },
            BusinessKeys.Empty.With("intermediaryId", row.IntermediaryId.Value.ToString())));

    public static void PublishLicence(
        IEventPublisher events, IntermediaryId id, IntermediaryLicenceChangedV1.StatusKindValue kind, string newStatus, BusinessDate? expiry = null) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(IntermediaryLicenceChangedV1.Descriptor), "Intermediary", id.Value.ToString(),
            new IntermediaryLicenceChangedV1 { IntermediaryId = id, StatusKind = kind, NewStatus = newStatus, Expiry = expiry },
            BusinessKeys.Empty.With("intermediaryId", id.Value.ToString())));
}

internal sealed class SuspendIntermediaryHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, IntermediaryQueries queries)
    : ICommandHandler<SuspendIntermediary, IntermediarySuspendResponse>
{
    public async Task<Result<IntermediarySuspendResponse>> HandleAsync(SuspendIntermediary command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var row = await db.Intermediaries.SingleOrDefaultAsync(i => i.IntermediaryId == command.Request.IntermediaryId && i.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The intermediary does not exist.");
        }

        if (row.RecordVersion != command.Request.ExpectedRecordVersion)
        {
            return IntermediaryLifecycleSupport.Stale(row);
        }

        var from = Codes.Parse<DomainStatus>(row.Status);
        var to = IntermediaryStateModel.Machine.Fire(from, IntermediaryTrigger.Suspend);
        if (to.IsFailure)
        {
            return to.Error;
        }

        row.Status = Codes.Of(to.Value);
        row.RecordVersion++;
        row.UpdatedAt = clock.Now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "The intermediary changed meanwhile; reload it.");
        }

        IntermediaryLifecycleSupport.PublishStatus(events, row, Codes.Of(from));
        IntermediaryLifecycleSupport.PublishLicence(events, row.IntermediaryId, IntermediaryLicenceChangedV1.StatusKindValue.Authorisation, row.Status);
        return new IntermediarySuspendResponse { Intermediary = (await queries.ViewAsync(row.IntermediaryId, cancellationToken).ConfigureAwait(false))! };
    }
}

internal sealed class TerminateIntermediaryHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, IntermediaryQueries queries)
    : ICommandHandler<TerminateIntermediary, IntermediaryTerminateResponse>
{
    public async Task<Result<IntermediaryTerminateResponse>> HandleAsync(TerminateIntermediary command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var row = await db.Intermediaries.SingleOrDefaultAsync(i => i.IntermediaryId == command.Request.IntermediaryId && i.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The intermediary does not exist.");
        }

        if (row.RecordVersion != command.Request.ExpectedRecordVersion)
        {
            return IntermediaryLifecycleSupport.Stale(row);
        }

        var from = Codes.Parse<DomainStatus>(row.Status);
        var to = IntermediaryStateModel.Machine.Fire(from, IntermediaryTrigger.Terminate);
        if (to.IsFailure)
        {
            return to.Error;
        }

        var now = clock.Now;
        var effective = command.Request.EffectiveDate ?? new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));
        row.Status = Codes.Of(to.Value);
        row.RecordVersion++;
        row.UpdatedAt = now;

        // REQ-PTY-202: the producer codes end with the intermediary. valid_to must stay after valid_from (check constraint).
        var codes = await db.ProducerCodes.Where(c => c.IntermediaryId == row.IntermediaryId && c.LegalEntityId == legalEntity).ToListAsync(cancellationToken).ConfigureAwait(false);
        var terminated = Codes.Of(ProducerCodeStatus.Terminated);
        var ended = new List<ProducerCodeRow>();
        foreach (var code in codes.Where(c => c.Status != terminated))
        {
            code.Status = terminated;
            code.ValidTo = effective > code.ValidFrom ? effective : code.ValidFrom.AddDays(1);
            code.RecordVersion++;
            ended.Add(code);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "The intermediary changed meanwhile; reload it.");
        }

        foreach (var code in ended)
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ProducerCodeChangedV1.Descriptor), "Intermediary", row.IntermediaryId.Value.ToString(),
                new ProducerCodeChangedV1
                {
                    IntermediaryId = row.IntermediaryId,
                    ProducerCode = code.Code,
                    Status = code.Status,
                    AuthoritiesSummary = IntermediaryQueries.AuthoritiesSummary(code),
                    ParentProducerCode = null,
                },
                BusinessKeys.Empty.With("intermediaryId", row.IntermediaryId.Value.ToString())));
        }

        IntermediaryLifecycleSupport.PublishStatus(events, row, Codes.Of(from));
        IntermediaryLifecycleSupport.PublishLicence(events, row.IntermediaryId, IntermediaryLicenceChangedV1.StatusKindValue.Authorisation, row.Status, effective);
        return new IntermediaryTerminateResponse
        {
            Intermediary = (await queries.ViewAsync(row.IntermediaryId, cancellationToken).ConfigureAwait(false))!,
            TerminatedProducerCodes = [.. ended.Select(c => c.Code)],
        };
    }
}

/// <summary>
/// Imports a register extract (the <c>IntermediaryRegister</c> SPI's file form) keyed by register number: register data and
/// status, CPD hours and the hierarchy parent. Re-importing a file changes nothing (idempotent by register number).
/// </summary>
internal sealed class ImportIntermediaryRegisterHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection)
    : ICommandHandler<ImportIntermediaryRegister, IntermediaryImportRegisterResponse>
{
    public async Task<Result<IntermediaryImportRegisterResponse>> HandleAsync(ImportIntermediaryRegister command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var rows = command.Request.Format == IntermediaryImportRegisterRequest.FormatValue.Json
            ? RegisterExtract.ParseJson(command.Request.Content)
            : RegisterExtract.ParseCsv(command.Request.Content);
        if (rows.IsFailure)
        {
            return rows.Error;
        }

        var unmatched = new List<string>();
        var rejected = new List<string>();
        int updated = 0, unchanged = 0, line = 0;
        foreach (var item in rows.Value)
        {
            line++;
            if (item.Problem is not null)
            {
                rejected.Add($"{line}: {item.Problem}");
                continue;
            }

            var intermediary = await db.Intermediaries.SingleOrDefaultAsync(i => i.LegalEntityId == legalEntity && i.RegisterNumber == item.RegisterNumber, cancellationToken)
                .ConfigureAwait(false);
            if (intermediary is null)
            {
                unmatched.Add(item.RegisterNumber);
                continue;
            }

            var changed = false;
            var registerChanged = false;
            if (item.RegisterStatus is not null && item.RegisterStatus != intermediary.RegisterStatus)
            {
                intermediary.RegisterStatus = item.RegisterStatus;
                registerChanged = changed = true;
            }

            changed |= Set(item.RegisterName, intermediary.RegisterName, v => intermediary.RegisterName = v);
            changed |= Set(item.Chamber, intermediary.Chamber, v => intermediary.Chamber = v);
            changed |= Set(item.RegistrationCategory, intermediary.RegistrationCategory, v => intermediary.RegistrationCategory = v);

            if (item.ParentRegisterNumber is not null)
            {
                var parent = await db.Intermediaries.SingleOrDefaultAsync(i => i.LegalEntityId == legalEntity && i.RegisterNumber == item.ParentRegisterNumber, cancellationToken)
                    .ConfigureAwait(false);
                if (parent is null || parent.IntermediaryId == intermediary.IntermediaryId)
                {
                    rejected.Add($"{line}: unknown or self parentRegisterNumber");
                    continue;
                }

                if (intermediary.ParentIntermediaryId != parent.IntermediaryId)
                {
                    intermediary.ParentIntermediaryId = parent.IntermediaryId;
                    changed = true;
                }
            }

            if (item.CpdYear is { } year && item.CpdHours is { } hours)
            {
                var cpd = await db.IntermediaryCpd.SingleOrDefaultAsync(c => c.IntermediaryId == intermediary.IntermediaryId && c.Year == year, cancellationToken)
                    .ConfigureAwait(false);
                var required = item.CpdRequiredHours ?? cpd?.RequiredHours ?? CpdRules.DefaultRequiredHours;
                var newStatus = CpdRules.Status(hours, required);
                if (cpd is null)
                {
                    db.IntermediaryCpd.Add(new IntermediaryCpdRow
                    {
                        IntermediaryId = intermediary.IntermediaryId, Year = year, Hours = hours, RequiredHours = required, RecordedAt = clock.Now,
                    });
                    changed = true;
                    IntermediaryLifecycleSupport.PublishLicence(events, intermediary.IntermediaryId, IntermediaryLicenceChangedV1.StatusKindValue.Cpd, CpdRules.Code(newStatus));
                }
                else if (cpd.Hours != hours || cpd.RequiredHours != required)
                {
                    var oldStatus = CpdRules.Status(cpd.Hours, cpd.RequiredHours);
                    cpd.Hours = hours;
                    cpd.RequiredHours = required;
                    cpd.RecordedAt = clock.Now;
                    changed = true;
                    if (oldStatus != newStatus)
                    {
                        IntermediaryLifecycleSupport.PublishLicence(events, intermediary.IntermediaryId, IntermediaryLicenceChangedV1.StatusKindValue.Cpd, CpdRules.Code(newStatus));
                    }
                }
            }

            if (!changed)
            {
                unchanged++;
                continue;
            }

            intermediary.RecordVersion++;
            intermediary.UpdatedAt = clock.Now;
            intermediary.RegisterVerifiedAt = clock.Now;
            updated++;
            if (registerChanged)
            {
                IntermediaryLifecycleSupport.PublishLicence(
                    events, intermediary.IntermediaryId, IntermediaryLicenceChangedV1.StatusKindValue.Register, intermediary.RegisterStatus);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "An intermediary changed meanwhile; retry the import.");
        }

        return new IntermediaryImportRegisterResponse
        {
            Processed = line, Updated = updated, Unchanged = unchanged, UnmatchedRegisterNumbers = unmatched, RejectedRows = rejected,
        };
    }

    private static bool Set(string? incoming, string current, Action<string> assign)
    {
        if (string.IsNullOrWhiteSpace(incoming) || incoming.Trim() == current)
        {
            return false;
        }

        assign(incoming.Trim());
        return true;
    }
}

/// <summary>One row of a register extract; <see cref="Problem"/> is set when the row is unusable.</summary>
internal sealed record RegisterRow(
    string RegisterNumber, string? RegisterStatus, string? RegisterName, string? Chamber, string? RegistrationCategory, int? CpdYear, decimal? CpdHours,
    decimal? CpdRequiredHours, string? ParentRegisterNumber, string? Problem);

/// <summary>Parses register extracts (JSON array of objects, or CSV with a header row).</summary>
internal static class RegisterExtract
{
    private const int MaxRows = 20_000;

    public static Result<IReadOnlyList<RegisterRow>> ParseJson(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return DomainError.Of(ModuleCode.PTY, "VALIDATION", "The JSON register extract must be an array of rows.");
            }

            var list = new List<RegisterRow>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= MaxRows)
                {
                    return DomainError.Of(ModuleCode.PTY, "VALIDATION", "The register extract is too large.");
                }

                var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in element.EnumerateObject())
                    {
                        map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    }
                }

                list.Add(ToRow(map));
            }

            return list;
        }
        catch (JsonException)
        {
            return DomainError.Of(ModuleCode.PTY, "VALIDATION", "The register extract is not valid JSON.");
        }
    }

    public static Result<IReadOnlyList<RegisterRow>> ParseCsv(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines.Count > MaxRows + 1)
        {
            return DomainError.Of(ModuleCode.PTY, "VALIDATION", "The CSV register extract needs a header and at most 20000 rows.");
        }

        var header = Split(lines[0]);
        var list = new List<RegisterRow>();
        foreach (var raw in lines.Skip(1))
        {
            var cells = Split(raw);
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < cells.Count; i++)
            {
                map[header[i].Trim()] = cells[i];
            }

            list.Add(ToRow(map));
        }

        return list;
    }

    private static RegisterRow ToRow(Dictionary<string, string?> map)
    {
        string? Get(string key) => map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var number = Get("registerNumber");
        RegisterRow Bad(string problem) => new(number ?? string.Empty, null, null, null, null, null, null, null, null, problem);
        if (number is null || number.Length > 50)
        {
            return Bad("registerNumber missing or too long");
        }

        var status = Get("registerStatus")?.ToUpperInvariant();
        if (status is not null && status is not ("ACTIVE" or "SUSPENDED" or "DELETED"))
        {
            return Bad("registerStatus must be ACTIVE, SUSPENDED or DELETED");
        }

        int? year = null;
        decimal? hours = null, required = null;
        if (Get("cpdYear") is { } y)
        {
            if (!int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1990 or > 2200)
            {
                return Bad("cpdYear invalid");
            }

            year = parsed;
        }

        if (Get("cpdHours") is { } h)
        {
            if (!decimal.TryParse(h, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 9999)
            {
                return Bad("cpdHours invalid");
            }

            hours = parsed;
        }

        if (Get("cpdRequiredHours") is { } r)
        {
            if (!decimal.TryParse(r, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 9999)
            {
                return Bad("cpdRequiredHours invalid");
            }

            required = parsed;
        }

        if ((year is null) != (hours is null))
        {
            return Bad("cpdYear and cpdHours go together");
        }

        return new RegisterRow(number, status, Get("registerName"), Get("chamber"), Get("registrationCategory"), year, hours, required, Get("parentRegisterNumber"), null);
    }

    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}

/// <summary>Audit facts of the lifecycle commands.</summary>
internal sealed class IntermediaryLifecycleAuditor :
    ICommandAuditor<SuspendIntermediary, IntermediarySuspendResponse>,
    ICommandAuditor<TerminateIntermediary, IntermediaryTerminateResponse>,
    ICommandAuditor<ImportIntermediaryRegister, IntermediaryImportRegisterResponse>
{
    public CommandAuditFacts Describe(SuspendIntermediary command, Result<IntermediarySuspendResponse>? result) =>
        Facts(command.Request.IntermediaryId, "ACTIVE", result is { IsSuccess: true } ok ? ok.Value.Intermediary.Status.ToString() : null);

    public CommandAuditFacts Describe(TerminateIntermediary command, Result<IntermediaryTerminateResponse>? result) =>
        Facts(command.Request.IntermediaryId, null, result is { IsSuccess: true } ok ? ok.Value.Intermediary.Status.ToString() : null);

    public CommandAuditFacts Describe(ImportIntermediaryRegister command, Result<IntermediaryImportRegisterResponse>? result) => new();

    private static CommandAuditFacts Facts(IntermediaryId id, string? before, string? after) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.PTY, "Intermediary", id),
        BusinessKeys = BusinessKeys.Empty.With("intermediaryId", id.Value.ToString()),
        Changes = AuditDiff.Compute(before is null ? null : new { status = before }, new { status = after ?? "UNCHANGED" }),
    };
}
