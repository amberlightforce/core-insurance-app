import { fromMinor, toMinor } from '../../format';

interface LineLike {
  side: 'DEBIT' | 'CREDIT';
  amount: { amount: string; currency: string };
}

export interface JournalTotals {
  debit: string;
  credit: string;
  balanced: boolean;
}

/** Debit and credit totals of a journal in exact minor units; balanced when they are equal. */
export function journalTotals(lines: readonly LineLike[]): JournalTotals {
  let debit = 0n;
  let credit = 0n;
  for (const line of lines) {
    const minor = toMinor(line.amount.amount);
    if (line.side === 'DEBIT') debit += minor;
    else credit += minor;
  }
  return { debit: fromMinor(debit), credit: fromMinor(credit), balanced: debit === credit };
}
