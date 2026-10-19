import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import type { FsStatementView } from './api';
import { monthPeriod } from './api';
import { FsStatementsPage } from './FsStatementsPage';

vi.setConfig({ testTimeout: 60_000 });
afterEach(() => {
  vi.unstubAllGlobals();
});

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const id = 'a5000000-1111-4222-8333-000000000001';

function statement(over: Partial<FsStatementView> = {}): FsStatementView {
  return {
    fsStatementId: id,
    period: { from: '2026-10-01', to: '2026-11-01' },
    reconciliation: 'EXCEPTIONS_OPEN',
    exceptionCount: 2,
    importedAt: '2026-11-02T08:00:00Z',
    recordVersion: 3,
    lines: [
      {
        lineRef: 'L1',
        direction: 'RECEIVABLE',
        amount: eur('1000.00'),
        counterpartyCode: 'INS-B',
        matchStatus: 'MATCHED',
      },
      {
        lineRef: 'L2',
        direction: 'PAYABLE',
        amount: eur('300.00'),
        counterpartyCode: 'INS-C',
        matchStatus: 'UNMATCHED',
      },
      {
        lineRef: 'L3',
        direction: 'PAYABLE',
        amount: eur('250.00'),
        counterpartyCode: 'INS-C',
        matchStatus: 'DIFFERENCE',
        difference: eur('50.00'),
      },
    ],
    nets: [
      {
        counterpartyCode: 'INS-B',
        direction: 'RECEIVABLE',
        amount: eur('1000.00'),
        status: 'PENDING',
        statementRef: 'S-1',
      },
      {
        counterpartyCode: 'INS-C',
        direction: 'PAYABLE',
        amount: eur('550.00'),
        status: 'PENDING',
        statementRef: 'S-1',
      },
    ],
    ...over,
  };
}

function at<T>(items: T[], index: number): T {
  const item = items[index];
  if (item === undefined) throw new Error('missing item');
  return item;
}

const view = () =>
  renderScreen(<FsStatementsPage />, {
    path: '/claims/fs-statements',
    url: `/claims/fs-statements?statement=${id}`,
  });
const slow = { timeout: 20_000 };

describe('fs statements screen', () => {
  it('builds the half-open month period', () => {
    expect(monthPeriod('2026-12')).toEqual({ from: '2026-12-01', to: '2027-01-01' });
    expect(monthPeriod('2026-13')).toBeNull();
  });

  it('shows lines, exception reasons and nets; only the clean counterparty can be approved', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/clm/v1/friendly-settlements/statements/${id}`,
        respond: () => ({ body: { statement: statement() } }),
      },
    ]);
    view();
    expect(await screen.findByText('L2', {}, slow)).toBeTruthy();
    expect(screen.getAllByText('Δεν υπάρχει αντίστοιχη υπόθεση').length).toBeGreaterThan(0);
    expect(screen.getByText(/Διαφορά ποσού/)).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Έγκριση καθαρού' })).toHaveLength(1);
    expect(screen.getByText(/Επιλύστε τις εξαιρέσεις/)).toBeTruthy();
  });

  it('approves the net sending the expected net and shows the billing hand-over', async () => {
    const matched = statement({
      reconciliation: 'MATCHED',
      exceptionCount: 0,
      lines: statement().lines.slice(0, 1),
      nets: [at(statement().nets, 0)],
    });
    const done = {
      ...matched,
      nets: [
        { ...at(matched.nets, 0), status: 'REQUESTED' as const, bilReferenceId: 'b1b1b1b1-0000' },
      ],
    };
    const api = mockApi([
      {
        method: 'GET',
        path: `/api/clm/v1/friendly-settlements/statements/${id}`,
        respond: () => ({ body: { statement: matched } }),
      },
      {
        method: 'POST',
        path: '/api/clm/v1/friendly-settlements/approve-net',
        respond: () => ({ body: { statement: done, net: at(done.nets, 0), outcome: 'REQUESTED' } }),
      },
    ]);
    const { user } = view();
    await user.click(await screen.findByRole('button', { name: 'Έγκριση καθαρού' }, slow));
    expect(await screen.findByTestId('fs-bil-INS-B', {}, slow)).toBeTruthy();
    const body = at(api.callsTo('POST', '/api/clm/v1/friendly-settlements/approve-net'), 0)
      .body as Record<string, unknown>;
    expect(body.expectedNet).toEqual(eur('1000.00'));
    expect(body.counterpartyCode).toBe('INS-B');
    expect(body.expectedRecordVersion).toBe(3);
  });

  it('shows the stale message when the net changed', async () => {
    const matched = statement({
      reconciliation: 'MATCHED',
      exceptionCount: 0,
      lines: statement().lines.slice(0, 1),
      nets: [at(statement().nets, 0)],
    });
    mockApi([
      {
        method: 'GET',
        path: `/api/clm/v1/friendly-settlements/statements/${id}`,
        respond: () => ({ body: { statement: matched } }),
      },
      {
        method: 'POST',
        path: '/api/clm/v1/friendly-settlements/approve-net',
        respond: () => problem(409, 'CLM-ERR-STALE', 'Stale'),
      },
    ]);
    const { user } = view();
    await user.click(await screen.findByRole('button', { name: 'Έγκριση καθαρού' }, slow));
    expect(await screen.findByText(/Το καθαρό άλλαξε/, {}, slow)).toBeTruthy();
  });
});
