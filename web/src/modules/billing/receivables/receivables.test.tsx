import { screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import * as fx from '../../../test/fixtures';
import { mockApi, renderScreen, type MockRoute } from '../../../test/mockApi';
import type { DisbursementView, ReceivableView } from './api';
import { ReceivableDetailPage } from './ReceivableDetailPage';
import { ReceivablesListPage } from './ReceivablesListPage';
import { ReleaseDetailPage } from './ReleaseDetailPage';
import { ReleasesInboxPage } from './ReleasesInboxPage';

vi.setConfig({ testTimeout: 60_000 });

const receivableId = 'a0000000-1111-4222-8333-000000000001';
const disbursementId = 'd0000000-1111-4222-8333-000000000001';
const eur = (amount: string) => ({ amount, currency: 'EUR' });

function signIn(id: string, roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id, name: id, roles },
    }),
  );
}

beforeEach(() => {
  signIn('billing', ['Staff.Billing']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function receivable(over: Partial<ReceivableView> = {}): ReceivableView {
  return {
    receivableId,
    billingAccountId: fx.accountId,
    sourceType: 'CLM_CLAIM_PAYMENT',
    sourceId: 'rec-1',
    purpose: 'SALVAGE',
    counterpartyPartyId: fx.partyId,
    claimId: null,
    recoveryId: null,
    statementRef: null,
    amount: eur('300.00'),
    openAmount: eur('300.00'),
    status: 'OPEN',
    dueDate: '2026-11-01',
    paymentReference: 'RCV-0001',
    registeredAt: '2026-10-10T09:00:00Z',
    recordVersion: 1,
    ...over,
  };
}

const partyRoute: MockRoute = {
  method: 'GET',
  path: new RegExp(`^/api/pty/v1/parties/`),
  respond: () => ({
    body: {
      party: {
        partyId: fx.partyId,
        partyNumber: 'P-1',
        names: [{ form: 'NATIVE', organisationName: 'Ασφαλιστική Δοκιμής' }],
      },
    },
  }),
};

const listRoute = (items: ReceivableView[]): MockRoute => ({
  method: 'GET',
  path: '/api/bil/v1/receivables',
  respond: () => ({ body: { items, nextCursor: null, limit: 100 } }),
});

describe('receivables list', () => {
  it('shows source, purpose, reference, amounts and status, and filters by status', async () => {
    const api = mockApi([
      listRoute([receivable(), receivable({ receivableId: 'r2', sourceType: 'FS_CLEARING' })]),
      partyRoute,
    ]);
    renderScreen(<ReceivablesListPage />, {
      path: '/billing/receivables',
      url: '/billing/receivables',
    });
    const grid = await screen.findByRole('grid', { name: 'Απαιτήσεις' });
    expect(within(grid).getAllByText('RCV-0001').length).toBeGreaterThan(0);
    expect(within(grid).getByText('Ανάκτηση ζημίας')).toBeInTheDocument();
    expect(within(grid).getByText('Φιλικός Διακανονισμός')).toBeInTheDocument();
    expect(within(grid).getAllByText('300,00 €').length).toBeGreaterThan(0);
    expect(api.callsTo('GET', '/api/bil/v1/receivables')[0]?.url.searchParams.has('status')).toBe(
      false,
    );
  });

  it('shows the empty state', async () => {
    mockApi([listRoute([])]);
    renderScreen(<ReceivablesListPage />, {
      path: '/billing/receivables',
      url: '/billing/receivables',
    });
    expect(await screen.findByText('Δεν υπάρχουν απαιτήσεις')).toBeInTheDocument();
  });
});

describe('receivable detail', () => {
  const detailRoutes = (
    r: ReceivableView,
    onTake?: (body: unknown, key: string | null) => void,
  ) => [
    {
      method: 'GET',
      path: `/api/bil/v1/receivables/${r.receivableId}`,
      respond: () => ({ body: { receivable: r } }),
    } satisfies MockRoute,
    {
      method: 'POST',
      path: '/api/bil/v1/payments/take',
      respond: (request) => {
        onTake?.(request.body, request.headers.get('Idempotency-Key'));
        return {
          body: {
            receipt: { receiptNumber: 'RC-77', amount: eur('100.00') },
            allocations: [],
            allocationOutcome: 'RECEIVABLE_ALLOCATED',
            receivableAllocation: {
              receivableId: r.receivableId,
              allocationId: 'al-1',
              amount: eur('100.00'),
              openAmountAfter: eur('200.00'),
              status: 'PARTIALLY_PAID',
            },
          },
        };
      },
    } satisfies MockRoute,
    partyRoute,
  ];
  const show = () =>
    renderScreen(<ReceivableDetailPage />, {
      path: '/billing/receivables/:receivableId',
      url: `/billing/receivables/${receivableId}`,
    });

  it('records a partial receipt with the payment reference and shows the open balance', async () => {
    let sent: { paymentReference?: string; amount?: { amount: string } } = {};
    let key: string | null = null;
    mockApi(
      detailRoutes(receivable(), (body, k) => {
        sent = body as typeof sent;
        key = k;
      }),
    );
    const { user } = show();
    await screen.findByRole('heading', { level: 1, name: 'Απαίτηση' });
    const amount = await screen.findByLabelText(/Ποσό είσπραξης/);
    await user.clear(amount);
    await user.type(amount, '100,00');
    await user.click(screen.getByRole('button', { name: 'Καταχώριση είσπραξης' }));
    expect(
      (await screen.findAllByText(/απόδειξη RC-77/, {}, { timeout: 10_000 })).length,
    ).toBeGreaterThan(0);
    expect(sent.paymentReference).toBe('RCV-0001');
    expect(sent.amount?.amount).toBe('100.00');
    expect(key).toBeTruthy();
    expect(screen.getByText(/200,00 €/)).toBeInTheDocument();
  });

  it('refuses a receipt above the open amount without calling the API', async () => {
    const api = mockApi(detailRoutes(receivable({ openAmount: eur('50.00') })));
    const { user } = show();
    await screen.findByRole('heading', { level: 1, name: 'Απαίτηση' });
    const amount = await screen.findByLabelText(/Ποσό είσπραξης/);
    await user.clear(amount);
    await user.type(amount, '60,00');
    await user.click(screen.getByRole('button', { name: 'Καταχώριση είσπραξης' }));
    expect(await screen.findByText(/δεν μπορεί να υπερβαίνει/)).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/bil/v1/payments/take')).toHaveLength(0);
  });

  it('offers no receipt form for a paid receivable', async () => {
    mockApi(detailRoutes(receivable({ status: 'PAID', openAmount: eur('0.00') })));
    show();
    expect(
      await screen.findByText('Η απαίτηση δεν δέχεται εισπράξεις στην τρέχουσα κατάστασή της.'),
    ).toBeInTheDocument();
  });
});

function disbursement(over: Partial<DisbursementView> = {}): DisbursementView {
  return {
    disbursementId,
    status: 'PendingApproval',
    sourceType: 'FS_CLEARING',
    sourceId: 'stmt-1',
    amount: eur('250.00'),
    method: 'CLEARING',
    statementReference: 'FS-STMT-2026-10',
    payeePartyId: fx.partyId,
    maskedPayeeAccount: 'GR16 **** 0695',
    requestedAt: '2026-10-12T08:00:00Z',
    releaseApproval: { required: true, requestedBy: 'maker-1' },
    lines: [
      { claimId: 'c1', fsCaseId: 'f1', amount: eur('400.00') },
      { claimId: 'c2', fsCaseId: 'f2', amount: eur('-150.00') },
    ],
    ...over,
  };
}

describe('release approvals', () => {
  const disbRoutes = (d: DisbursementView, onDecide?: (body: unknown) => void): MockRoute[] => [
    {
      method: 'GET',
      path: '/api/bil/v1/disbursements',
      respond: () => ({
        body: {
          items: [
            { disbursement: d },
            { disbursement: { ...d, disbursementId: 'other', sourceType: 'CLM_CLAIM_PAYMENT' } },
          ],
          nextCursor: null,
          limit: 200,
        },
      }),
    },
    {
      method: 'GET',
      path: `/api/bil/v1/disbursements/${d.disbursementId}`,
      respond: () => ({ body: { disbursement: d } }),
    },
    {
      method: 'POST',
      path: '/api/bil/v1/disbursements/approve-release',
      respond: (request) => {
        onDecide?.(request.body);
        return {
          body: {
            disbursement: { ...d, status: 'Approved' },
            decision: (request.body as { decision: 'APPROVE' | 'REJECT' }).decision,
          },
        };
      },
    },
    partyRoute,
  ];
  const showDetail = () =>
    renderScreen(<ReleaseDetailPage />, {
      path: '/billing/releases/:disbursementId',
      url: `/billing/releases/${disbursementId}`,
    });

  it('lists only FS_CLEARING releases awaiting approval', async () => {
    mockApi(disbRoutes(disbursement()));
    renderScreen(<ReleasesInboxPage />, { path: '/billing/releases', url: '/billing/releases' });
    const pending = await screen.findByRole('grid', { name: 'Αναμένουν έγκριση' });
    expect(within(pending).getByText(/FS-STMT-2026-10/)).toBeInTheDocument();
    expect(within(pending).getAllByRole('row')).toHaveLength(2);
  });

  it('shows lines, flags a sum that differs from the net and lets the manager approve', async () => {
    signIn('mgr', ['Staff.BillingManager']);
    let sent: { decision?: string } = {};
    mockApi(
      disbRoutes(disbursement(), (b) => {
        sent = b as typeof sent;
      }),
    );
    const { user } = showDetail();
    await screen.findByRole('heading', { level: 1, name: 'Αποδέσμευση συμψηφισμού' });
    expect(screen.getByText('CLEARING')).toBeInTheDocument();
    expect(screen.getByText(/Άθροισμα γραμμών: 250,00 €/)).toBeInTheDocument();
    expect(
      screen.getByText('Το άθροισμα των γραμμών ισούται με το καθαρό ποσό.'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Έγκριση αποδέσμευσης' }));
    expect(
      (await screen.findAllByText('Η αποδέσμευση εγκρίθηκε', {}, { timeout: 10_000 })).length,
    ).toBeGreaterThan(0);
    expect(sent.decision).toBe('APPROVE');
  });

  it('disables the actions for the requester and shows the SoD reason', async () => {
    signIn('maker-1', ['Staff.BillingManager']);
    mockApi(disbRoutes(disbursement()));
    showDetail();
    await screen.findByRole('heading', { level: 1, name: 'Αποδέσμευση συμψηφισμού' });
    expect(screen.getByText(/Εσείς υποβάλατε το αίτημα/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Έγκριση αποδέσμευσης' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Απόρριψη' })).toBeDisabled();
  });

  it('requires a reason to reject', async () => {
    signIn('mgr', ['Staff.BillingManager']);
    const api = mockApi(disbRoutes(disbursement()));
    const { user } = showDetail();
    await user.click(await screen.findByRole('button', { name: 'Απόρριψη' }));
    expect(await screen.findByText('Δώστε αιτιολογία για την απόρριψη.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/bil/v1/disbursements/approve-release')).toHaveLength(0);
  });
});
