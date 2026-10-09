import { fromMinor, toMinor } from '../../../format/money-input';
import type { ChargeLineModel, TermViewModel, TimelineTransaction } from './api';

/** Money is summed in BigInt minor units, never as floating point. */
export interface MinorMoney {
  minor: bigint;
  currency: string;
}

const safeMinor = (amount: string): bigint => {
  try {
    return toMinor(amount);
  } catch {
    return 0n;
  }
};

/** Back to the contract shape so the single money formatter can show it. */
export function toMoney(value: MinorMoney): { amount: string; currency: string } {
  return { amount: fromMinor(value.minor), currency: value.currency };
}

export type HistoryKind = 'NEW_BUSINESS' | 'CHANGE' | 'CANCELLATION' | 'RENEWAL' | 'OTHER';

/**
 * The history badge of a transaction. A renewal term is bound as NEW_BUSINESS (pol.yaml TransactionKindCode), so
 * the first transaction of term 2 or later reads as a renewal.
 */
export function historyKind(kind: string, termNumber: number, sequence: number): HistoryKind {
  if (kind === 'NEW_BUSINESS' || kind === 'ISSUANCE') {
    return termNumber > 1 && sequence === 1 ? 'RENEWAL' : 'NEW_BUSINESS';
  }
  if (kind === 'RENEWAL') return 'RENEWAL';
  if (kind.startsWith('ENDORSEMENT') || kind === 'CHANGE') return 'CHANGE';
  if (kind === 'CANCELLATION' || kind === 'RETURN_PREMIUM' || kind === 'REFUND') {
    return 'CANCELLATION';
  }
  return 'OTHER';
}

/** Newest first: the higher sequence on top. */
export function orderHistory<T extends { sequence: number }>(rows: readonly T[]): T[] {
  return [...rows].sort((a, b) => b.sequence - a.sequence);
}

/** Unique by id, in term order (1, 2, 3 …). */
export function orderTerms<T extends { termNumber: number; termId: string }>(
  terms: readonly T[],
): T[] {
  const unique = new Map(terms.map((x) => [x.termId, x] as const));
  return [...unique.values()].sort((a, b) => a.termNumber - b.termNumber);
}

export interface TermPremium {
  written: MinorMoney;
  credits: MinorMoney;
  net: MinorMoney;
}

/**
 * The term premium card: written = sum of positive premium changes, credits = sum of negative ones (a negative
 * number), net = written + credits. Reversed transactions are left out of both.
 */
export function termPremium(
  rows: readonly Pick<TimelineTransaction, 'premiumChange' | 'reversed'>[],
  currency: string,
): TermPremium {
  let written = 0n;
  let credits = 0n;
  for (const row of rows) {
    if (row.reversed) continue;
    const minor = safeMinor(row.premiumChange.amount);
    if (minor >= 0n) written += minor;
    else credits += minor;
  }
  return {
    written: { minor: written, currency },
    credits: { minor: credits, currency },
    net: { minor: written + credits, currency },
  };
}

export interface InvoiceTotals {
  billed: MinorMoney;
  paid: MinorMoney;
}

/** Billed and paid of a term's invoices; credit notes count negative. */
export function invoiceTotals(
  invoices: readonly { total: { amount: string }; paid: { amount: string }; kind: string }[],
  currency: string,
): InvoiceTotals {
  let billed = 0n;
  let paid = 0n;
  for (const invoice of invoices) {
    const sign = invoice.kind === 'CREDIT_NOTE' ? -1n : 1n;
    billed += sign * safeMinor(invoice.total.amount);
    paid += sign * safeMinor(invoice.paid.amount);
  }
  return { billed: { minor: billed, currency }, paid: { minor: paid, currency } };
}

/** Charges of one transaction, one row per element × charge type, in a stable order. */
export function chargesOf(
  charges: readonly ChargeLineModel[],
  transactionId: string,
): ChargeLineModel[] {
  const key = (c: ChargeLineModel) => `${c.elementLocator}:${c.coverageCode}`;
  return charges
    .filter((c) => c.transactionId === transactionId)
    .sort((a, b) => key(a).localeCompare(key(b)) || a.chargeType.localeCompare(b.chargeType));
}

export type StripState = 'done' | 'current' | 'todo';

/** Timeline node state of a term: the viewed term is current, earlier terms done, later ones still to come. */
export function stripState(term: TermViewModel, viewed: TermViewModel): StripState {
  if (term.termId === viewed.termId) return 'current';
  return term.termNumber < viewed.termNumber ? 'done' : 'todo';
}

export type ServicingAction = 'change' | 'cancel' | 'renew';
export type ActionReason = 'notInForce' | 'cancellationPending' | 'renewalExists' | 'noTerm';
export type Availability =
  { status: 'hidden' } | { status: 'enabled' } | { status: 'disabled'; reason: ActionReason };

/** Servicing (change / cancel / renew) is an underwriter capability (pol.PolicyChange, Cancellation, Renewal). */
export const servicingRoles: readonly string[] = ['Staff.Underwriter'];

export const hasServicingRole = (roles: readonly string[]): boolean =>
  roles.some((r) => servicingRoles.includes(r));

/**
 * Which actions the bar offers: hidden without the permission; otherwise enabled when the term state allows
 * it, else disabled with the reason. The server enforces the same rules again.
 */
export function actionAvailability(
  action: ServicingAction,
  input: { permitted: boolean; termState: string | undefined; renewalExists: boolean },
): Availability {
  if (!input.permitted) return { status: 'hidden' };
  const state = input.termState;
  if (!state) return { status: 'disabled', reason: 'noTerm' };
  if (state === 'PENDING_CANCELLATION') {
    return { status: 'disabled', reason: 'cancellationPending' };
  }
  if (state !== 'IN_FORCE') return { status: 'disabled', reason: 'notInForce' };
  if (action === 'renew' && input.renewalExists) {
    return { status: 'disabled', reason: 'renewalExists' };
  }
  return { status: 'enabled' };
}
