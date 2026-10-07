import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { rememberRecent } from '../staff/recent';
import { AccountPage } from './AccountPage';
import { BillingHomePage } from './BillingHomePage';
import { InvoicePage } from './InvoicePage';

vi.setConfig({ testTimeout: 30_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const receipt = {
  receiptId: 'rcp-1',
  receiptNumber: 'RCP000000001',
  billingAccountId: fx.accountId,
  state: 'ALLOCATED',
  channel: 'STAFF',
  method: 'BANK_TRANSFER',
  amount: { amount: '479.20', currency: 'EUR' },
  allocated: { amount: '479.20', currency: 'EUR' },
  unallocated: { amount: '0.00', currency: 'EUR' },
  valueDate: '2026-10-07',
  accountingDate: '2026-10-07',
  recordedAt: '2026-10-07T20:30:00Z',
};

const invoiceRoutes = (
  state: 'BILLED' | 'PAID' = 'BILLED',
  take?: MockRoute['respond'],
): MockRoute[] => [
  {
    method: 'GET',
    path: `/api/bil/v1/invoices/${fx.invoiceId}`,
    respond: () => ({ body: fx.invoice(state) }),
  },
  {
    method: 'POST',
    path: '/api/bil/v1/payments/take',
    respond:
      take ??
      (() => ({ status: 201, body: { receipt, allocations: [], allocationOutcome: 'ALLOCATED' } })),
  },
];
const invoiceView = () =>
  renderScreen(<InvoicePage />, {
    path: '/billing/invoices/:invoiceId',
    url: `/billing/invoices/${fx.invoiceId}`,
  });

describe('InvoicePage', () => {
  it('shows number, status, lines, totals and the fiscal document marked as a stub', async () => {
    mockApi(invoiceRoutes());
    const { container } = invoiceView();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Τιμολόγιο INV000000003' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Εκδόθηκε').length).toBeGreaterThan(0);

    const fiscal = screen.getByRole('region', { name: 'Φορολογικό παραστατικό' });
    expect(within(fiscal).getByText('Δοκιμαστικό φορολογικό παραστατικό')).toBeInTheDocument();
    expect(within(fiscal).getByText(/Καταχωρίστηκε \(ΜΑΡΚ\)/)).toBeInTheDocument();
    expect(within(fiscal).getByText(/Δοκιμαστικό \(stub\)/)).toBeInTheDocument();
    expect(within(fiscal).getByText('400000000042')).toBeInTheDocument();

    const lines = screen.getByRole('grid', { name: 'Γραμμές τιμολογίου' });
    expect(within(lines).getByText('PREM-MTPL')).toBeInTheDocument();
    expect(within(lines).getAllByText('312,35 €').length).toBeGreaterThan(0);
    const totals = screen.getByRole('region', { name: 'Σύνολα' });
    expect(within(totals).getAllByText('479,20 €').length).toBeGreaterThan(0);
    expect(
      screen.getByText('Δεν έχει κατανεμηθεί είσπραξη σε αυτό το τιμολόγιο.'),
    ).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('records a payment with an Idempotency-Key, reuses the key on retry and reports the receipt', async () => {
    let attempt = 0;
    const api = mockApi(
      invoiceRoutes('BILLED', () =>
        ++attempt === 1
          ? problem(503, 'BIL-ERR-METHOD-UNAVAILABLE', 'Η υπηρεσία δεν είναι διαθέσιμη')
          : { status: 201, body: { receipt, allocations: [], allocationOutcome: 'ALLOCATED' } },
      ),
    );
    const { user } = invoiceView();
    await screen.findByRole('heading', { level: 1, name: 'Τιμολόγιο INV000000003' });
    const form = screen.getByRole('form', { name: 'Καταχώριση πληρωμής' });
    const submit = within(form).getByRole('button', { name: 'Καταχώριση πληρωμής' });

    // A bank transfer needs its reference: the error summary says so and nothing is sent.
    await user.click(submit);
    expect(
      await screen.findAllByText('Συμπληρώστε την αναφορά τράπεζας του εμβάσματος.'),
    ).not.toHaveLength(0);
    expect(api.callsTo('POST', '/api/bil/v1/payments/take')).toHaveLength(0);

    await user.type(
      within(form).getByRole('textbox', { name: /Αναφορά τράπεζας/ }),
      'TRF-2026-1007',
    );
    await user.click(submit);
    expect(await screen.findByText('Η καταχώριση της πληρωμής απέτυχε')).toBeInTheDocument();
    await user.click(submit);
    expect(
      (await screen.findAllByText('Καταχωρίστηκε η απόδειξη RCP000000001')).length,
    ).toBeGreaterThan(0);

    const [first, second] = api.callsTo('POST', '/api/bil/v1/payments/take');
    expect(first?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(second?.headers.get('Idempotency-Key')).toBe(first?.headers.get('Idempotency-Key'));
    expect(second?.body).toMatchObject({
      billingAccountId: fx.accountId,
      amount: { amount: '479.20', currency: 'EUR' },
      method: 'BANK_TRANSFER',
      bankReference: 'TRF-2026-1007',
      invoiceId: fx.invoiceId,
      autoAllocate: true,
    });
    expect((second?.body as { valueDate: string }).valueDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('does not accept more than the open balance', async () => {
    const api = mockApi(invoiceRoutes());
    const { user } = invoiceView();
    await screen.findByRole('heading', { level: 1, name: 'Τιμολόγιο INV000000003' });
    const form = screen.getByRole('form', { name: 'Καταχώριση πληρωμής' });
    const amount = within(form).getByRole('textbox', { name: /^Ποσό/ });
    await user.clear(amount);
    await user.type(amount, '500');
    await user.type(within(form).getByRole('textbox', { name: /Αναφορά τράπεζας/ }), 'X1');
    await user.click(within(form).getByRole('button', { name: 'Καταχώριση πληρωμής' }));
    expect(await screen.findAllByText(/υπερβαίνει το ανοιχτό υπόλοιπο/)).not.toHaveLength(0);
    expect(api.callsTo('POST', '/api/bil/v1/payments/take')).toHaveLength(0);
  });

  it('offers no payment form for a paid invoice, and handles not-found', async () => {
    mockApi(invoiceRoutes('PAID'));
    invoiceView();
    expect(
      await screen.findByText('Δεν υπάρχει ανοιχτό υπόλοιπο για πληρωμή.'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Εξοφλήθηκε').length).toBeGreaterThan(0);
  });

  it('shows not-found', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/invoices/${fx.invoiceId}`,
        respond: () => problem(404, 'BIL-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    invoiceView();
    expect(await screen.findByText('Το τιμολόγιο δεν βρέθηκε')).toBeInTheDocument();
  });
});

describe('AccountPage', () => {
  it('shows balances, billed terms, invoices and the record-payment form', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/billing-accounts/${fx.accountId}`,
        respond: () => ({ body: fx.account() }),
      },
      { method: 'GET', path: '/api/bil/v1/invoices', respond: () => ({ body: fx.invoiceList }) },
    ]);
    const { container } = renderScreen(<AccountPage />, {
      path: '/billing/accounts/:accountId',
      url: `/billing/accounts/${fx.accountId}`,
    });
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Λογαριασμός χρέωσης BA000000002' }),
    ).toBeInTheDocument();
    const balances = screen.getByRole('region', { name: 'Υπόλοιπα ανά κατάσταση' });
    expect(within(balances).getByText('Τιμολογημένο')).toBeInTheDocument();
    expect(within(balances).getAllByText('479,20 €')).toHaveLength(1);
    const terms = screen.getByRole('grid', { name: 'Περίοδοι ασφαλιστηρίων' });
    expect(within(terms).getByText('POL000000007')).toBeInTheDocument();
    expect(await screen.findByRole('grid', { name: 'Τιμολόγια' })).toBeInTheDocument();
    expect(screen.getByRole('form', { name: 'Καταχώριση πληρωμής' })).toBeInTheDocument();
    expect(
      api.callsTo('GET', '/api/bil/v1/invoices')[0]?.url.searchParams.get('billingAccountId'),
    ).toBe(fx.accountId);
    await expectNoA11yViolations(container);
  });

  it('shows not-found', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/billing-accounts/${fx.accountId}`,
        respond: () => problem(404, 'BIL-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    renderScreen(<AccountPage />, {
      path: '/billing/accounts/:accountId',
      url: `/billing/accounts/${fx.accountId}`,
    });
    expect(await screen.findByText('Ο λογαριασμός χρέωσης δεν βρέθηκε')).toBeInTheDocument();
  });
});

describe('BillingHomePage', () => {
  it('starts empty, validates a record id and lists recent records', async () => {
    const { user, container } = renderScreen(<BillingHomePage />, {
      path: '/billing',
      url: '/billing',
    });
    expect(screen.getByText('Δεν υπάρχουν πρόσφατες εγγραφές')).toBeInTheDocument();
    await expectNoA11yViolations(container);
    await user.type(screen.getByRole('textbox', { name: /Αναγνωριστικό/ }), 'nope{Enter}');
    expect(await screen.findByText('Δώστε έγκυρο αναγνωριστικό UUID.')).toBeInTheDocument();
    await user.clear(screen.getByRole('textbox', { name: /Αναγνωριστικό/ }));
    await user.type(
      screen.getByRole('textbox', { name: /Αναγνωριστικό/ }),
      `${fx.invoiceId}{Enter}`,
    );
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`/billing/invoices/${fx.invoiceId}`);
    });
  });

  it('lists recently opened invoices', () => {
    rememberRecent('invoice', { id: fx.invoiceId, label: 'INV000000003' });
    renderScreen(<BillingHomePage />, { path: '/billing', url: '/billing' });
    expect(screen.getByRole('button', { name: 'Τιμολόγιο INV000000003' })).toBeInTheDocument();
  });
});
