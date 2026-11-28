import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { FiscalDocumentPage } from './FiscalDocumentPage';
import { FiscalBreaksPage } from './FiscalBreaksPage';

vi.setConfig({ testTimeout: 60_000 });
afterEach(() => {
  vi.unstubAllGlobals();
});

const docId = '11111111-1111-4111-8111-111111111111';
const docPath = `/api/cmp/v1/fiscal-documents/${docId}`;

const doc = (status: string, number: string, rejectionCodes?: string[]) => ({
  document: {
    fiscalDocumentId: docId,
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

const renderDoc = () =>
  renderScreen(<FiscalDocumentPage />, {
    path: '/compliance/fiscal/:id',
    url: `/compliance/fiscal/${docId}`,
  });

describe('Fiscal document actions (BO-CMP-14)', () => {
  it('loads the QR payload and marks the layout as not verified', async () => {
    mockApi([
      { method: 'GET', path: docPath, respond: () => ({ body: doc('REGISTERED', '101') }) },
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-documents/qr-payload',
        respond: () => ({
          body: {
            status: 'REGISTERED',
            mark: 'MARK-101',
            qrPayload: 'https://example.test/qr?mark=MARK-101',
            stub: true,
            layoutVerified: false,
          },
        }),
      },
    ]);
    const { user } = renderDoc();
    await user.click(await screen.findByRole('button', { name: 'Εμφάνιση δεδομένων QR' }));
    expect(await screen.findByText('Η διάταξη δεν έχει επαληθευτεί')).toBeInTheDocument();
    expect(screen.getByText('https://example.test/qr?mark=MARK-101')).toBeInTheDocument();
    await expectNoA11yViolations();
  });

  it('resubmits a rejected document with a reason', async () => {
    const api = mockApi([
      { method: 'GET', path: docPath, respond: () => ({ body: doc('REJECTED', '103', ['E-1']) }) },
      {
        method: 'POST',
        path: '/api/cmp/v1/fiscal-documents/resubmit',
        respond: () => ({
          body: { fiscalDocumentId: docId, status: 'REGISTERED', resubmitted: true },
        }),
      },
    ]);
    const { user } = renderDoc();
    await user.click(await screen.findByRole('button', { name: 'Επανυποβολή' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/Αιτιολογία/), 'Channel was down');
    await user.click(within(dialog).getByRole('button', { name: 'Επανυποβολή' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/cmp/v1/fiscal-documents/resubmit')).toHaveLength(1);
    });
    const call = api.callsTo('POST', '/api/cmp/v1/fiscal-documents/resubmit')[0];
    expect(call?.body).toEqual({ fiscalDocumentId: docId, reason: 'Channel was down' });
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
  });

  it('shows the server refusal of a cancel and keeps the dialog open', async () => {
    mockApi([
      { method: 'GET', path: docPath, respond: () => ({ body: doc('REGISTERED', '101') }) },
      {
        method: 'POST',
        path: '/api/cmp/v1/fiscal-documents/cancel',
        respond: () => problem(409, 'CMP-ERR-NOT-CANCELLABLE', 'Conflict', 'Not cancellable.'),
      },
    ]);
    const { user } = renderDoc();
    await user.click(await screen.findByRole('button', { name: 'Ακύρωση παραστατικού' }));
    const dialog = await screen.findByRole('alertdialog');
    await user.type(within(dialog).getByLabelText(/Αιτιολογία/), 'Issued in error');
    await user.click(within(dialog).getByRole('button', { name: 'Ακύρωση παραστατικού' }));
    expect(await screen.findByText('CMP-ERR-NOT-CANCELLABLE')).toBeInTheDocument();
    expect(screen.getByRole('alertdialog')).toBeInTheDocument();
  });

  it('bulk-resubmits selected rejected documents and shows per-item results', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-documents/list-by-source',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
      {
        method: 'GET',
        path: '/api/cmp/v1/fiscal-reconciliation/breaks',
        respond: () => ({
          body: { items: [{ breaks: doc('REJECTED', '102', ['ERR-77']) }], nextCursor: null },
        }),
      },
      {
        method: 'POST',
        path: '/api/cmp/v1/fiscal-documents/bulk-resubmit',
        respond: () => ({
          body: {
            requested: 1,
            resubmitted: 0,
            skipped: 1,
            results: [{ fiscalDocumentId: docId, outcome: 'SKIPPED', errorCode: 'NOT-REJECTED' }],
          },
        }),
      },
    ]);
    const { user } = renderScreen(<FiscalBreaksPage />, {
      path: '/compliance/fiscal/breaks',
      url: '/compliance/fiscal/breaks',
    });
    await screen.findByText('A-102');
    await user.click(screen.getByRole('button', { name: 'Επανυποβολή απορριφθέντων' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.type(within(dialog).getByLabelText(/Αιτιολογία/), 'Retry batch');
    await user.click(within(dialog).getByRole('button', { name: /^Επανυποβολή/ }));
    expect(await within(dialog).findByText('NOT-REJECTED')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/cmp/v1/fiscal-documents/bulk-resubmit')[0]?.body).toEqual({
      ids: [docId],
      reason: 'Retry batch',
    });
  });
});
