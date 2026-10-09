import { screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { InvoiceGetResponse } from '../../../api/types';
import { expectNoA11yViolations } from '../../../test/axe';
import * as fx from '../../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../../test/mockApi';
import { AccountPage } from '../AccountPage';
import { InvoicePage } from '../InvoicePage';
import type { RefundView } from './api';
import { RefundDetailPage } from './RefundDetailPage';
import { RefundsInboxPage } from './RefundsInboxPage';
import { RequireRefundRole } from './RequireRefundRole';

vi.setConfig({ testTimeout: 60_000 });

const refundId = 'f0000000-1111-4222-8333-000000000001';
const makerId = 'e0000000-1111-4222-8333-0000000000aa';
const iban = 'GR1601101250000000012300695';

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
  signIn('billingmgr', ['Staff.BillingManager']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const eur = (amount: string) => ({ amount, currency: 'EUR' });

function refund(over: Partial<RefundView> = {}): RefundView {
  return {
    refundId,
    billingAccountId: fx.accountId,
    state: 'PENDING_APPROVAL',
    approvalState: 'PENDING',
    amount: eur('620.00'),
    breakdown: [
      {
        policyId: fx.policyId,
        policyTermId: 'term-1',
        transactionId: 'tx-9',
        creditNoteId: 'dddddddd-1111-4222-8333-000000000001',
        chargeType: 'PREM-MTPL',
        chargeCategory: 'PREMIUM',
        transactionKind: 'CANCELLATION',
        amount: eur('560.00'),
      },
      {
        policyId: fx.policyId,
        policyTermId: 'term-1',
        transactionId: 'tx-9',
        chargeType: 'TAX-IPT',
        chargeCategory: 'TAX',
        transactionKind: 'CANCELLATION',
        treatmentRuleId: 'TAX-REFUND-PRORATA',
        legalStatus: 'Open',
        provisional: true,
        amount: eur('80.00'),
      },
    ],
    netting: [{ kind: 'OPEN_INVOICE', invoiceId: fx.invoiceId, amount: eur('20.00') }],
    payee: {
      payeePartyId: fx.partyId,
      payeeAccountId: 'aaaaaaaa-0000-4000-8000-0000000000a1',
      maskedIban: 'GR16 **** **** **** **** *** 0695',
      verificationStatus: 'VoPMatched',
    },
    payoutMethod: 'SEPA_CT',
    reasonCode: 'CANCELLATION',
    sourcePolicyIds: [fx.policyId],
    requestedBy: makerId,
    proposedAt: '2026-10-08T09:00:00Z',
    recordVersion: 1,
    ...over,
  };
}

const page = (...items: RefundView[]) => ({
  items: items.map((r) => ({ refund: r })),
  nextCursor: null,
  limit: 50,
});

const listRoute = (pending: RefundView[], all: RefundView[]): MockRoute => ({
  method: 'GET',
  path: '/api/bil/v1/refunds',
  respond: (request) => ({
    body:
      request.url.searchParams.get('state') === 'PENDING_APPROVAL'
        ? page(...pending)
        : page(...all),
  }),
});

const getRoute = (r: RefundView): MockRoute => ({
  method: 'GET',
  path: `/api/bil/v1/refunds/${r.refundId}`,
  respond: () => ({ body: { refund: r } }),
});

const detail = () =>
  renderScreen(<RefundDetailPage />, {
    path: '/billing/refunds/:refundId',
    url: `/billing/refunds/${refundId}`,
  });

describe('RefundsInboxPage', () => {
  it('lists the refunds awaiting a decision with the masked IBAN only and opens one', async () => {
    mockApi([
      listRoute(
        [refund()],
        [refund(), refund({ refundId: 'f2', state: 'PAID', approvalState: 'APPROVED' })],
      ),
    ]);
    const { container, user } = renderScreen(<RefundsInboxPage />, {
      path: '/billing/refunds',
      url: '/billing/refunds',
    });
    const pending = await screen.findByRole('grid', { name: 'Αναμένουν απόφαση' });
    expect(within(pending).getByText(/GR16 \*{4}/)).toBeInTheDocument();
    expect(within(pending).getByText('620,00 €')).toBeInTheDocument();
    expect(container.textContent).not.toContain(iban);
    expect(screen.getByRole('grid', { name: 'Όλες οι επιστροφές' })).toBeInTheDocument();
    await expectNoA11yViolations(container);
    within(pending).getAllByRole('row').at(1)?.focus();
    await user.keyboard('{Enter}');
    expect(await screen.findByTestId('location')).toHaveTextContent(`/billing/refunds/${refundId}`);
  });

  it('shows the done-empty state when nothing awaits a decision', async () => {
    mockApi([listRoute([], [])]);
    renderScreen(<RefundsInboxPage />, { path: '/billing/refunds', url: '/billing/refunds' });
    expect(await screen.findByText('Καμία επιστροφή δεν αναμένει απόφαση')).toBeInTheDocument();
    expect(screen.getByText('Δεν υπάρχουν επιστροφές ακόμη')).toBeInTheDocument();
  });

  it('shows a retryable error with the trace id when the list fails', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/refunds',
        respond: () => problem(500, 'PLT-ERR-INTERNAL', 'Σφάλμα διακομιστή'),
      },
    ]);
    renderScreen(<RefundsInboxPage />, { path: '/billing/refunds', url: '/billing/refunds' });
    expect((await screen.findAllByText('Σφάλμα διακομιστή')).length).toBeGreaterThan(0);
  });

  it('shows the no-permission state to a role without billing', async () => {
    signIn('claims', ['Staff.ClaimsHandler']);
    mockApi([]);
    renderScreen(<RequireRefundRole />, { path: '/billing/refunds', url: '/billing/refunds' });
    expect(await screen.findByText(/δικαίωμα|πρόσβαση/i)).toBeInTheDocument();
  });
});

