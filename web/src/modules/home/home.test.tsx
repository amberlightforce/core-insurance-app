import { screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { InvoiceListPage } from '../../api/types';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, renderScreen } from '../../test/mockApi';
import { rememberRecent } from '../staff/recent';
import { HomePage } from './HomePage';
import { greetingKey, summariseInvoices } from './summary';

vi.setConfig({ testTimeout: 30_000 });

function signInAs(name: string, roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'dev', name, roles },
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
  localStorage.clear();
});

type Row = InvoiceListPage['items'][number];

const row = (
  number: string,
  state: Row['invoice']['state'],
  open: string,
  due: string,
  issue = '2026-10-01',
): Row => {
  const base = fx.invoiceList.items[0];
  if (!base) throw new Error('fixture');
  return {
    ...base,
    invoice: {
      ...base.invoice,
      invoiceId: number,
      invoiceNumber: number,
      state,
      issueDate: issue,
      dueDate: due,
      total: { amount: '100.00', currency: 'EUR' },
      paid: { amount: (100 - Number(open)).toFixed(2), currency: 'EUR' },
      open: { amount: open, currency: 'EUR' },
    },
  };
};

const invoices: InvoiceListPage = {
  items: [
    row('INV-A', 'PAID', '0.00', '2026-10-01', '2026-09-01'),
    row('INV-B', 'BILLED', '100.00', '2099-01-01', '2026-10-02'),
    row('INV-C', 'OVERDUE', '40.10', '2026-01-01', '2026-10-03'),
  ],
  nextCursor: null,
  limit: 50,
};

describe('summariseInvoices', () => {
  it('counts open, overdue and paid invoices and sums in cents', () => {
    const s = summariseInvoices(invoices.items, '2026-10-08');
    expect(s.count).toBe(3);
    expect(s.open.map((r) => r.invoice.invoiceNumber)).toEqual(['INV-C', 'INV-B']);
    expect(s.overdue.map((r) => r.invoice.invoiceNumber)).toEqual(['INV-C']);
    expect(s.paid).toHaveLength(1);
    expect(s.totalOpen).toBe(140.1);
    expect(s.totalBilled).toBe(300);
    expect(s.latest[0]?.invoice.invoiceNumber).toBe('INV-C');
  });

  it('greets by Athens time of day', () => {
    expect(greetingKey(new Date('2026-10-08T06:00:00Z'))).toBe('greeting.morning');
    expect(greetingKey(new Date('2026-10-08T12:00:00Z'))).toBe('greeting.evening');
  });
});

describe('HomePage', () => {
  it('asks a signed-out visitor to sign in', () => {
    renderScreen(<HomePage />, { path: '/', url: '/' });
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(/Καλ/);
    expect(screen.getByRole('button', { name: 'Σύνδεση' })).toBeInTheDocument();
  });

  it('shows an underwriter the real invoice figures, quick actions and recent records', async () => {
    signInAs('Dev Underwriter (synthetic)', ['Staff.Underwriter']);
    rememberRecent('policy', fx.policyId);
    const api = mockApi([
      { method: 'GET', path: '/api/bil/v1/invoices', respond: () => ({ body: invoices }) },
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => ({ body: fx.policy('IN_FORCE') }),
      },
    ]);
    const { container } = renderScreen(<HomePage />, { path: '/', url: '/' });

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(/, Dev$/);
    expect(screen.getByRole('button', { name: 'Νέα προσφορά' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Νέα δήλωση ζημίας' })).not.toBeInTheDocument();

    const cards = await screen.findByRole('region', { name: 'Εργασίες σε εκκρεμότητα' });
    const open = within(cards).getByRole('group', { name: 'Ανοιχτά τιμολόγια' });
    expect(await within(open).findByText('2 εκκρεμότητες')).toBeInTheDocument();
    expect(within(cards).getByRole('group', { name: 'Ληξιπρόθεσμες οφειλές' })).toHaveTextContent(
      'INV-C',
    );
    expect(screen.getAllByText('140,10 €').length).toBeGreaterThan(0);
    expect(await screen.findByRole('link', { name: 'POL000000007' })).toBeInTheDocument();
    // Only the opaque id is kept in the browser, never the policy number.
    expect(JSON.stringify(Object.entries(localStorage))).not.toContain('POL000000007');
    // The unfiltered list only: no ids or personal data in the query.
    expect(api.callsTo('GET', '/api/bil/v1/invoices')[0]?.url.searchParams.get('policyId')).toBe(
      null,
    );
    await expectNoA11yViolations(container);
  });

  it('shows a claims manager the pending approvals and the FNOL action', async () => {
    signInAs('Dev Claims Manager (synthetic)', ['Staff.ClaimsManager']);
    mockApi([
      {
        method: 'GET',
        path: '/api/plt/v1/approval',
        respond: () => ({
          body: {
            items: [
              {
                request: {
                  requestId: 'req-1',
                  type: 'CLM.PAYMENT',
                  status: 'PendingApproval',
                  requestedAt: '2026-10-07T09:00:00Z',
                },
              },
            ],
            nextCursor: null,
            limit: 50,
          },
        }),
      },
    ]);
    renderScreen(<HomePage />, { path: '/', url: '/' });
    expect(screen.getByRole('button', { name: 'Νέα δήλωση ζημίας' })).toBeInTheDocument();
    const approvals = await screen.findByRole('group', { name: 'Εγκρίσεις σε αναμονή' });
    expect(await within(approvals).findByText('07/10/2026')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Μεγέθη τιμολόγησης' })).not.toBeInTheDocument();
  });
});
