namespace CoreIns.Modules.Reinsurance.Domain.Recovery;

/// <summary>
/// Pure excess-of-loss recovery engine (D-SL4-05; REQ-RI-116/117/119/122/123/125/126/127/129/132). No I/O, no clock, no
/// randomness: the same inputs always give the same targets, so a rerun books no delta. The whole contract year is
/// recomputed on every call, ordered by occurrence date then id (BR-RI-007).
/// </summary>
internal static class XolRecoveryEngine
{
    public const string EngineVersion = "xol-recovery/1.0.0";

    public static RecoveryOutcome Calculate(RecoveryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = Validate(input);
        if (errors.Count > 0)
        {
            return new RecoveryOutcome(null, errors);
        }

        var terms = input.Terms;
        var layers = terms.Layers.OrderBy(l => l.Attachment).ThenBy(l => l.LayerId, StringComparer.Ordinal).ToList();
        var occurrences = input.Occurrences.OrderBy(o => o.OccurrenceDate).ThenBy(o => o.OccurrenceId, StringComparer.Ordinal).ToList();
        var participants = terms.Participations.OrderBy(p => p.ParticipantId, StringComparer.Ordinal).ToList();

        var unl = occurrences.ToDictionary(
            o => o.OccurrenceId,
            o => (Incurred: UnlOf(o.Claim, terms.Clause, incurred: true), Paid: UnlOf(o.Claim, terms.Clause, incurred: false)),
            StringComparer.Ordinal);
        var targets = new List<RecoverableRow>();
        var traces = new List<LayerTrace>();

        foreach (var layer in layers)
        {
            decimal cumInc = 0m;
            decimal cumPaid = 0m;
            foreach (var occ in occurrences)
            {
                var u = unl[occ.OccurrenceId];
                var lossInc = LayerLoss(u.Incurred.Unl, layer);
                var lossPaid = LayerLoss(u.Paid.Unl, layer);
                var incPos = Step(ref cumInc, lossInc, layer);
                var paidPos = Step(ref cumPaid, lossPaid, layer);
                var recInc = incPos.RecoveryAfter - incPos.RecoveryBefore;
                var recPaid = paidPos.RecoveryAfter - paidPos.RecoveryBefore;

                var placedInc = terms.Round(recInc * terms.PlacedPct);
                var placedPaid = terms.Round(recPaid * terms.PlacedPct);
                var splitInc = Split(placedInc, participants, terms.Round);
                var splitPaid = Split(placedPaid, participants, terms.Round);
                foreach (var p in participants)
                {
                    var i = splitInc[p.ParticipantId];
                    var pd = splitPaid[p.ParticipantId];
                    targets.Add(new RecoverableRow(occ.OccurrenceId, layer.LayerId, p.ParticipantId, i, pd, i - pd));
                }

                traces.Add(new LayerTrace(
                    occ.OccurrenceId, occ.OccurrenceDate, layer.LayerId, occ.Claim, u.Incurred, u.Paid,
                    layer.Attachment, layer.Limit, layer.Aad, layer.Aal, lossInc, lossPaid, incPos, paidPos,
                    recInc, recPaid, recInc - recPaid, terms.PlacedPct, placedInc, placedPaid, EngineVersion));
            }
        }

        return new RecoveryOutcome(new RecoveryResult(targets, Deltas(targets, input.Booked), traces), []);
    }

