import { describe, expect, it } from 'vitest';

import {
  actionAvailability,
  hasServicingRole,
  historyKind,
  invoiceTotals,
  orderHistory,
  orderTerms,
  stripState,
  termPremium,
  toMoney,
} from './model';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

describe('policy file model', () => {
  it('sums the term premium in exact minor units, leaving reversed lines out', () => {
    const premium = termPremium(
      [
        { premiumChange: eur('0.10'), reversed: false },
        { premiumChange: eur('0.20'), reversed: false },
        { premiumChange: eur('-0.05'), reversed: false },
        { premiumChange: eur('999.99'), reversed: true },
      ],
      'EUR',
    );
    expect(toMoney(premium.written).amount).toBe('0.30');
    expect(toMoney(premium.credits).amount).toBe('-0.05');
    expect(toMoney(premium.net).amount).toBe('0.25');
  });

  it('counts credit notes negative in billed and paid', () => {
    const totals = invoiceTotals(
      [
        { kind: 'INVOICE', total: eur('100.10'), paid: eur('40.00') },
        { kind: 'CREDIT_NOTE', total: eur('20.05'), paid: eur('0.00') },
      ],
      'EUR',
    );
    expect(toMoney(totals.billed).amount).toBe('80.05');
    expect(toMoney(totals.paid).amount).toBe('40.00');
  });

  it('maps transaction kinds to the four history badges; a later term issuance is a renewal', () => {
    expect(historyKind('NEW_BUSINESS', 1, 1)).toBe('NEW_BUSINESS');
    expect(historyKind('NEW_BUSINESS', 2, 1)).toBe('RENEWAL');
    expect(historyKind('ENDORSEMENT_DEBIT', 1, 2)).toBe('CHANGE');
    expect(historyKind('ENDORSEMENT_CREDIT', 1, 3)).toBe('CHANGE');
    expect(historyKind('CANCELLATION', 1, 4)).toBe('CANCELLATION');
    expect(historyKind('FEE', 1, 5)).toBe('OTHER');
  });

  it('orders history newest first and terms ascending, unique by id', () => {
    expect(
      orderHistory([{ sequence: 1 }, { sequence: 3 }, { sequence: 2 }]).map((x) => x.sequence),
    ).toEqual([3, 2, 1]);
    expect(
      orderTerms([
        { termId: 'b', termNumber: 2 },
        { termId: 'a', termNumber: 1 },
        { termId: 'b', termNumber: 2 },
      ]).map((x) => x.termId),
    ).toEqual(['a', 'b']);
  });

  it('marks earlier terms done, the viewed one current, later ones todo', () => {
    const t = (n: number) => ({ termId: `t${String(n)}`, termNumber: n }) as never;
    expect(stripState(t(1), t(2))).toBe('done');
    expect(stripState(t(2), t(2))).toBe('current');
    expect(stripState(t(3), t(2))).toBe('todo');
  });

  it('decides action availability from permission and term state', () => {
    const base = { permitted: true, termState: 'IN_FORCE', renewalExists: false };
    expect(actionAvailability('change', { ...base, permitted: false })).toEqual({
      status: 'hidden',
    });
    expect(actionAvailability('change', base)).toEqual({ status: 'enabled' });
    expect(actionAvailability('renew', { ...base, renewalExists: true })).toEqual({
      status: 'disabled',
      reason: 'renewalExists',
    });
    expect(actionAvailability('cancel', { ...base, termState: 'EXPIRED' })).toEqual({
      status: 'disabled',
      reason: 'notInForce',
    });
    expect(actionAvailability('cancel', { ...base, termState: 'PENDING_CANCELLATION' })).toEqual({
      status: 'disabled',
      reason: 'cancellationPending',
    });
    expect(actionAvailability('change', { ...base, termState: undefined })).toEqual({
      status: 'disabled',
      reason: 'noTerm',
    });
    expect(hasServicingRole(['Staff.Billing'])).toBe(false);
    expect(hasServicingRole(['Staff.Underwriter'])).toBe(true);
  });
});
