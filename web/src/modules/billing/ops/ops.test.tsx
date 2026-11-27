import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { mockApi, renderScreen } from '../../../test/mockApi';
import { AccountCurrentsPage } from './AccountCurrentsPage';
import { MandatesPage } from './MandatesPage';
import { PlansPage } from './PlansPage';
import { SuspensePage } from './SuspensePage';

vi.setConfig({ testTimeout: 60_000 });

const account = 'acc00000-1111-4222-8333-000000000001';
const receiptId = 'r0000000-1111-4222-8333-000000000001';
const invoiceId = 'i0000000-1111-4222-8333-000000000001';

beforeEach(() => {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'billing', name: 'billing', roles: ['Staff.Billing'] },
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const item = {
  suspenseItemId: 's0000000-1111-4222-8333-000000000001',
  receiptId,
  receiptNumber: 'RC-0001',
  billingAccountId: account,
  state: 'OPEN',
  amount: { amount: '120.00', currency: 'EUR' },
  reason: 'AMOUNT_MISMATCH',
  bankReference: 'BANK-REF-1',
  valueDate: '2026-10-05',
  recordedAt: '2026-10-05T09:00:00Z',
};

describe('suspense workbench', () => {
  it('lists unapplied receipts and allocates whole invoices with an idempotency key', async () => {
    const posts: { body: unknown; key: string | null }[] = [];
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/suspense',
        respond: () => ({ body: { items: [item], nextCursor: null, limit: 100 } }),
      },
      {
        method: 'GET',
        path: '/api/bil/v1/invoices',
        respond: () => ({
          body: {
            items: [
              {
                invoice: {
                  invoiceId,
                  invoiceNumber: 'INV-0001',
                  kind: 'INVOICE',
                  state: 'DUE',
                  billingAccountId: account,
                  issueDate: '2026-10-01',
                  dueDate: '2026-10-15',
                  total: { amount: '120.00', currency: 'EUR' },
                  paid: { amount: '0.00', currency: 'EUR' },
                  open: { amount: '120.00', currency: 'EUR' },
                },
              },
            ],
            nextCursor: null,
          },
        }),
      },
      {
        method: 'POST',
        path: '/api/bil/v1/allocations/allocate',
        respond: (request) => {
          posts.push({ body: request.body, key: request.headers.get('Idempotency-Key') });
          return { body: { allocations: [], receipt: item } };
        },
      },
    ]);
    renderScreen(<SuspensePage />, {
      path: '/billing/suspense/:suspenseItemId?',
      url: '/billing/suspense',
    });
    const grid = await screen.findByRole('grid');
    expect(within(grid).getByText('RC-0001')).toBeInTheDocument();
    expect(within(grid).getByText('120,00 €')).toBeInTheDocument();

    await userEvent.click(within(grid).getByText('RC-0001'));
    const box = await screen.findByRole('checkbox', { name: /INV-0001/ }, { timeout: 20_000 });
    await userEvent.click(box);
    const submit = await screen.findByRole('button', { name: /Κατανομή/ });
    await vi.waitFor(() => {
      expect(submit.textContent).toMatch(/120,00/);
    });
    await userEvent.click(submit);
    await vi.waitFor(() => {
      expect(posts).toHaveLength(1);
    });
    expect(posts[0]?.key).toBeTruthy();
    expect(posts[0]?.body).toEqual({
      receiptId,
      lines: [{ invoiceId, amount: { amount: '120.00', currency: 'EUR' } }],
    });
  });

  it('shows the empty state', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/suspense',
        respond: () => ({ body: { items: [], nextCursor: null, limit: 100 } }),
      },
    ]);
    renderScreen(<SuspensePage />, {
      path: '/billing/suspense/:suspenseItemId?',
      url: '/billing/suspense',
    });
    expect(await screen.findByText('Δεν υπάρχουν μη εφαρμοσμένες εισπράξεις')).toBeInTheDocument();
  });
});

describe('payment plans, mandates, account-currents', () => {
  it('shows plan instances', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/billing-ops/plan-instances',
        respond: () => ({
          body: {
            items: [
              {
                planInstanceId: 'p1',
                termId: 't1',
                billingAccountId: account,
                policyId: 'pol1',
                policyNumber: 'POL-100',
                termNumber: 1,
                productCode: 'MOTOR',
                planCode: 'MONTHLY',
                billMode: 'DIRECT',
                method: 'SEPA',
                termFrom: '2026-10-01',
                termTo: '2027-10-01',
              },
            ],
            nextCursor: null,
            limit: 100,
          },
        }),
      },
    ]);
    renderScreen(<PlansPage />, { path: '/billing/plans', url: '/billing/plans' });
    const grid = await screen.findByRole('grid');
    expect(within(grid).getByText('POL-100')).toBeInTheDocument();
    expect(within(grid).getByText('MONTHLY')).toBeInTheDocument();
  });

  it('shows mandates with a masked IBAN', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/migrated-records/mandates',
        respond: () => ({
          body: {
            items: [
              {
                mandateId: 'm1',
                batchId: 'b1',
                billingAccountId: account,
                mandateReference: 'UMR-77',
                signatureDate: '2025-01-02',
                ibanMasked: 'GR16********0001',
                status: 'ACTIVE',
                origin: 'MIGRATION',
              },
            ],
            nextCursor: null,
            limit: 100,
          },
        }),
      },
    ]);
    renderScreen(<MandatesPage />, { path: '/billing/mandates', url: '/billing/mandates' });
    const grid = await screen.findByRole('grid');
    expect(within(grid).getByText('UMR-77')).toBeInTheDocument();
    expect(within(grid).getByText('GR16********0001')).toBeInTheDocument();
  });

  it('says account-currents are not available', async () => {
    mockApi([]);
    renderScreen(<AccountCurrentsPage />, {
      path: '/billing/account-currents',
      url: '/billing/account-currents',
    });
    expect(await screen.findByText('Δεν είναι ακόμη διαθέσιμο')).toBeInTheDocument();
  });
});
