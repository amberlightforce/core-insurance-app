using System.Globalization;
using CoreIns.Modules.Finance.Contracts;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>Calendar-month finance periods and their lock state (REQ-POL-108 G5; close workflow arrives with W5-FIN close).</summary>
internal sealed class FinancePeriods(FinanceDbContext db, IClock clock)
{
    public const string Open = "OPEN";
    public const string Locked = "LOCKED";

    /// <summary>How many months a correction looks ahead for an open period before it fails closed.</summary>
    private const int MaxLockedRun = 36;

    public static string CodeOf(BusinessDate date) => date.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public async Task<string> StateAsync(Guid legalEntity, string periodCode, CancellationToken cancellationToken) =>
        await db.Periods.AsNoTracking().Where(p => p.LegalEntityId == legalEntity && p.PeriodCode == periodCode).Select(p => p.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? Open;

    public async Task<bool> IsLockedAsync(Guid legalEntity, BusinessDate date, CancellationToken cancellationToken) =>
        await StateAsync(legalEntity, CodeOf(date), cancellationToken).ConfigureAwait(false) == Locked;

    /// <summary>The first day, on or after <paramref name="date"/>, whose month is not locked: <paramref name="date"/> itself when open; null after a run of locked months.</summary>
    public async Task<BusinessDate?> FirstOpenAsync(Guid legalEntity, BusinessDate date, CancellationToken cancellationToken)
    {
        var current = date;
        for (var month = 0; month < MaxLockedRun; month++)
        {
            if (!await IsLockedAsync(legalEntity, current, cancellationToken).ConfigureAwait(false))
            {
                return current;
            }

            current = new BusinessDate(new DateOnly(current.Value.Year, current.Value.Month, 1).AddMonths(1));
        }

        return null;
    }

    /// <summary>Sets the state of a month (dev endpoint; the close workflow replaces it).</summary>
    public async Task SetStateAsync(Guid legalEntity, string periodCode, string state, CancellationToken cancellationToken)
    {
        var row = await db.Periods.SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.PeriodCode == periodCode, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.Periods.Add(new FinancialPeriodRow { PeriodId = Guid.CreateVersion7(), LegalEntityId = legalEntity, PeriodCode = periodCode, Status = state, CreatedAt = clock.Now });
        }
        else
        {
            row.Status = state;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>In-process <see cref="IFinancePeriodService"/>: the read-only period state POL asks for out-of-sequence bookings.</summary>
internal sealed class FinancePeriodService(FinancePeriods periods, ILegalEntityDirectory legalEntities, IClock clock, IOptions<FinanceOptions> options) : IFinancePeriodService
{
    public async Task<PeriodStatusResponse> StatusAsync(string legalEntity, ValidAt? validAt = null, CancellationToken cancellationToken = default)
    {
        var id = legalEntities.Resolve(LegalEntityCode.Parse(legalEntity));
        var date = validAt is { } at ? at.Date ?? at.Instant?.ToBusinessDate(options.Value.Zone) : null;
        date ??= clock.Now.ToBusinessDate(options.Value.Zone);
        var code = FinancePeriods.CodeOf(date.Value);
        var state = await periods.StateAsync(id.Value, code, cancellationToken).ConfigureAwait(false);
        return new PeriodStatusResponse { LegalEntity = legalEntity, Period = code, State = state, ClosedForPosting = state == FinancePeriods.Locked };
    }
}