    /// <summary>UNL on one basis (REQ-RI-116/117). Incurred adds open reserves; paid does not. Floored at 0.</summary>
    internal static UnlTrace UnlOf(ClaimAmounts c, UnlClause clause, bool incurred)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(clause);
        var indemnity = c.IndemnityPaid + (incurred ? c.IndemnityOpen : 0m);
        var alae = clause.IncludeAlae ? c.AlaePaid + (incurred ? c.AlaeOpen : 0m) : 0m;
        var interest = clause.IncludeStatutoryInterest ? c.InterestPaid + (incurred ? c.InterestOpen : 0m) : 0m;
        var anticipated = incurred && clause.AnticipatedRecoveriesInure && !c.Closed ? c.OpenRecoveryReserve : 0m;
        var before = indemnity + alae + interest - c.RealisedRecoveries - anticipated;
        return new UnlTrace(indemnity, alae, interest, c.RealisedRecoveries, anticipated, before, Math.Max(before, 0m));
    }

    private static decimal LayerLoss(decimal unl, XolLayer layer)
    {
        var loss = Math.Max(unl - layer.Attachment, 0m);
        return layer.Limit is { } limit ? Math.Min(loss, limit) : loss;
    }

    private static decimal Aggregate(decimal cumulative, XolLayer layer)
    {
        var r = Math.Max(cumulative - layer.Aad, 0m);
        return layer.Aal is { } aal ? Math.Min(r, aal) : r;
    }

    private static AggregatePosition Step(ref decimal cumulative, decimal loss, XolLayer layer)
    {
        var before = cumulative;
        cumulative += loss;
        return new AggregatePosition(before, cumulative, Aggregate(before, layer), Aggregate(cumulative, layer));
    }

    /// <summary>Sum of participants = total exactly: each non-lead rounds by the MKT rule, the lead takes the residual.</summary>
    private static Dictionary<string, decimal> Split(decimal total, List<Participation> participants, Func<decimal, decimal> round)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal assigned = 0m;
        foreach (var p in participants.Where(p => !p.IsLead))
        {
            var share = round(total * p.SignedLine);
            result[p.ParticipantId] = share;
            assigned += share;
        }

        var lead = participants.Single(p => p.IsLead);
        result[lead.ParticipantId] = total - assigned;
        return result;
    }

    private static List<RecoverableRow> Deltas(List<RecoverableRow> targets, IReadOnlyList<RecoverableRow> booked)
    {
        var t = targets.ToDictionary(r => (r.OccurrenceId, r.LayerId, r.ParticipantId));
        var b = booked.ToDictionary(r => (r.OccurrenceId, r.LayerId, r.ParticipantId));
        var deltas = new List<RecoverableRow>();
        var keys = t.Keys.Union(b.Keys)
            .OrderBy(k => k.OccurrenceId, StringComparer.Ordinal)
            .ThenBy(k => k.LayerId, StringComparer.Ordinal)
            .ThenBy(k => k.ParticipantId, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            t.TryGetValue(key, out var tr);
            b.TryGetValue(key, out var br);
            var inc = (tr?.Incurred ?? 0m) - (br?.Incurred ?? 0m);
            var paid = (tr?.Paid ?? 0m) - (br?.Paid ?? 0m);
            var outstanding = (tr?.Outstanding ?? 0m) - (br?.Outstanding ?? 0m);
            if (inc != 0m || paid != 0m || outstanding != 0m)
            {
                deltas.Add(new RecoverableRow(key.OccurrenceId, key.LayerId, key.ParticipantId, inc, paid, outstanding));
            }
        }

        return deltas;
    }

    private static List<RecoveryError> Validate(RecoveryInput input)
    {
        var errors = new List<RecoveryError>();
        void Err(bool condition, string code, string message)
        {
            if (condition)
            {
                errors.Add(new RecoveryError(code, message));
            }
        }

        ContractTerms? t = input.Terms;
        IReadOnlyList<RecoveryOccurrence>? occurrences = input.Occurrences;
        IReadOnlyList<RecoverableRow>? bookedRows = input.Booked;
        if (t is null || occurrences is null || bookedRows is null)
        {
            return [new RecoveryError("INPUT_MISSING", "Terms, occurrences and booked rows are required.")];
        }

        IReadOnlyList<XolLayer>? layerList = t.Layers;
        IReadOnlyList<Participation>? parts = t.Participations;
        Func<decimal, decimal>? round = t.Round;
        UnlClause? clause = t.Clause;

        Err(round is null, "ROUNDING_MISSING", "The MKT rounding rule is required.");
        Err(clause is null, "CLAUSE_MISSING", "The UNL clause is required.");
        Err(layerList is null || layerList.Count == 0, "LAYERS_MISSING", "At least one layer is required.");
        Err(parts is null || parts.Count == 0, "PARTICIPANTS_MISSING", "At least one participant is required.");
        if (errors.Count > 0 || layerList is null || parts is null)
        {
            return errors;
        }

        Err(t.PlacedPct is <= 0m or > 1m, "PLACED_PCT_INVALID", "Placed percentage must be in (0, 1].");

        foreach (var l in layerList)
        {
            Err(string.IsNullOrWhiteSpace(l.LayerId), "LAYER_ID_MISSING", "Layer id is required.");
            Err(l.Attachment < 0m, "ATTACHMENT_NEGATIVE", $"Layer {l.LayerId}: attachment is negative.");
            Err(l.Limit is < 0m, "LIMIT_NEGATIVE", $"Layer {l.LayerId}: limit is negative.");
            Err(l.Aad < 0m, "AAD_NEGATIVE", $"Layer {l.LayerId}: aggregate deductible is negative.");
            Err(l.Aal is < 0m, "AAL_NEGATIVE", $"Layer {l.LayerId}: aggregate limit is negative.");
        }

        Err(layerList.Select(l => l.LayerId).Distinct(StringComparer.Ordinal).Count() != layerList.Count, "LAYER_DUPLICATE", "Layer ids must be unique.");

        Err(parts.Select(p => p.ParticipantId).Distinct(StringComparer.Ordinal).Count() != parts.Count, "PARTICIPANT_DUPLICATE", "Participant ids must be unique.");
        Err(parts.Count(p => p.IsLead) != 1, "LEAD_INVALID", "Exactly one participant must be the lead.");
        Err(parts.Any(p => p.SignedLine is <= 0m or > 1m), "LINE_INVALID", "Signed lines must be in (0, 1].");
        Err(parts.Sum(p => p.SignedLine) != 1m, "LINES_NOT_100", "Signed lines must sum to 100%.");

        foreach (var o in occurrences)
        {
            if (string.IsNullOrWhiteSpace(o.OccurrenceId) || o.Claim is null)
            {
                errors.Add(new("OCCURRENCE_INVALID", "Occurrence id and claim amounts are required."));
                continue;
            }

            var c = o.Claim;
            decimal[] all = [c.IndemnityPaid, c.IndemnityOpen, c.AlaePaid, c.AlaeOpen, c.InterestPaid, c.InterestOpen, c.RealisedRecoveries, c.OpenRecoveryReserve];
            Err(all.Any(a => a < 0m), "AMOUNT_NEGATIVE", $"Occurrence {o.OccurrenceId}: amounts must not be negative.");
            if (c.Closed && (c.IndemnityOpen != 0m || c.AlaeOpen != 0m || c.InterestOpen != 0m))
            {
                errors.Add(new("CLOSED_WITH_OPEN_RESERVE", $"Occurrence {o.OccurrenceId}: a closed claim has an open reserve."));
            }
        }

        Err(occurrences.Select(o => o.OccurrenceId).Distinct(StringComparer.Ordinal).Count() != occurrences.Count, "OCCURRENCE_DUPLICATE", "Occurrence ids must be unique.");

        var layerIds = layerList.Select(l => l.LayerId).ToHashSet(StringComparer.Ordinal);
        var partIds = parts.Select(p => p.ParticipantId).ToHashSet(StringComparer.Ordinal);
        foreach (var b in bookedRows.Where(b => !layerIds.Contains(b.LayerId) || !partIds.Contains(b.ParticipantId)))
        {
            errors.Add(new("BOOKED_UNKNOWN_KEY", $"Booked row {b.OccurrenceId}/{b.LayerId}/{b.ParticipantId} references an unknown layer or participant."));
        }

        Err(bookedRows.GroupBy(b => (b.OccurrenceId, b.LayerId, b.ParticipantId)).Any(g => g.Count() > 1), "BOOKED_DUPLICATE", "Booked rows must be unique per occurrence, layer and participant.");
        return errors;
    }
}
