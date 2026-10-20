using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Modules.Reinsurance.Domain.Recovery;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Recovery;

/// <summary>
/// Recalculates one contract year and books the non-zero deltas (REQ-RI-003, -131, -132; D-SL4-05, D-SL4-19). One batch is in
/// flight per contract year (a transaction-scoped advisory lock); the whole year is loaded, run through the pure
/// <see cref="XolRecoveryEngine"/>, and only the difference to what is already booked is written, as sealed rows. The
/// batch, its trace and one <c>RecoveryCalculated</c> per layer x participant delta commit in the caller's transaction.
/// </summary>
internal sealed class RecoveryBooker(ReinsuranceDbContext db, IClock clock, IEventPublisher events)
{
    public const string ContractTypeCode = "XOL_PER_RISK";

    public const string UnmappedLob = "UNMAPPED";

    private const string CostIndemnity = "INDEMNITY";
    private const string CostAlae = "EXPENSE_ALLOCATED";
    private const string CostInterest = "STATUTORY_INTEREST";

    private static readonly JsonSerializerOptions TraceJson = new(JsonSerializerDefaults.Web);

    /// <summary>Recalculates <paramref name="contractId"/>; returns the number of delta rows booked (0 when nothing changed).</summary>
    public async Task<int> RecalculateAsync(Guid contractId, Guid triggerClaimId, Guid sourceEventId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", "ri:recovery:" + contractId.ToString("D"))], cancellationToken)
            .ConfigureAwait(false);

        var id = new RiContractId(contractId);
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.ContractId == id, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return 0;
        }

        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.ContractId == id && v.KnownTo == null, cancellationToken).ConfigureAwait(false);
        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        var layerRows = await db.Layers.AsNoTracking().Where(l => l.VersionId == version.VersionId && l.Rev == version.ContentRev)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var exposures = await db.ClaimExposures.AsNoTracking().Where(e => e.ContractId == contractId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var claimIds = exposures.Select(e => e.ClaimId).Distinct().ToArray();
        var views = await db.ClaimViews.AsNoTracking().Where(v => claimIds.Contains(v.ClaimId)).ToDictionaryAsync(v => v.ClaimId, cancellationToken).ConfigureAwait(false);
        var amounts = await db.ClaimAmounts.AsNoTracking().Where(a => claimIds.Contains(a.ClaimId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        var occurrences = new List<RecoveryOccurrence>();
        var lobByClaim = new Dictionary<Guid, string>();
        foreach (var group in exposures.GroupBy(e => e.ClaimId))
        {
            if (!views.TryGetValue(group.Key, out var view) || view.LossDate is not { } lossDate)
            {
                continue;
            }

            var exposureIds = group.Select(e => e.ExposureId).ToHashSet();
            var lines = amounts.Where(a => a.ClaimId == group.Key && exposureIds.Contains(a.ExposureId)).ToList();
            occurrences.Add(new RecoveryOccurrence(group.Key.ToString("D"), lossDate.Value, ClaimAmountsOf(lines, view.Closed)));
            lobByClaim[group.Key] = group.Select(e => e.SiiLob).FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? UnmappedLob;
        }

        var terms = new ContractTerms(
            [.. layerRows.Select(l => new XolLayer(l.LayerId.ToString("D"), l.Attachment, l.LimitAmount, l.Aad, l.Aal))],
            new UnlClause(content.AlaeIncluded, content.StatutoryInterestIncluded),
            Participations(content),
            content.PlacedPct / 100m, // stored as a percentage (100 = 100%); the engine wants a fraction
            // TODO(hardening): resolve the legal entity's rule via mkt.Rounding.apply (PITFALLS 11); until then the platform money mode at cents.
            amount => DecimalRounding.RoundTo(amount, 2, DecimalRounding.DefaultMoneyMode),
            $"{version.VersionNo}:{version.ContentHash}");

        var booked = (await db.Recoveries.AsNoTracking().Where(r => r.ContractId == contractId)
                .GroupBy(r => new { r.ClaimId, r.LayerId, r.ParticipantPartyId })
                .Select(g => new { g.Key.ClaimId, g.Key.LayerId, g.Key.ParticipantPartyId, Incurred = g.Sum(r => r.IncurredDelta), Paid = g.Sum(r => r.PaidDelta) })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(g => new RecoverableRow(g.ClaimId.ToString("D"), g.LayerId.ToString("D"), g.ParticipantPartyId.ToString("D"), g.Incurred, g.Paid))
            .ToList();

        var outcome = XolRecoveryEngine.Calculate(new RecoveryInput(terms, occurrences, booked));
        if (!outcome.IsSuccess)
        {
            // Fail closed: nothing is booked on an impossible input; the refusal is logged for a person to look at.
            db.IntakeExceptions.Add(new IntakeExceptionRow
            {
                ExceptionId = Guid.CreateVersion7(), LegalEntityId = contract.LegalEntityId.Value, ClaimId = triggerClaimId, ContractId = contractId,
                Code = "ENGINE_REFUSED", Detail = string.Join("; ", outcome.Errors.Select(e => e.Code + ": " + e.Message)),
                SourceEventId = sourceEventId, RecordedAt = clock.Now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var result = outcome.Result!;
        if (result.Deltas.Count == 0)
        {
            return 0;
        }

        var layerById = layerRows.ToDictionary(l => l.LayerId.ToString("D"), l => l);
        var now = clock.Now;
        var batchId = Guid.CreateVersion7();
        db.RecoveryBatches.Add(new RecoveryBatchRow
        {
            BatchId = batchId, LegalEntityId = contract.LegalEntityId.Value, ContractId = contractId, ContractYear = contract.ContractYear, ClaimId = triggerClaimId,
            SourceEventId = sourceEventId, RecordedAt = now, CalcHash = Hash(result.Targets), EngineVersion = XolRecoveryEngine.EngineVersion,
            ContractVersion = terms.ContractVersion, Trace = JsonSerializer.Serialize(result.Traces, TraceJson),
        });

        var rows = new List<RecoveryRow>();
        foreach (var delta in result.Deltas)
        {
            var claimId = Guid.Parse(delta.OccurrenceId);
            rows.Add(new RecoveryRow
            {
                RecoveryId = Guid.CreateVersion7(), BatchId = batchId, LegalEntityId = contract.LegalEntityId.Value, ContractId = contractId, ContractYear = contract.ContractYear,
                ClaimId = claimId, LayerId = Guid.Parse(delta.LayerId), LayerNo = layerById[delta.LayerId].LayerNo, ParticipantPartyId = Guid.Parse(delta.ParticipantId),
                IncurredDelta = delta.Incurred, PaidDelta = delta.Paid, OutstandingDelta = delta.Incurred - delta.Paid,
                SiiLob = lobByClaim.GetValueOrDefault(claimId, UnmappedLob), RecordedAt = now,
            });
        }

        db.Recoveries.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(contract, batchId, rows, sourceEventId);
        return rows.Count;
    }

    private static ClaimAmounts ClaimAmountsOf(List<ClaimAmountRow> lines, bool closed)
    {
        decimal Paid(string cost) => lines.Where(l => l.CostType == cost).Sum(l => l.Paid);
        decimal Open(string cost) => closed ? 0m : lines.Where(l => l.CostType == cost).Sum(l => l.OpenReserve);
        var openTotal = Open(CostIndemnity) + Open(CostAlae) + Open(CostInterest);
        var recoveryReserve = closed ? 0m : Math.Min(lines.Sum(l => l.RecoveryOpen), openTotal);
        return new ClaimAmounts(
            Paid(CostIndemnity), Open(CostIndemnity), Paid(CostAlae), Open(CostAlae), Paid(CostInterest), Open(CostInterest),
            lines.Sum(l => l.Realised), recoveryReserve, closed);
    }

    /// <summary>Signed lines are percentages of the whole market, like the placed percentage; the engine wants fractions of the placed cover that sum to exactly 1 (the lead takes the residue).</summary>
    private static List<Participation> Participations(ContractContent content)
    {
        var placed = content.PlacedPct;
        var follow = content.Lines.Where(l => !l.Lead).Select(l => (l.ReinsurerPartyId, Share: decimal.Round(l.SignedLinePct / placed, 12, MidpointRounding.ToZero))).ToList();
        var lead = content.Lines.Single(l => l.Lead);
        var list = follow.Select(f => new Participation(f.ReinsurerPartyId.ToString("D"), f.Share, false)).ToList();
        list.Add(new Participation(lead.ReinsurerPartyId.ToString("D"), 1m - follow.Sum(f => f.Share), true));
        return list;
    }

    private static string Hash(IReadOnlyList<RecoverableRow> targets)
    {
        var text = string.Join('\n', targets.Select(t => string.Join('|', t.OccurrenceId, t.LayerId, t.ParticipantId,
            t.Incurred.ToString(CultureInfo.InvariantCulture), t.Paid.ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void Publish(ContractRow contract, Guid batchId, List<RecoveryRow> rows, Guid sourceEventId)
    {
        var eur = Currency.FromCode("EUR");
        MoneyByCurrency4 Four(decimal amount) => new()
        {
            Transaction = new Money(amount, eur), Functional = new Money(amount, eur), Group = new Money(amount, eur), Contract = new Money(amount, eur),
        };

        // Claim-level totals: what this batch moved per claim (REQ-RI-003), shared by every delta event of the batch.
        var totals = rows.GroupBy(r => r.ClaimId).OrderBy(g => g.Key).Select(g => new ClaimLevelTotal
        {
            ClaimId = new ClaimId(g.Key),
            Totals = new Dictionary<string, MoneyByCurrency4>(StringComparer.Ordinal)
            {
                ["incurred"] = Four(g.Sum(r => r.IncurredDelta)),
                ["paid"] = Four(g.Sum(r => r.PaidDelta)),
                ["outstanding"] = Four(g.Sum(r => r.OutstandingDelta)),
            },
        }).ToList();

        foreach (var row in rows)
        {
            var payload = new RecoveryCalculatedV1
            {
                BatchId = batchId,
                RecoveryIds = [row.RecoveryId],
                Occurrence = row.ClaimId.ToString("D"),
                ClaimIds = [new ClaimId(row.ClaimId)],
                RiContractId = new RiContractId(row.ContractId),
                LayerId = row.LayerNo.ToString(CultureInfo.InvariantCulture),
                ParticipantPartyId = new PartyId(row.ParticipantPartyId),
                IncurredDelta = Four(row.IncurredDelta),
                PaidDelta = Four(row.PaidDelta),
                OutstandingDelta = Four(row.OutstandingDelta),
                ClaimLevelTotals = totals,
                PostingKey = $"RECOVERY.{ContractTypeCode}.CEDED.L{row.LayerNo}.REINSURER",
                SiiLob = row.SiiLob,
                Ifrs17Refs = [],
                ContractYear = contract.ContractYear,
                ContractType = ContractTypeCode,
                Posting = new RecoveryCalculatedV1.PostingDetail
                {
                    Item = RecoveryCalculatedV1.PostingDetail.ItemValue.Recovery,
                    ContractType = ContractTypeCode,
                    Direction = RecoveryCalculatedV1.PostingDetail.DirectionValue.Ceded,
                    Line = "L" + row.LayerNo.ToString(CultureInfo.InvariantCulture),
                    ParticipantType = "REINSURER",
                },
                Ifrs17GroupRef = "UNASSIGNED",
                SourceCorrelationKey = row.ClaimId.ToString("D") + ":" + sourceEventId.ToString("D"),
            };
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RecoveryCalculatedV1.Descriptor),
                ContractLifecycleService.AggregateType,
                row.ContractId.ToString("D"),
                payload,
                BusinessKeys.Empty
                    .With("riContractId", row.ContractId.ToString("D"))
                    .With("batchId", batchId.ToString("D"))
                    .With("participantPartyId", row.ParticipantPartyId.ToString("D"))));
        }
    }
}
