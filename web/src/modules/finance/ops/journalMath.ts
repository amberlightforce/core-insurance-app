import { readSession } from '../../../dev-auth/devAuth';
import { fromMinor, toMinor } from '../../../format';
import type { ManualJournalLine } from './types';

/** Debit and credit totals in minor units (exact, no float) and whether they balance. */
export function lineTotals(lines: readonly Pick<ManualJournalLine, 'side' | 'amount'>[]) {
  let debit = 0n;
  let credit = 0n;
  for (const line of lines) {
    const minor = /^\d+(\.\d{1,2})?$/.test(line.amount) ? toMinor(line.amount) : 0n;
    if (line.side === 'DEBIT') debit += minor;
    else credit += minor;
  }
  return {
    debit: fromMinor(debit),
    credit: fromMinor(credit),
    balanced: debit === credit && debit > 0n,
  };
}

/** The signed-in user's id; the server decides, this only hides controls the user cannot use (four-eyes). */
export function currentUserId(): string | undefined {
  return readSession()?.user.id;
}

/** Whether the signed-in user made this journal (so a second user must decide it). */
export function isMaker(makerId: string | undefined): boolean {
  const me = currentUserId();
  return me !== undefined && makerId !== undefined && me === makerId;
}
