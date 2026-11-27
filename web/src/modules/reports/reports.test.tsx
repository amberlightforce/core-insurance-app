import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { mockApi, renderScreen } from '../../test/mockApi';
import { ReportsPage } from './ReportsPage';
import { groupByStatus, sumByCurrency } from './summarise';

vi.setConfig({ testTimeout: 30_000 });

beforeEach(async () => {
  await i18n.changeLanguage('en');
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('summarise', () => {
  it('sums decimals exactly per currency', () => {
    expect(
      sumByCurrency([
        { amount: '0.10', currency: 'EUR' },
        { amount: '0.20', currency: 'EUR' },
        { amount: '5.00', currency: 'USD' },
      ]),
    ).toEqual({ EUR: '0.30', USD: '5.00' });
  });

  it('groups by status', () => {
    const rows = groupByStatus(
      [
        { s: 'A', m: { amount: '1.00', currency: 'EUR' } },
        { s: 'A', m: { amount: '2.00', currency: 'EUR' } },
        { s: 'B', m: { amount: '3.00', currency: 'EUR' } },
      ],
      (x) => x.s,
      (x) => x.m,
    );
    expect(rows.map((r) => [r.status, r.count])).toEqual([
      ['A', 2],
      ['B', 1],
    ]);
  });
});

const receivable = (status: string, open: string) => ({
  receivableId: crypto.randomUUID(),
  sourceType: 'CLM_CLAIM_PAYMENT',
  sourceId: 's',
  counterpartyPartyId: crypto.randomUUID(),
  purpose: 'RECOVERY',
  amount: { amount: open, currency: 'EUR' },
  openAmount: { amount: open, currency: 'EUR' },
  status,
  dueDate: '2026-11-01',
  billingAccountId: crypto.randomUUID(),
  paymentReference: 'REF',
  registeredAt: '2026-10-01T00:00:00Z',
  recordVersion: 1,
});

describe('ReportsPage', () => {
  it('summarises receivables and delinquency from the live reads', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/receivables',
        respond: () => ({
          body: { items: [receivable('OPEN', '100.00'), receivable('OPEN', '50.50')] },
        }),
      },
      {
        method: 'GET',
        path: '/api/bil/v1/delinquencies',
        respond: () => ({ body: { items: [] } }),
      },
    ]);
    renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    expect(await screen.findByRole('heading', { level: 1, name: 'Reports' })).toBeInTheDocument();
    expect(await screen.findByText(/150[.,]50/)).toBeInTheDocument();
    expect(screen.getByText(/not available through the API yet/)).toBeInTheDocument();
  });
});
