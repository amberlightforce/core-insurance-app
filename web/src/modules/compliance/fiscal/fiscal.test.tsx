import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, renderScreen } from '../../../test/mockApi';
import { FiscalDocumentPage } from './FiscalDocumentPage';
import { FiscalListPage } from './FiscalListPage';

vi.setConfig({ testTimeout: 60_000 });
afterEach(() => {
  vi.unstubAllGlobals();
});

const doc = (status: string, number: string, rejectionCodes?: string[]) => ({
  document: {
    fiscalDocumentId: '11111111-1111-4111-8111-111111111111',
    status,
    sourceType: 'INVOICE',
    sourceId: 'INV-1',
    role: 'ISSUE',
    revision: 1,
    documentType: 'UNMAPPED-OQ-012',
    documentTypeIsPlaceholder: true,
    issueDate: '2026-10-07',
    counterpartyPartyId: '22222222-2222-4222-8222-222222222222',
    total: { amount: '124.00', currency: 'EUR' },
    lines: [{ fiscalCategoryKey: 'PREMIUM', amount: { amount: '124.00', currency: 'EUR' } }],
    ...(rejectionCodes ? { rejectionCodes } : {}),
  },
  identifiers: { series: 'A', number, mark: `MARK-${number}`, channel: 'MYDATA-STUB', stub: true },
});

describe('Fiscal documents', () => {
  it('lists documents and summarises the open breaks from the Host reads', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-documents/list-by-source',
        respond: () => ({
          body: { items: [{ documents: [doc('REGISTERED', '101')] }], nextCursor: null },
        }),
      },
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-reconciliation/breaks',
        respond: () => ({
          body: { items: [{ breaks: doc('REJECTED', '102', ['ERR-77']) }], nextCursor: null },
        }),
      },
    ]);
    renderScreen(<FiscalListPage />, { path: '/compliance/fiscal', url: '/compliance/fiscal' });
    expect(await screen.findByText('A-101')).toBeInTheDocument();
    expect(await screen.findByText('1 ανοικτή διαφορά')).toBeInTheDocument();
  });

  it('shows one document with its identifiers and lines', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-documents/11111111-1111-4111-8111-111111111111',
        respond: () => ({ body: doc('REGISTERED', '101') }),
      },
    ]);
    renderScreen(<FiscalDocumentPage />, {
      path: '/compliance/fiscal/:id',
      url: '/compliance/fiscal/11111111-1111-4111-8111-111111111111',
    });
    expect(await screen.findByText('MARK-101')).toBeInTheDocument();
    expect(screen.getByText('PREMIUM')).toBeInTheDocument();
  });
});
