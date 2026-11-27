import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { SuspensePage } from './SuspensePage';

vi.setConfig({ testTimeout: 60_000 });

const account = 'acc00000-1111-4222-8333-000000000001';
const targetId = 'acc00000-1111-4222-8333-000000000002';
const payeeId = 'a0000000-1111-4222-8333-000000000001';

function setRoles(roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'u', name: 'u', roles },
    }),
  );
}

beforeEach(() => {
  setRoles(['Staff.Billing']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const item = {
  suspenseItemId: 's0000000-1111-4222-8333-000000000001',
  receiptId: 'r0000000-1111-4222-8333-000000000001',
  receiptNumber: 'RC-0001',
  billingAccountId: account,
  state: 'OPEN',
  amount: { amount: '120.00', currency: 'EUR' },
  reason: 'AMOUNT_MISMATCH',
  bankReference: 'BANK-REF-1',
  valueDate: '2026-10-05',
  recordedAt: '2026-10-05T09:00:00Z',
};

const accountBody = (id: string, number: string) => ({
  account: {
    billingAccountId: id,
    accountNumber: number,
    payerPartyId: 'f0000000-1111-4222-8333-000000000001',
    currency: 'EUR',
    status: 'ACTIVE',
    createdAt: '2026-01-01T00:00:00Z',
    terms: [],
  },
  balancesByState: {},
});

const detailRoutes = [
  {
    method: 'GET' as const,
    path: '/api/bil/v1/suspense',
    respond: () => ({ body: { items: [item], nextCursor: null, limit: 200 } }),
  },
  {
    method: 'GET' as const,
    path: `/api/bil/v1/billing-accounts/${account}`,
    respond: () => ({ body: accountBody(account, 'BA-0001') }),
  },
  {
    method: 'GET' as const,
    path: `/api/bil/v1/billing-accounts/${targetId}`,
    respond: () => ({ body: accountBody(targetId, 'BA-0002') }),
  },
  {
    method: 'GET' as const,
    path: '/api/bil/v1/invoices',
    respond: () => ({ body: { items: [], nextCursor: null } }),
  },
];

const route = {
  path: '/billing/suspense/:suspenseItemId?',
  url: `/billing/suspense/${item.suspenseItemId}`,
};

describe('suspense detail actions', () => {
  it('shows the receipt facts and ranked suggestions, and has no axe violations', async () => {
    const posts: unknown[] = [];
    mockApi([
      ...detailRoutes,
      {
        method: 'POST',
        path: '/api/bil/v1/suspense/suggest',
        respond: (request) => {
          posts.push(request.body);
          return {
            body: {
              item,
              suggestions: [
                { invoiceNo: 'INV-0001', score: 0.95, exactAmount: true },
                { invoiceNo: 'INV-0002', score: 0.4, exactAmount: false },
              ],
            },
          };
        },
      },
    ]);
    const { container } = renderScreen(<SuspensePage />, route);
    expect((await screen.findAllByText('BANK-REF-1')).length).toBeGreaterThan(0);
    await userEvent.click(await screen.findByRole('button', { name: /Πρότεινε/ }));
    expect(await screen.findByText(/INV-0001/)).toBeInTheDocument();
    expect(screen.getByText(/INV-0002/)).toBeInTheDocument();
    expect(posts).toEqual([{ suspenseItemId: item.suspenseItemId }]);
    await expectNoA11yViolations(container);
  });

  it('transfers to another account after a confirmation, with an idempotency key', async () => {
    const posts: { body: unknown; key: string | null }[] = [];
    mockApi([
      ...detailRoutes,
      {
        method: 'POST',
        path: '/api/bil/v1/suspense/transfer',
        respond: (request) => {
          posts.push({ body: request.body, key: request.headers.get('Idempotency-Key') });
          return { body: { item: { ...item, billingAccountId: targetId } } };
        },
      },
    ]);
    renderScreen(<SuspensePage />, route);
    await userEvent.type(
      await screen.findByLabelText(/Αναγνωριστικό λογαριασμού προορισμού/),
      targetId,
    );
    expect(await screen.findByText('BA-0002')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /^Μεταφορά…/ }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Μεταφορά' }));
    await vi.waitFor(() => {
      expect(posts).toHaveLength(1);
    });
    expect(posts[0]?.key).toBeTruthy();
    expect(posts[0]?.body).toEqual({
      suspenseItemId: item.suspenseItemId,
      targetBillingAccountId: targetId,
    });
  });

  it('shows the state conflict when the item is no longer open', async () => {
    mockApi([
      ...detailRoutes,
      {
        method: 'POST',
        path: '/api/bil/v1/suspense/transfer',
        respond: () => problem(409, 'BIL-ERR-SUSPENSE-STATE', 'Suspense item is not open'),
      },
    ]);
    renderScreen(<SuspensePage />, route);
    await userEvent.type(
      await screen.findByLabelText(/Αναγνωριστικό λογαριασμού προορισμού/),
      targetId,
    );
    await screen.findByText('BA-0002');
    await userEvent.click(screen.getByRole('button', { name: /^Μεταφορά…/ }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Μεταφορά' }));
    expect(await within(dialog).findByText(/BIL-ERR-SUSPENSE-STATE/)).toBeInTheDocument();
  });

  it('creates a refund to the sender and points to the approval screen', async () => {
    const posts: unknown[] = [];
    mockApi([
      ...detailRoutes,
      {
        method: 'POST',
        path: '/api/bil/v1/suspense/refund-to-sender',
        respond: (request) => {
          posts.push(request.body);
          return {
            body: {
              item: { ...item, state: 'REFUND_PENDING' },
              refund: { refundId: 'e0000000-1111-4222-8333-000000000001' },
            },
          };
        },
      },
    ]);
    renderScreen(<SuspensePage />, route);
    await userEvent.type(
      await screen.findByLabelText(/Αναγνωριστικό λογαριασμού δικαιούχου/),
      payeeId,
    );
    await userEvent.click(screen.getByRole('button', { name: /Αιτιολογία/ }));
    await userEvent.click(await screen.findByRole('option', { name: /Υπερείσπραξη/ }));
    await userEvent.click(screen.getByRole('button', { name: /^Επιστροφή στον αποστολέα…/ }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Δημιουργία επιστροφής' }));
    expect(
      await screen.findByRole('button', { name: 'Άνοιγμα της επιστροφής' }),
    ).toBeInTheDocument();
    expect(posts).toEqual([
      { suspenseItemId: item.suspenseItemId, payeeAccountId: payeeId, reasonCode: 'OVERPAYMENT' },
    ]);
  });

  it('hides transfer and refund for a role without them', async () => {
    setRoles(['Platform.Admin']);
    mockApi(detailRoutes);
    renderScreen(<SuspensePage />, route);
    expect(await screen.findByText(/απαιτούν ρόλο χρέωσης/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Μεταφορά…/ })).not.toBeInTheDocument();
  });

  it('shows a forbidden list as no permission', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/suspense',
        respond: () => problem(403, 'AUTH-ERR-FORBIDDEN', 'Forbidden'),
      },
    ]);
    renderScreen(<SuspensePage />, { path: route.path, url: '/billing/suspense' });
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
