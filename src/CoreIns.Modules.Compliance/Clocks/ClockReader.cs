using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Clocks;

/// <summary><c>cmp.Clock.get</c> and <c>cmp.Clock.query</c> (REQ-CMP-096, REQ-CMP-117), filtered by legal entity.</summary>
internal sealed class ClockReader(ComplianceDbContext db)
{
    private const int DefaultLimit = 50;
    public const int MaxPage = 200;
    private const int MaxLimit = MaxPage;

    /// <summary>The instance with this id.</summary>
    public async Task<ClockInstanceView?> GetByIdAsync(LegalEntityId legalEntity, Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Clocks.AsNoTracking().SingleOrDefaultAsync(c => c.LegalEntityId == legalEntity && c.ClockId == new StatutoryClockId(id), cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToView(row);
    }

    /// <summary>A page of instances, newest id last (UUIDv7 order); the cursor is the offset of the next page (TODO(hardening): keyset cursor).</summary>
    public async Task<(IReadOnlyList<ClockInstanceView> Items, string? NextCursor, int Limit)> QueryAsync(
        LegalEntityId legalEntity, string? cursor, int? limit, Guid? id, ObjectRef? subject, string? code, string? status, string? deadlineRange, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var query = db.Clocks.AsNoTracking().Where(c => c.LegalEntityId == legalEntity);
        if (id is { } wanted)
        {
            var clockId = new StatutoryClockId(wanted);
            query = query.Where(c => c.ClockId == clockId);
        }

        if (subject is not null)
        {
            var module = subject.Module.ToString();
            query = query.Where(c => c.SubjectModule == module && c.SubjectType == subject.Type && c.SubjectId == subject.Id);
        }

        if (!string.IsNullOrEmpty(code))
        {
            query = query.Where(c => c.ClockCode == code);
        }

        if (!string.IsNullOrEmpty(status))
        {
            var upper = status.ToUpperInvariant();
            query = query.Where(c => c.Status == upper);
        }

        if (ParseRange(deadlineRange) is { } range)
        {
            if (range.From is { } start)
            {
                query = query.Where(c => c.Deadline >= start);
            }

            if (range.To is { } end)
            {
                query = query.Where(c => c.Deadline <= end);
            }
        }

        var skip = int.TryParse(cursor, out var offset) && offset > 0 ? offset : 0;

        var rows = await query.OrderBy(c => c.ClockId).Skip(skip).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(take).ToList();
        var next = rows.Count > take ? (skip + take).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return ([.. page.Select(ToView)], next, take);
    }

    /// <summary>Parses <c>from/to</c> (either side may be empty).</summary>
    private static (Instant? From, Instant? To)? ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        var parts = range.Split('/', 2);
        Instant? from = parts[0].Length > 0 && Instant.TryParse(parts[0], out var f) ? f : null;
        Instant? to = parts.Length > 1 && parts[1].Length > 0 && Instant.TryParse(parts[1], out var t) ? t : null;
        return (from, to);
    }

    /// <summary>Parses <c>MOD/Type/id</c> (the text form of an <see cref="ObjectRef"/>).</summary>
    public static ObjectRef? ParseSubject(string? text)
    {
        var parts = text?.Split('/', 3);
        return parts is { Length: 3 } && Enum.TryParse<ModuleCode>(parts[0], ignoreCase: true, out var module) && parts[1].Length > 0 && parts[2].Length > 0
            ? new ObjectRef(module, parts[1], parts[2])
            : null;
    }

    public static ClockInstanceView ToView(ClockRow row) => new()
    {
        InstanceId = row.ClockId.Value,
        ClockCode = ClockCode.Parse(row.ClockCode),
        SubjectRef = ClockEngine.SubjectOf(row),
        Key = row.Key,
        Kind = Enum.Parse<ClockInstanceView.KindValue>(row.Kind.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
        Status = Enum.Parse<ClockInstanceView.StatusValue>(row.Status, ignoreCase: true),
        StartedAt = row.StartedAt,
        Deadline = row.Deadline,
        PausedAt = row.PausedAt,
        ClosedAt = row.ClosedAt,
        Outcome = row.Outcome,
        MetLate = row.MetLate,
        Variant = row.Variant,
        ValueVersion = row.ValueVersion,
    };

    public static ClockQueryItem ToItem(ClockInstanceView v) => new()
    {
        InstanceId = v.InstanceId,
        ClockCode = v.ClockCode,
        SubjectRef = v.SubjectRef,
        Key = v.Key,
        Kind = v.Kind is { } k ? Enum.Parse<ClockQueryItem.KindValue>(k.ToString(), ignoreCase: true) : null,
        Status = Enum.Parse<ClockQueryItem.StatusValue>(v.Status.ToString(), ignoreCase: true),
        StartedAt = v.StartedAt,
        Deadline = v.Deadline,
        PausedAt = v.PausedAt,
        ClosedAt = v.ClosedAt,
        Outcome = v.Outcome,
        MetLate = v.MetLate,
        Variant = v.Variant,
        ValueVersion = v.ValueVersion,
    };

    public static ClockDefListItem ToItem(ClockDefinition d) => new()
    {
        Code = ClockCode.Parse(d.Code),
        Name = d.Name,
        Kind = d.Kind == Contracts.StatutoryClockKind.WaitingPeriod ? ClockDefListItem.KindValue.WaitingPeriod : ClockDefListItem.KindValue.Deadline,
        Duration = d.Duration,
        Jurisdiction = d.Jurisdiction,
        LegalStatus = d.LegalStatus,
        Source = d.Source,
        Extendable = d.Extendable,
        WarnBefore = d.WarnBefore,
    };

    public static ClockDefGetResponse ToGet(ClockDefinition d) => new()
    {
        Code = ClockCode.Parse(d.Code),
        Name = d.Name,
        Kind = d.Kind == Contracts.StatutoryClockKind.WaitingPeriod ? ClockDefGetResponse.KindValue.WaitingPeriod : ClockDefGetResponse.KindValue.Deadline,
        Duration = d.Duration,
        Jurisdiction = d.Jurisdiction,
        LegalStatus = d.LegalStatus,
        Source = d.Source,
        Extendable = d.Extendable,
        WarnBefore = d.WarnBefore,
    };
}