describe('RefundDetailPage', () => {
  it('shows summary, masked payee, breakdown by charge type with treatment rule, provisional marker and netting', async () => {
    mockApi([getRoute(refund())]);
    const { container } = detail();
    expect(await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' })).toBeInTheDocument();
    const payee = screen.getByRole('region', { name: 'Δικαιούχος' });
    expect(within(payee).getByText('GR16 **** **** **** **** *** 0695')).toBeInTheDocument();
    expect(within(payee).getByText('Το όνομα ταιριάζει')).toBeInTheDocument();
    const breakdown = screen.getByRole('grid', { name: 'Ανάλυση ανά είδος χρέωσης' });
    expect(within(breakdown).getByText('PREM-MTPL')).toBeInTheDocument();
    expect(within(breakdown).getByText('TAX-REFUND-PRORATA')).toBeInTheDocument();
    expect(within(breakdown).getByText(/Προσωρινό/)).toBeInTheDocument();
    expect(screen.getByRole('grid', { name: 'Συμψηφισμός' })).toBeInTheDocument();
    expect(container.textContent).not.toContain(iban);
    await expectNoA11yViolations(container);
  });

  it('lets the billing manager approve; the request carries only the id and the decision', async () => {
    const api = mockApi([
      getRoute(refund()),
      {
        method: 'POST',
        path: '/api/bil/v1/refunds/decide',
        respond: () => ({
          body: {
            refund: refund({ state: 'APPROVED', approvalState: 'APPROVED' }),
            disbursementId: 'd0000000-1111-4222-8333-000000000001',
          },
        }),
      },
    ]);
    const { user } = detail();
    await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' });
    await user.click(screen.getByRole('button', { name: 'Έγκριση επιστροφής' }));
    expect((await screen.findAllByText('Η επιστροφή εγκρίθηκε')).length).toBeGreaterThan(0);
    expect(screen.getByText('d0000000-1111-4222-8333-000000000001')).toBeInTheDocument();
    const [call] = api.callsTo('POST', '/api/bil/v1/refunds/decide');
    expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(call?.body).toEqual({ refundId, decision: 'APPROVE' });
  });

  it('requires a comment to reject and sends nothing without it', async () => {
    const api = mockApi([getRoute(refund())]);
    const { user } = detail();
    await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' });
    await user.click(screen.getByRole('button', { name: 'Απόρριψη επιστροφής' }));
    expect(await screen.findByText('Συμπληρώστε τον λόγο απόρριψης.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/bil/v1/refunds/decide')).toHaveLength(0);
  });

  it('surfaces the server refusal (separation of duties) as a readable problem', async () => {
    mockApi([
      getRoute(refund()),
      {
        method: 'POST',
        path: '/api/bil/v1/refunds/decide',
        respond: () => problem(403, 'PLT-ERR-SOD', 'Ο αιτών δεν μπορεί να εγκρίνει'),
      },
    ]);
    const { user } = detail();
    await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' });
    await user.click(screen.getByRole('button', { name: 'Έγκριση επιστροφής' }));
    expect(await screen.findByText('Η απόφαση απέτυχε')).toBeInTheDocument();
  });

  it('hides approve and reject from the maker even with the manager role', async () => {
    signIn(makerId, ['Staff.BillingManager']);
    mockApi([getRoute(refund())]);
    detail();
    await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' });
    expect(screen.queryByRole('button', { name: 'Έγκριση επιστροφής' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Απόρριψη επιστροφής' })).toBeNull();
    expect(screen.getByText(/Εσείς προτείνατε αυτή την επιστροφή/)).toBeInTheDocument();
  });

  it('hides the decision from a billing clerk', async () => {
    signIn('billing', ['Staff.Billing']);
    mockApi([getRoute(refund())]);
    detail();
    await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' });
    expect(screen.queryByRole('button', { name: 'Έγκριση επιστροφής' })).toBeNull();
    expect(screen.getByText(/Μόνο ο υπεύθυνος χρεώσεων αποφασίζει/)).toBeInTheDocument();
  });

  it('offers resubmit for a rejected refund and sends the id and comment only', async () => {
    signIn('billing', ['Staff.Billing']);
    const api = mockApi([
      getRoute(refund({ state: 'REJECTED', approvalState: 'REJECTED' })),
      {
        method: 'POST',
        path: '/api/bil/v1/refunds/resubmit',
        respond: () => ({ body: { refund: refund({ state: 'PROPOSED' }) } }),
      },
    ]);
    const { user } = detail();
    await screen.findByRole('heading', { level: 1, name: 'Επιστροφή' });
    await user.click(screen.getByRole('button', { name: 'Επανυποβολή επιστροφής' }));
    expect((await screen.findAllByText('Η επιστροφή υποβλήθηκε ξανά')).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/bil/v1/refunds/resubmit')[0]?.body).toEqual({ refundId });
  });

  it('shows not found for an unknown refund', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/refunds/${refundId}`,
        respond: () => problem(404, 'PLT-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    detail();
    expect(await screen.findByText('Η επιστροφή δεν βρέθηκε')).toBeInTheDocument();
  });
});

describe('ProposeRefundSection on the billing account', () => {
  const creditAccount = () => {
    const base = fx.account();
    return { ...base, balancesByState: { ...base.balancesByState, credit: eur('620.00') } };
  };
  const accountRoutes = (extra: MockRoute[] = []): MockRoute[] => [
    {
      method: 'GET',
      path: `/api/bil/v1/billing-accounts/${fx.accountId}`,
      respond: () => ({ body: creditAccount() }),
    },
    {
      method: 'GET',
      path: '/api/bil/v1/invoices',
      respond: () => ({ body: { items: [], nextCursor: null, limit: 50 } }),
    },
    ...extra,
  ];
  const view = () =>
    renderScreen(<AccountPage />, {
      path: '/billing/accounts/:accountId',
      url: `/billing/accounts/${fx.accountId}`,
    });

  it('previews with a dry run, then proposes; the request never sets authority and never carries an IBAN', async () => {
    signIn('billing', ['Staff.Billing']);
    const api = mockApi(
      accountRoutes([
        {
          method: 'POST',
          path: '/api/bil/v1/refunds/propose',
          respond: (request) => ({
            status: request.url.searchParams.get('dryRun') === 'true' ? 200 : 201,
            body: { refund: refund({ state: 'PROPOSED', approvalState: 'PENDING' }) },
          }),
        },
      ]),
    );
    const { user } = view();
    const form = await screen.findByRole('form', { name: 'Επιστροφή πιστωτικού υπολοίπου' });
    expect(within(form).getByText('620,00 €')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Πρόταση επιστροφής' }));
    // Proposing is only possible after the preview.
    expect(api.callsTo('POST', '/api/bil/v1/refunds/propose')).toHaveLength(0);
    await user.click(within(form).getByRole('button', { name: 'Προεπισκόπηση επιστροφής' }));
    expect(await within(form).findByText('Επιλέξτε αιτία.')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: /Αιτία/ }));
    await user.click(await screen.findByRole('option', { name: 'Ακύρωση' }));
    await user.click(within(form).getByRole('button', { name: 'Προεπισκόπηση επιστροφής' }));
    expect(await within(form).findByText('Δεν έχει αποθηκευτεί τίποτα ακόμη.')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Πρόταση επιστροφής' }));
    expect(await screen.findByTestId('location')).toHaveTextContent('/billing');

    const calls = api.callsTo('POST', '/api/bil/v1/refunds/propose');
    expect(calls).toHaveLength(2);
    expect(calls[0]?.url.searchParams.get('dryRun')).toBe('true');
    expect(calls[1]?.url.searchParams.get('dryRun')).toBeNull();
    expect(calls[1]?.body).toEqual({ billingAccountId: fx.accountId, reasonCode: 'CANCELLATION' });
    expect(calls[1]?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('captures a different payee account with the IBAN in the body only, then clears it', async () => {
    signIn('billing', ['Staff.Billing']);
    const api = mockApi(
      accountRoutes([
        {
          method: 'POST',
          path: '/api/bil/v1/payee-accounts',
          respond: () => ({
            status: 201,
            body: {
              payeeAccountId: 'aaaaaaaa-0000-4000-8000-0000000000a2',
              verificationStatus: 'VoPMatched',
              coolingOffUntil: '2026-10-22',
              maskedIban: 'GR16 **** 0695',
              change: true,
            },
          }),
        },
      ]),
    );
    const { user } = view();
    const form = await screen.findByRole('form', { name: 'Επιστροφή πιστωτικού υπολοίπου' });
    await user.click(
      within(form).getByRole('checkbox', { name: 'Πληρωμή σε άλλον τραπεζικό λογαριασμό' }),
    );
    await user.type(
      within(form).getByRole('textbox', { name: /Δικαιούχος λογαριασμού/ }),
      'Δοκιμή Διεπαφής',
    );
    await user.type(within(form).getByRole('textbox', { name: /IBAN/ }), iban);
    await user.click(within(form).getByRole('button', { name: 'Αποθήκευση λογαριασμού' }));
    expect(await within(form).findByText('GR16 **** 0695')).toBeInTheDocument();
    expect(within(form).getByRole('textbox', { name: /IBAN/ })).toHaveValue('');
    const [call] = api.callsTo('POST', '/api/bil/v1/payee-accounts');
    expect(call?.url.href).not.toContain(iban);
    expect(call?.body).toMatchObject({
      partyId: fx.partyId,
      purpose: 'REFUND',
      iban,
      holderName: 'Δοκιμή Διεπαφής',
    });
    expect(JSON.stringify(Object.entries(sessionStorage))).not.toContain(iban);
    expect(JSON.stringify(Object.entries(localStorage))).not.toContain(iban);
  });

  it('says there is no credit to refund', async () => {
    signIn('billing', ['Staff.Billing']);
    mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/billing-accounts/${fx.accountId}`,
        respond: () => ({ body: fx.account() }),
      },
      {
        method: 'GET',
        path: '/api/bil/v1/invoices',
        respond: () => ({ body: { items: [], nextCursor: null, limit: 50 } }),
      },
    ]);
    view();
    expect(
      await screen.findByText('Ο λογαριασμός δεν έχει πιστωτικό υπόλοιπο προς επιστροφή.'),
    ).toBeInTheDocument();
  });

  it('does not offer the refund form to a role without billing', async () => {
    signIn('claims', ['Staff.ClaimsHandler']);
    mockApi(accountRoutes());
    view();
    await screen.findByRole('heading', { level: 1 });
    expect(screen.queryByRole('form', { name: 'Επιστροφή πιστωτικού υπολοίπου' })).toBeNull();
  });
});

describe('Credit note on the invoice page', () => {
  it('marks the credit note, links the original invoice and shows the treatment rule of its lines', async () => {
    const original = 'cccccccc-1111-4222-8333-000000000099';
    const base = fx.invoice('BILLED');
    const credit = {
      ...base,
      invoice: { ...base.invoice, kind: 'CREDIT_NOTE', originalInvoiceId: original },
      invoiceItems: base.invoiceItems.map((item) => ({
        ...item,
        transactionKind: 'CANCELLATION',
        treatmentRuleId: 'TAX-REFUND-PRORATA',
      })),
    } as InvoiceGetResponse;
    mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/invoices/${fx.invoiceId}`,
        respond: () => ({ body: credit }),
      },
    ]);
    const { container, user } = renderScreen(<InvoicePage />, {
      path: '/billing/invoices/:invoiceId',
      url: `/billing/invoices/${fx.invoiceId}`,
    });
    await screen.findByRole('heading', { level: 1 });
    expect(screen.getAllByText('Πιστωτικό σημείωμα').length).toBeGreaterThan(0);
    const lines = screen.getByRole('grid', { name: 'Γραμμές τιμολογίου' });
    expect(within(lines).getByText('TAX-REFUND-PRORATA')).toBeInTheDocument();
    expect(within(lines).getByText('CANCELLATION')).toBeInTheDocument();
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Άνοιγμα αρχικού τιμολογίου' }));
    expect(await screen.findByTestId('location')).toHaveTextContent(
      `/billing/invoices/${original}`,
    );
  });
});
