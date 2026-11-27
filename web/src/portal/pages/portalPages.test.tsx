import { screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../test/axe';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { BillingPage } from './BillingPage';
import { CancellationPage } from './CancellationPage';
import { DashboardPage } from './DashboardPage';
import { DocumentsPage } from './DocumentsPage';
import { MessagesPage } from './MessagesPage';
import { NotificationsPage } from './NotificationsPage';
import { PoliciesPage } from './PoliciesPage';
import { PolicyDetailPage } from './PolicyDetailPage';
import { ProfilePage } from './ProfilePage';
import { RenewalPage } from './RenewalPage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const api = '/api/chn/v1';
const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
const jobId = '1a1a1a1a-1111-4222-8333-444444444444';
const eur = (amount: string) => ({ amount, currency: 'EUR' });

const policy = {
  policyId,
  policyNumber: 'POL000123',
  productCode: 'MOTOR',
  status: 'InForce',
  ownership: 'OWN',
  termNumber: 1,
  termStart: '2026-01-01T00:00:00Z',
  termEnd: '2026-12-31T21:59:59Z',
  currency: 'EUR',
  paymentPlan: 'ANNUAL',
  coverages: [
    { coverageCode: 'MTPL', selected: true },
    { coverageCode: 'GLASS', selected: false },
  ],
  vehicles: [{ plate: 'IKA1234', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2019 }],
  premium: [{ category: 'NET', amount: eur('120') }],
  renewalJobId: jobId,
};

const get = (path: string, body: unknown, status = 200): MockRoute => ({
  method: 'GET',
  path,
  respond: () => ({ status, body }),
});
const post = (path: string, body: unknown, status = 200): MockRoute => ({
  method: 'POST',
  path,
  respond: () => ({ status, body }),
});

describe('portal Overview', () => {
  const dashboard = {
    policyCount: 2,
    activePolicyCount: 2,
    renewalsDue: [
      {
        policyId,
        policyNumber: 'POL000123',
        productCode: 'MOTOR',
        expiresAt: '2026-12-31T21:59:59Z',
        daysLeft: 24,
        renewalJobId: jobId,
      },
    ],
    unpaidInvoices: [
      {
        invoiceId: 'a1',
        invoiceNumber: 'INV000001',
        policyId,
        dueDate: '2026-12-01',
        open: eur('55.10'),
        overdue: true,
      },
    ],
    unreadNotificationCount: 3,
    openRequests: [{ threadId: 't1', topic: 'BILLING', lastMessageAt: '2026-11-20T10:00:00Z' }],
  };

  it('shows the work left from the real dashboard and links to the renewal', async () => {
    mockApi([get(`${api}/portal/dashboard`, dashboard)]);
    const { container } = renderScreen(<DashboardPage />, { path: '/p', url: '/p' });
    await screen.findByText('Ανανέωση του ασφαλιστηρίου POL000123');
    expect(screen.getByText('Παραστατικό INV000001')).toBeInTheDocument();
    expect(screen.getByText('Ανοιχτό αίτημα: Πληρωμές')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Δείτε την πρόταση' })).toHaveAttribute(
      'href',
      `/portal/policies/${policyId}/renew`,
    );
    await expectNoA11yViolations(container);
  });

  it('is empty for a customer without policies', async () => {
    mockApi([
      get(`${api}/portal/dashboard`, {
        ...dashboard,
        policyCount: 0,
        activePolicyCount: 0,
        renewalsDue: [],
        unpaidInvoices: [],
        openRequests: [],
        unreadNotificationCount: 0,
      }),
    ]);
    renderScreen(<DashboardPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν υπάρχουν ακόμη ασφαλιστήρια');
  });

  it('shows a quiet 403 without a retry and a retry for a server error', async () => {
    mockApi([
      {
        method: 'GET',
        path: `${api}/portal/dashboard`,
        respond: () => problem(403, 'FORBIDDEN', 'Forbidden'),
      },
    ]);
    const first = renderScreen(<DashboardPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν φορτώθηκε η επισκόπηση');
    expect(screen.queryByRole('button', { name: 'Δοκιμάστε ξανά' })).toBeNull();
    first.unmount();
    mockApi([
      {
        method: 'GET',
        path: `${api}/portal/dashboard`,
        respond: () => problem(500, 'INTERNAL', 'Boom'),
      },
    ]);
    renderScreen(<DashboardPage />, { path: '/p', url: '/p' });
    expect(await screen.findByRole('button', { name: 'Δοκιμάστε ξανά' })).toBeInTheDocument();
  });
});

describe('portal Policies', () => {
  it('lists policies and is empty for none', async () => {
    mockApi([
      get(`${api}/portal/policies`, {
        items: [
          {
            policyId,
            policyNumber: 'POL000123',
            productCode: 'MOTOR',
            status: 'InForce',
            termStart: '2026-01-01T00:00:00Z',
            termEnd: '2026-12-31T21:59:59Z',
            ownership: 'OWN',
          },
        ],
        nextCursor: null,
      }),
    ]);
    const { container, unmount } = renderScreen(<PoliciesPage />, { path: '/p', url: '/p' });
    expect(await screen.findByText('POL000123')).toBeInTheDocument();
    expect(screen.getByText('Σε ισχύ')).toBeInTheDocument();
    await expectNoA11yViolations(container);
    unmount();
    mockApi([get(`${api}/portal/policies`, { items: [], nextCursor: null })]);
    renderScreen(<PoliciesPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν βρέθηκαν ασφαλιστήρια');
  });

  it('shows not found for a policy that is not the callers (404)', async () => {
    mockApi([
      {
        method: 'GET',
        path: `${api}/portal/policies/${policyId}`,
        respond: () => problem(404, 'CHN-ERR-NOT-FOUND', 'Not found'),
      },
    ]);
    renderScreen(<PolicyDetailPage />, { path: '/p/:id', url: `/p/${policyId}` });
    await screen.findByText('Το ασφαλιστήριο δεν βρέθηκε');
  });

  it('previews a cover change, requires the acknowledgement, then confirms', async () => {
    const mock = mockApi([
      get(`${api}/portal/policies/${policyId}`, policy),
      post(`${api}/changes/preview`, {
        changeId: 'c1',
        policyId,
        jobId,
        versionNo: 1,
        effectiveAt: '2026-12-01T00:00:00Z',
        outcome: 'ALLOW',
        reasons: [],
        premiumChange: eur('10'),
        taxChange: eur('2'),
        totalChange: eur('12'),
        additionalDue: eur('12'),
      }),
      post(`${api}/changes/confirm`, { changeId: 'c1', policyId, outcome: 'CONFIRMED' }),
    ]);
    const { container, user } = renderScreen(<PolicyDetailPage />, {
      path: '/p/:id',
      url: `/p/${policyId}`,
    });
    await screen.findByRole('heading', { level: 1, name: 'Ασφαλιστήριο POL000123' });
    await expectNoA11yViolations(container);
    const section = screen.getByRole('region', { name: 'Αλλαγή καλύψεων' });
    await user.click(within(section).getByRole('checkbox', { name: 'GLASS' }));
    await user.click(within(section).getByRole('button', { name: 'Δείτε το κόστος της αλλαγής' }));
    await within(section).findByText('Συνολική μεταβολή');
    const preview = mock.callsTo('POST', `${api}/changes/preview`)[0];
    expect(preview?.body).toMatchObject({
      policyId,
      instructions: [
        {
          op: 'SET_COVERAGES',
          coverages: [
            { coverageCode: 'MTPL', selected: true },
            { coverageCode: 'GLASS', selected: true },
          ],
        },
      ],
    });
    expect(preview?.headers.get('Idempotency-Key')).toBeTruthy();
    // Confirming before the acknowledgement does nothing.
    await user.click(within(section).getByRole('button', { name: 'Επιβεβαίωση αλλαγής' }));
    expect(mock.callsTo('POST', `${api}/changes/confirm`)).toHaveLength(0);
    await user.click(within(section).getByRole('checkbox', { name: /Έχω δει το κόστος/ }));
    await user.click(within(section).getByRole('button', { name: 'Επιβεβαίωση αλλαγής' }));
    await within(section).findByText('Η αλλαγή έγινε');
    expect(mock.callsTo('POST', `${api}/changes/confirm`)[0]?.body).toEqual({
      changeId: 'c1',
      acknowledged: true,
    });
  });

  it('sends a change the matrix refers to the team', async () => {
    const mock = mockApi([
      get(`${api}/portal/policies/${policyId}`, policy),
      post(`${api}/changes/preview`, {
        changeId: 'c2',
        policyId,
        jobId,
        versionNo: 1,
        effectiveAt: '2026-12-01T00:00:00Z',
        outcome: 'REFER',
        reasons: ['REFER'],
        premiumChange: eur('10'),
        taxChange: eur('2'),
        totalChange: eur('12'),
      }),
      post(`${api}/changes/refer`, { changeId: 'c2', policyId, outcome: 'REFERRED' }),
    ]);
    const { user } = renderScreen(<PolicyDetailPage />, { path: '/p/:id', url: `/p/${policyId}` });
    await screen.findByRole('heading', { level: 1, name: 'Ασφαλιστήριο POL000123' });
    const section = screen.getByRole('region', { name: 'Αλλαγή καλύψεων' });
    await user.click(within(section).getByRole('checkbox', { name: 'GLASS' }));
    await user.click(within(section).getByRole('button', { name: 'Δείτε το κόστος της αλλαγής' }));
    await user.click(
      await within(section).findByRole('button', { name: 'Αποστολή στην ομάδα μας' }),
    );
    await within(section).findByText('Το αίτημα στάλθηκε στην ομάδα μας');
    expect(mock.callsTo('POST', `${api}/changes/refer`)).toHaveLength(1);
  });
});

describe('portal Renewal and Cancellation', () => {
  it('accepts the offered renewal with the job id of the policy', async () => {
    const mock = mockApi([
      get(`${api}/portal/policies/${policyId}`, policy),
      post(`${api}/portal/renewals/accept`, {
        policyId,
        jobId,
        outcome: 'ACCEPTED',
        newTermNumber: 2,
      }),
    ]);
    const { container, user } = renderScreen(<RenewalPage />, {
      path: '/p/:id/renew',
      url: `/p/${policyId}/renew`,
    });
    await screen.findByText('Αποδοχή της ανανέωσης');
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('checkbox', { name: /Διάβασα την πρόταση/ }));
    await user.click(screen.getByRole('button', { name: 'Ανανέωση του ασφαλιστηρίου' }));
    await screen.findByText('Η ανανέωση έγινε δεκτή');
    expect(mock.callsTo('POST', `${api}/portal/renewals/accept`)[0]?.body).toEqual({
      policyId,
      jobId,
      acknowledged: true,
    });
  });

  it('says there is no offer yet instead of inventing one', async () => {
    const { renewalJobId: _unused, ...withoutOffer } = policy;
    void _unused;
    mockApi([get(`${api}/portal/policies/${policyId}`, withoutOffer)]);
    renderScreen(<RenewalPage />, { path: '/p/:id/renew', url: `/p/${policyId}/renew` });
    await screen.findByText('Η πρότασή μας δεν είναι ακόμη έτοιμη');
    expect(screen.queryByRole('button', { name: 'Ανανέωση του ασφαλιστηρίου' })).toBeNull();
  });

  it('shows a referred renewal as sent to the team', async () => {
    mockApi([
      get(`${api}/portal/policies/${policyId}`, policy),
      post(`${api}/portal/renewals/accept`, {
        policyId,
        jobId,
        outcome: 'REFERRED',
        ticketId: 't',
      }),
    ]);
    const { user } = renderScreen(<RenewalPage />, {
      path: '/p/:id/renew',
      url: `/p/${policyId}/renew`,
    });
    await user.click(await screen.findByRole('checkbox', { name: /Διάβασα την πρόταση/ }));
    await user.click(screen.getByRole('button', { name: 'Ανανέωση του ασφαλιστηρίου' }));
    await screen.findByText('Το αίτημα στάλθηκε στην ομάδα μας');
  });

  it('requests a cancellation and does not claim the policy is cancelled', async () => {
    const mock = mockApi([
      get(`${api}/portal/policies/${policyId}`, policy),
      post(`${api}/portal/cancellations/request`, {
        policyId,
        outcome: 'REQUESTED',
        ticketId: 't',
      }),
    ]);
    const { container, user } = renderScreen(<CancellationPage />, {
      path: '/p/:id/cancel',
      url: `/p/${policyId}/cancel`,
    });
    await screen.findByRole('button', { name: 'Αποστολή αιτήματος' });
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Αποστολή αιτήματος' }));
    await screen.findByText('Επιλέξτε λόγο.');
    expect(mock.callsTo('POST', `${api}/portal/cancellations/request`)).toHaveLength(0);
    await user.click(screen.getByRole('button', { name: /Λόγος ακύρωσης/ }));
    await user.click(await screen.findByRole('option', { name: 'Το κόστος' }));
    await user.click(screen.getByRole('button', { name: 'Αποστολή αιτήματος' }));
    await screen.findByText('Το αίτημά σας στάλθηκε');
    expect(screen.getByText(/δεν έχει ακυρωθεί ακόμη/)).toBeInTheDocument();
    expect(mock.callsTo('POST', `${api}/portal/cancellations/request`)[0]?.body).toMatchObject({
      policyId,
      reasonCode: 'PRICE',
    });
  });
});

describe('portal Payments', () => {
  const summary = {
    accounts: [
      {
        billingAccountId: 'b1',
        accountNumber: 'BA0001',
        currency: 'EUR',
        billed: eur('500'),
        overdue: eur('55.10'),
        unapplied: eur('0'),
        credit: eur('0'),
      },
    ],
    openInvoiceCount: 1,
    overdueInvoiceCount: 1,
  };
  const invoice = {
    invoiceId: 'a1',
    invoiceNumber: 'INV000001',
    policyId,
    policyNumber: 'POL000123',
    issueDate: '2026-11-01',
    dueDate: '2026-12-01',
    state: 'OVERDUE',
    total: eur('100'),
    paid: eur('44.90'),
    open: eur('55.10'),
    overdue: true,
    payable: true,
  };

  it('shows balances, invoices and refunds, and never fakes a payment', async () => {
    const mock = mockApi([
      get(`${api}/portal/billing/summary`, summary),
      get(`${api}/portal/billing/invoices`, { items: [invoice], nextCursor: null }),
      get(`${api}/portal/billing/refunds`, {
        items: [
          {
            refundId: 'r1',
            state: 'PROPOSED',
            amount: eur('20'),
            proposedAt: '2026-11-10T10:00:00Z',
          },
        ],
      }),
      post(`${api}/portal/billing/invoices/a1/pay-now`, {
        invoiceId: 'a1',
        decision: 'ALLOW',
        payable: true,
        payUrl: 'https://pay.example/never-opened',
        reasons: [],
      }),
    ]);
    const { container, user } = renderScreen(<BillingPage />, { path: '/p', url: '/p' });
    await screen.findByText('INV000001');
    expect(screen.getAllByText('Εκπρόθεσμο').length).toBeGreaterThan(0);
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Πληρωμή τώρα' }));
    await screen.findByText('Η ηλεκτρονική πληρωμή έρχεται σύντομα');
    expect(screen.getByText(/Δεν έγινε καμία χρέωση/)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /pay\.example/ })).toBeNull();
    expect(
      mock
        .callsTo('POST', `${api}/portal/billing/invoices/a1/pay-now`)[0]
        ?.headers.get('Idempotency-Key'),
    ).toBeTruthy();
  });

  it('shows a referred decision as sent to the team and a deny as not possible', async () => {
    mockApi([
      get(`${api}/portal/billing/summary`, summary),
      get(`${api}/portal/billing/invoices`, { items: [invoice], nextCursor: null }),
      get(`${api}/portal/billing/refunds`, { items: [] }),
      post(`${api}/portal/billing/invoices/a1/pay-now`, {
        invoiceId: 'a1',
        decision: 'REFER',
        payable: false,
        reasons: ['x'],
      }),
    ]);
    const { user } = renderScreen(<BillingPage />, { path: '/p', url: '/p' });
    await user.click(await screen.findByRole('button', { name: 'Πληρωμή τώρα' }));
    await screen.findByText('Το αίτημα στάλθηκε στην ομάδα μας');
  });

  it('is empty without invoices and reports a 403 quietly', async () => {
    mockApi([
      get(`${api}/portal/billing/summary`, {
        accounts: [],
        openInvoiceCount: 0,
        overdueInvoiceCount: 0,
      }),
      get(`${api}/portal/billing/invoices`, { items: [], nextCursor: null }),
      get(`${api}/portal/billing/refunds`, { items: [] }),
    ]);
    const first = renderScreen(<BillingPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν υπάρχουν παραστατικά');
    first.unmount();
    mockApi([
      {
        method: 'GET',
        path: /\/portal\/billing\//,
        respond: () => problem(403, 'FORBIDDEN', 'Forbidden'),
      },
    ]);
    renderScreen(<BillingPage />, { path: '/p', url: '/p' });
    await screen.findAllByText('Δεν φορτώθηκαν οι πληρωμές');
    expect(screen.queryByRole('button', { name: 'Δοκιμάστε ξανά' })).toBeNull();
  });
});

describe('portal Documents', () => {
  it('lists documents, filters by policy and downloads through the API', async () => {
    const createUrl = vi.fn(() => 'blob:x');
    vi.stubGlobal(
      'URL',
      Object.assign(URL, { createObjectURL: createUrl, revokeObjectURL: vi.fn() }),
    );
    const mock = mockApi([
      get(`${api}/portal/documents`, {
        items: [
          {
            documentId: 'd1',
            documentNumber: 'DOC000001',
            documentType: 'POLICY_SCHEDULE',
            policyId,
            createdAt: '2026-11-01T10:00:00Z',
            openCount: 0,
          },
        ],
        nextCursor: null,
      }),
      {
        method: 'GET',
        path: `${api}/portal/documents/d1/download`,
        respond: () => ({ body: { ok: true } }),
      },
    ]);
    const { container, user } = renderScreen(<DocumentsPage />, {
      path: '/p',
      url: `/p?policy=${policyId}`,
    });
    await screen.findByText('DOC000001');
    expect(
      mock.callsTo('GET', `${api}/portal/documents`)[0]?.url.searchParams.get('policyId'),
    ).toBe(policyId);
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Λήψη του εγγράφου DOC000001' }));
    await vi.waitFor(() => {
      expect(mock.callsTo('GET', `${api}/portal/documents/d1/download`)).toHaveLength(1);
      expect(createUrl).toHaveBeenCalled();
    });
  });

  it('is empty without documents', async () => {
    mockApi([get(`${api}/portal/documents`, { items: [], nextCursor: null })]);
    renderScreen(<DocumentsPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν υπάρχουν έγγραφα');
  });
});

describe('portal Messages', () => {
  const thread = {
    threadId: 't1',
    topic: 'BILLING',
    lastMessageAt: '2026-11-20T10:00:00Z',
    awaitingStaff: true,
    messages: [
      {
        messageId: 'm1',
        direction: 'CUSTOMER',
        text: 'Πότε πληρώνεται;',
        sentAt: '2026-11-20T10:00:00Z',
      },
      {
        messageId: 'm2',
        direction: 'STAFF',
        text: 'Έως 1 Δεκεμβρίου.',
        sentAt: '2026-11-20T11:00:00Z',
      },
    ],
  };

  it('shows threads and sends a reply on the thread', async () => {
    const mock = mockApi([
      get(`${api}/messages`, { items: [{ thread }], nextCursor: null }),
      post(`${api}/messages/send`, { thread }),
    ]);
    const { container, user } = renderScreen(<MessagesPage />, { path: '/p', url: '/p' });
    await screen.findByText('Έως 1 Δεκεμβρίου.');
    await expectNoA11yViolations(container);
    const reply = screen.getByRole('form', { name: 'Απάντηση' });
    await user.type(within(reply).getByLabelText('Η απάντησή σας'), 'Ευχαριστώ');
    await user.click(within(reply).getByRole('button', { name: 'Αποστολή απάντησης' }));
    await vi.waitFor(() => {
      expect(mock.callsTo('POST', `${api}/messages/send`)).toHaveLength(1);
    });
    expect(mock.callsTo('POST', `${api}/messages/send`)[0]?.body).toEqual({
      threadId: 't1',
      text: 'Ευχαριστώ',
    });
  });

  it('is empty and starts a new thread with a topic', async () => {
    const mock = mockApi([
      get(`${api}/messages`, { items: [], nextCursor: null }),
      post(`${api}/messages/send`, { thread }),
    ]);
    const { user } = renderScreen(<MessagesPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν έχετε μηνύματα ακόμη');
    await user.click(screen.getByRole('button', { name: 'Αποστολή' }));
    await screen.findByText('Γράψτε ένα μήνυμα.');
    await user.type(screen.getByLabelText('Το μήνυμά σας'), 'Γεια σας');
    await user.click(screen.getByRole('button', { name: 'Αποστολή' }));
    await vi.waitFor(() => {
      expect(mock.callsTo('POST', `${api}/messages/send`)).toHaveLength(1);
    });
    expect(mock.callsTo('POST', `${api}/messages/send`)[0]?.body).toEqual({
      topic: 'GENERAL',
      text: 'Γεια σας',
    });
  });
});

describe('portal Notifications', () => {
  it('lists notifications, marks one read and shows the preferences', async () => {
    const mock = mockApi([
      get(`${api}/notifications`, {
        items: [
          {
            notificationId: 'n1',
            kind: 'INVOICE_ISSUED',
            notificationClass: 'BILLING',
            createdAt: '2026-11-20T10:00:00Z',
          },
          {
            notificationId: 'n2',
            kind: 'DOCUMENT_READY',
            notificationClass: 'DOCUMENT',
            createdAt: '2026-11-19T10:00:00Z',
            readAt: '2026-11-19T12:00:00Z',
          },
        ],
        nextCursor: null,
        unreadCount: 1,
      }),
      get(`${api}/notification-subscriptions`, {
        subscriptions: [{ notificationClass: 'BILLING', enabled: false }],
      }),
      post(`${api}/notifications/mark-read`, { updated: 1, unreadCount: 0 }),
    ]);
    const { container, user } = renderScreen(<NotificationsPage />, { path: '/p', url: '/p' });
    await screen.findByText('Εκδόθηκε νέο παραστατικό');
    expect(screen.getByRole('switch', { name: 'Πληρωμές και παραστατικά' })).not.toBeChecked();
    expect(screen.getByRole('switch', { name: 'Ανανεώσεις' })).toBeChecked();
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: /Σήμανση ως διαβασμένη/ }));
    await vi.waitFor(() => {
      expect(mock.callsTo('POST', `${api}/notifications/mark-read`)).toHaveLength(1);
    });
    expect(mock.callsTo('POST', `${api}/notifications/mark-read`)[0]?.body).toEqual({
      notificationIds: ['n1'],
    });
  });

  it('is empty without notifications', async () => {
    mockApi([
      get(`${api}/notifications`, { items: [], nextCursor: null, unreadCount: 0 }),
      get(`${api}/notification-subscriptions`, { subscriptions: [] }),
    ]);
    renderScreen(<NotificationsPage />, { path: '/p', url: '/p' });
    await screen.findByText('Δεν υπάρχουν ειδοποιήσεις');
  });
});

describe('portal Profile', () => {
  const contact = {
    recordVersion: 4,
    addresses: [
      {
        addressId: 'ad1',
        types: ['MAILING'],
        primary: true,
        country: 'GR',
        street: 'Ερμού',
        number: '10',
        postcode: '10563',
        locality: 'Αθήνα',
        formattedLines: ['Ερμού 10', '10563 Αθήνα'],
      },
    ],
    contactPoints: [
      {
        contactPointId: 'cp1',
        type: 'MOBILE',
        value: '+306900000000',
        primary: true,
        verified: true,
      },
    ],
  };

  it('replaces a contact point with the expected record version', async () => {
    const mock = mockApi([
      get(`${api}/portal/contact`, contact),
      post(`${api}/portal/contact/update`, {
        outcome: 'APPLIED',
        action: 'REPLACE',
        kind: 'CONTACT_POINT',
        recordVersion: 5,
      }),
    ]);
    const { container, user } = renderScreen(<ProfilePage />, { path: '/p', url: '/p' });
    await screen.findByText('+306900000000');
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Αλλαγή: +306900000000' }));
    const field = screen.getByLabelText('Τηλέφωνο ή e-mail');
    await user.clear(field);
    await user.type(field, '+306911111111');
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση' }));
    await screen.findByText('Τα στοιχεία σας ενημερώθηκαν');
    expect(mock.callsTo('POST', `${api}/portal/contact/update`)[0]?.body).toMatchObject({
      action: 'REPLACE',
      kind: 'CONTACT_POINT',
      locationId: 'cp1',
      expectedRecordVersion: 4,
      contactPoint: { type: 'MOBILE', value: '+306911111111' },
    });
  });

  it('refetches and explains a stale record (409)', async () => {
    let reads = 0;
    mockApi([
      {
        method: 'GET',
        path: `${api}/portal/contact`,
        respond: () => {
          reads += 1;
          return { body: { ...contact, recordVersion: 3 + reads } };
        },
      },
      {
        method: 'POST',
        path: `${api}/portal/contact/update`,
        respond: () => problem(409, 'PTY-ERR-STALE', 'Stale'),
      },
    ]);
    const { user } = renderScreen(<ProfilePage />, { path: '/p', url: '/p' });
    await user.click(await screen.findByRole('button', { name: 'Αλλαγή: +306900000000' }));
    await user.type(screen.getByLabelText('Τηλέφωνο ή e-mail'), '1');
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση' }));
    await screen.findByText('Τα στοιχεία σας άλλαξαν στο μεταξύ');
    await vi.waitFor(() => {
      expect(reads).toBeGreaterThanOrEqual(2);
    });
  });

  it('shows a referred update as sent to the team', async () => {
    mockApi([
      get(`${api}/portal/contact`, contact),
      post(`${api}/portal/contact/update`, {
        outcome: 'REFERRED',
        action: 'REMOVE',
        kind: 'ADDRESS',
        ticketId: 't',
      }),
    ]);
    const { user } = renderScreen(<ProfilePage />, { path: '/p', url: '/p' });
    await user.click(await screen.findByRole('button', { name: /Διαγραφή: Ερμού 10/ }));
    await user.click(screen.getByRole('button', { name: 'Ναι, διαγραφή' }));
    await screen.findByText('Το αίτημα στάλθηκε στην ομάδα μας');
  });
});
