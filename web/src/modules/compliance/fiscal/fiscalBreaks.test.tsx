import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, renderScreen } from '../../../test/mockApi';
import { FiscalBreaksPage } from './FiscalBreaksPage';

vi.setConfig({ testTimeout: 60_000 });
afterEach(() => {
  vi.unstubAllGlobals();
});

const docId = '11111111-1111-4111-8111-111111111111';

const brk = {
  breakId: '33333333-3333-4333-8333-333333333333',
  kind: 'REJECTED_BY_CHANNEL',
  detail: 'Rejected by the channel: ERR-77.',
  status: 'OPEN',
  detectedAt: '2026-11-28T08:00:00Z',
  lastSeenAt: '2026-11-28T09:00:00Z',
  document: {
    fiscalDocumentId: docId,
    status: 'REJECTED',
    sourceType: 'INVOICE',
    sourceId: 'INV-1',
    role: 'ISSUE',
    revision: 1,
    issueDate: '2026-10-07',
    total: { amount: '124.00', currency: 'EUR' },
    rejectionCodes: ['ERR-77'],
  },
  identifiers: { series: 'A', number: '102', channel: 'MYDATA-STUB', stub: true },
};

describe('Fiscal breaks page', () => {
  it('lists open breaks and runs the reconciliation with an idempotency key', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-reconciliation/breaks',
        respond: () => ({ body: { items: [{ breaks: brk }], nextCursor: null } }),
      },
      {
        method: 'POST',
        path: '/api/cmp/v1/fiscal-reconciliation/run',
        respond: () => ({
          body: { checked: 12, opened: 1, reopened: 0, resolved: 2, open: 1, breaks: [] },
        }),
      },
    ]);
    const { user } = renderScreen(<FiscalBreaksPage />, {
      path: '/compliance/fiscal/breaks',
      url: '/compliance/fiscal/breaks',
    });
    expect(await screen.findByText('A-102')).toBeInTheDocument();
    expect(screen.getByText('Rejected by the channel: ERR-77.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Εκτέλεση συμφωνίας' }));
    expect(await screen.findByText('Παραστατικά που ελέγχθηκαν')).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/cmp/v1/fiscal-reconciliation/run')).toHaveLength(1);
  });

  it('refuses a malformed run date without calling the server', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-reconciliation/breaks',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    const { user } = renderScreen(<FiscalBreaksPage />, {
      path: '/compliance/fiscal/breaks',
      url: '/compliance/fiscal/breaks',
    });
    await user.type(await screen.findByLabelText(/Έως ημερομηνία/), '28/11/2026');
    await user.click(screen.getByRole('button', { name: 'Εκτέλεση συμφωνίας' }));
    expect(await screen.findByText(/Εισάγετε ημερομηνία/)).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/cmp/v1/fiscal-reconciliation/run')).toHaveLength(0);
  });
});
