import type { ContractParticipation, RecoveryRow } from './api';
import { allocateLargestRemainder, fromMinor, pctToMicro, toMinor } from './money';

export interface Amount {
  amount: string;
  currency: string;
}

export interface LayerYearTotal {
  layerNo: number;
  incurred: bigint;
  paid: bigint;
  /** Layer-year outstanding = Σ incurred − Σ paid (D-SL4-21); never a per-claim figure. */
  outstanding: bigint;
}

export interface ParticipantYearTotal {
  layerNo: number;
  participantId: string;
  incurred: bigint;
  paid: bigint;
  /** The layer outstanding allocated by largest remainder (D-SL4-22), not incurred − paid. */
  outstanding: bigint;
}

export interface RecoverableTotals {
  layers: LayerYearTotal[];
  participants: ParticipantYearTotal[];
}

/**
 * Layer-year and participant totals from the listByContract rows. The rows also carry a per-row outstanding; it is
 * deliberately ignored: outstanding is derived once per layer-year (D-SL4-21) and allocated to the participants by
 * largest remainder on their signed lines (D-SL4-22), so no share is negative and the shares sum to the layer
 * figure exactly.
 */
export function recoverableTotals(
  rows: readonly RecoveryRow[],
  participations: readonly ContractParticipation[],
): RecoverableTotals {
  const layers = new Map<number, LayerYearTotal>();
  const byParticipant = new Map<
    string,
    { layerNo: number; participantId: string; incurred: bigint; paid: bigint }
  >();
  for (const row of rows) {
    const layerNo = row.layerNo ?? 0;
    const incurred = toMinor(row.recoverableIncurred.amount);
    const paid = toMinor(row.recoverablePaid.amount);
    const layer = layers.get(layerNo) ?? { layerNo, incurred: 0n, paid: 0n, outstanding: 0n };
    layer.incurred += incurred;
    layer.paid += paid;
    layers.set(layerNo, layer);
    if (row.participantId) {
      const key = `${String(layerNo)}|${row.participantId}`;
      const entry = byParticipant.get(key) ?? {
        layerNo,
        participantId: row.participantId,
        incurred: 0n,
        paid: 0n,
      };
      entry.incurred += incurred;
      entry.paid += paid;
      byParticipant.set(key, entry);
    }
  }
  const layerList = [...layers.values()].sort((a, b) => a.layerNo - b.layerNo);
  for (const layer of layerList) {
    const raw = layer.incurred - layer.paid;
    if (raw < 0n) throw new RangeError('Layer-year paid exceeds incurred');
    layer.outstanding = raw;
  }

  const participants: ParticipantYearTotal[] = [];
  for (const layer of layerList) {
    const entries = [...byParticipant.values()]
      .filter((entry) => entry.layerNo === layer.layerNo)
      .sort((a, b) => a.participantId.localeCompare(b.participantId));
    const weights = entries.map((entry) =>
      pctToMicro(
        participations.find((p) => p.reinsurerPartyId === entry.participantId)?.signedLinePct ??
          '0',
      ),
    );
    const shares = allocateLargestRemainder(layer.outstanding, weights);
    entries.forEach((entry, index) => {
      participants.push({ ...entry, outstanding: shares[index] ?? 0n });
    });
  }
  return { layers: layerList, participants };
}

export function money(minor: bigint, currency: string): Amount {
  return { amount: fromMinor(minor), currency };
}
