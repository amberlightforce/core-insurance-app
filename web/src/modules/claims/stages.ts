import type { ClaimPaymentView, ClaimView } from '../../api/types';

export type StageId = 'notice' | 'contact' | 'assessment' | 'offer' | 'payment' | 'closing';
/**
 * - `done`: the claim's data shows the stage happened.
 * - `current`: the first stage not yet done.
 * - `todo`: later stages.
 * - `untracked`: the slice records nothing for this stage (first contact), so it is never shown as done or
 *   pending — the strip says so instead of guessing.
 */
export type StageState = 'done' | 'current' | 'todo' | 'untracked';

export interface Stage {
  id: StageId;
  state: StageState;
}

export interface MoneyFacts {
  /** Total ever reserved (financials totals); `null` while unknown. */
  reserved: number | null;
  payments: readonly ClaimPaymentView[] | null;
}

const paidOut = new Set(['ISSUED', 'CLEARED']);

/**
 * The claim file's stage strip (mockup «Φάκελος ζημίας»: Αναγγελία → Επαφή → Εκτίμηση → Προσφορά → Πληρωμή →
 * Κλείσιμο), derived only from recorded facts: the claim exists (notice), an exposure was opened (assessment),
 * a reserve was set (offer), a payment was issued (payment), the claim is closed (closing).
 */
export function claimStages(claim: ClaimView, money: MoneyFacts): Stage[] {
  const closed = claim.summary.status === 'CLOSED';
  const facts: Record<Exclude<StageId, 'contact'>, boolean> = {
    notice: true,
    assessment: claim.exposures.length > 0,
    offer: (money.reserved ?? 0) > 0,
    payment: (money.payments ?? []).some((p) => paidOut.has(p.status)),
    closing: closed,
  };
  const order: StageId[] = ['notice', 'contact', 'assessment', 'offer', 'payment', 'closing'];
  let currentSet = closed;
  return order.map((id) => {
    if (id === 'contact') return { id, state: 'untracked' };
    // A closed claim may skip stages (denied, withdrawn): what did not happen stays not done, nothing is current.
    if (facts[id]) return { id, state: 'done' };
    if (!currentSet) {
      currentSet = true;
      return { id, state: 'current' };
    }
    return { id, state: 'todo' };
  });
}

/** Cents-safe number from a contract decimal string. */
export const amountOf = (value: { amount: string } | null | undefined): number =>
  value ? Math.round(Number(value.amount) * 100) / 100 : 0;
