import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { PoliciesHomePage } from './PoliciesHomePage';
import { PolicyViewPage } from './PolicyViewPage';
import { rememberRecent } from '../staff/recent';

vi.setConfig({ testTimeout: 30_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const policyRoutes = (
  reply: (validAt: string | null) => { status?: number; body?: unknown },
): MockRoute[] => [
  {
    method: 'GET',
    path: `/api/pol/v1/policies/${fx.policyId}`,
    respond: (r) => reply(r.url.searchParams.get('validAt')),
  },
  { method: 'GET', path: '/api/bil/v1/invoices', respond: () => ({ body: fx.invoiceList }) },
];
const view = () =>
  renderScreen(<PolicyViewPage />, {
    path: '/policies/:policyId',
    url: `/policies/${fx.policyId}`,
  });

describe('PolicyViewPage', () => {
  it('shows number, status, term, covers, the issuance transaction, charge lines and invoices', async () => {
    const api = mockApi(policyRoutes(() => ({ body: fx.policy('IN_FORCE') })));
    const { container } = view();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Ασφαλιστήριο POL000000007' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Σε ισχύ').length).toBeGreaterThan(0);

    const term = screen.getByRole('region', { name: 'Περίοδος ασφάλισης' });
    expect(within(term).getByText('08/10/2026')).toBeInTheDocument();
    expect(within(term).getByText('08/10/2027')).toBeInTheDocument();
    expect(within(term).getByText('ANNUAL')).toBeInTheDocument();

    const covers = screen.getByRole('region', { name: 'Καλύψεις' });
    expect(within(covers).getByText('MTPL')).toBeInTheDocument();
    expect(within(covers).getByText('OWN-DAMAGE')).toBeInTheDocument();
    expect(within(covers).queryByText('WINDSCREEN')).not.toBeInTheDocument();

    const transactions = screen.getByRole('grid', { name: 'Συναλλαγές ασφαλιστηρίου' });
    expect(within(transactions).getByText('NEW_BUSINESS')).toBeInTheDocument();
    expect(within(transactions).getByText('479,20 €')).toBeInTheDocument();
    const charges = screen.getByRole('grid', { name: 'Γραμμές χρέωσης' });
    expect(within(charges).getByText('PREM-MTPL')).toBeInTheDocument();
    expect(within(charges).getByText('GR-IPT')).toBeInTheDocument();
    expect(within(charges).getByText(/Προσωρινό/)).toBeInTheDocument();
    const invoices = await screen.findByRole('grid', { name: 'Τιμολόγια ασφαλιστηρίου' });
    expect(within(invoices).getByText('INV000000003')).toBeInTheDocument();
    expect(within(invoices).getByText(/Δοκιμαστικό \(stub\)/)).toBeInTheDocument();

    // The as-of date goes out in date form (D-SLC-13).
    expect(
      api.callsTo('GET', `/api/pol/v1/policies/${fx.policyId}`)[0]?.url.searchParams.get('validAt'),
    ).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(api.callsTo('GET', '/api/bil/v1/invoices')[0]?.url.searchParams.get('policyId')).toBe(
      fx.policyId,
    );
    await expectNoA11yViolations(container);
  });

  it('re-reads the policy as of a chosen date (validAt) and shows the status at that date', async () => {
    const api = mockApi(
      policyRoutes((validAt) => ({
        body: fx.policy(validAt === '2027-10-09' ? 'EXPIRED' : 'IN_FORCE'),
      })),
    );
    const { user } = view();
    await screen.findByRole('heading', { level: 1, name: 'Ασφαλιστήριο POL000000007' });

    const picker = screen.getByRole('group', { name: /Κατάσταση κατά/ });
    const [firstField] = within(picker).getAllByRole('spinbutton');
    if (!firstField) throw new Error('no date field');
    await user.click(firstField);
    await user.keyboard('09102027');
    await waitFor(() => {
      expect(
        api
          .callsTo('GET', `/api/pol/v1/policies/${fx.policyId}`)
          .some((c) => c.url.searchParams.get('validAt') === '2027-10-09'),
      ).toBe(true);
    });
    await waitFor(() => {
      expect(screen.getAllByText('Έληξε').length).toBeGreaterThan(0);
    });
    expect(screen.getByText('Κατάσταση κατά 09/10/2027')).toBeInTheDocument();
  });

  it('shows a scheduled policy and the no-term state', async () => {
    mockApi(policyRoutes(() => ({ body: { ...fx.policy('SCHEDULED'), term: undefined } })));
    view();
    expect(await screen.findByText('Δεν υπάρχει περίοδος ασφάλισης')).toBeInTheDocument();
    expect(screen.getAllByText('Σε αναμονή έναρξης').length).toBeGreaterThan(0);
  });

  it('a policy that has not started yet is read as of the start of its upcoming term, with a note', async () => {
    // The term starts far in the future (in Athens: 2099-01-10), so «today» is always before it.
    const future = (state: 'SCHEDULED' | 'IN_FORCE') => {
      const base = fx.policy(state);
      return {
        ...base,
        term: base.term && {
          ...base.term,
          period: { from: '2099-01-10T00:00:00+02:00', to: '2100-01-10T00:00:00+02:00' },
        },
      };
    };
    const api = mockApi(
      policyRoutes((validAt) => ({
        // Today the policy has no segment; at the term start the covers are visible.
        body:
          validAt === '2099-01-10'
            ? future('IN_FORCE')
            : { ...future('SCHEDULED'), riskTree: undefined, charges: [] },
      })),
    );
    view();
    expect(await screen.findByText('Δεν έχει αρχίσει ακόμη')).toBeInTheDocument();
    const covers = await screen.findByRole('region', { name: 'Καλύψεις' });
    await waitFor(() => {
      expect(within(covers).getByText('MTPL')).toBeInTheDocument();
    });
    expect(screen.getByText('Κατάσταση κατά 10/01/2099')).toBeInTheDocument();
    expect(
      api
        .callsTo('GET', `/api/pol/v1/policies/${fx.policyId}`)
        .some((c) => c.url.searchParams.get('validAt') === '2099-01-10'),
    ).toBe(true);
  });

  it('shows a «no permission» state, not an endless loading, when the invoices are forbidden (403)', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => ({ body: fx.policy() }),
      },
      {
        method: 'GET',
        path: '/api/bil/v1/invoices',
        respond: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden'),
      },
    ]);
    view();
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
    expect(screen.queryByText('Φόρτωση…')).not.toBeInTheDocument();
  });

  it('shows not-found, error with retry, and empty invoices', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => problem(404, 'POL-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    view();
    expect(await screen.findByText('Το ασφαλιστήριο δεν βρέθηκε')).toBeInTheDocument();
  });

  it('shows the empty invoice state while billing has not issued one yet', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => ({ body: fx.policy() }),
      },
      {
        method: 'GET',
        path: '/api/bil/v1/invoices',
        respond: () => ({ body: { items: [], nextCursor: null, limit: 50 } }),
      },
    ]);
    view();
    expect(await screen.findByText('Δεν υπάρχουν τιμολόγια')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    mockApi(policyRoutes(() => ({ body: fx.policy() })));
    renderScreen(<PolicyViewPage />, {
      path: '/policies/:policyId',
      url: `/policies/${fx.policyId}`,
    });
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Policy POL000000007' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('In force').length).toBeGreaterThan(0);
  });
});

describe('PoliciesHomePage', () => {
  it('offers a new quote, recent policies and an id lookup that validates the UUID', async () => {
    rememberRecent('policy', { id: fx.policyId, label: 'POL000000007' });
    const { user, container } = renderScreen(<PoliciesHomePage />, {
      path: '/policies',
      url: '/policies',
    });
    expect(screen.getByRole('button', { name: 'Νέα προσφορά' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ασφαλιστήριο POL000000007' })).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.type(
      screen.getByRole('textbox', { name: /Αναγνωριστικό ασφαλιστηρίου/ }),
      'not-a-uuid{Enter}',
    );
    expect(await screen.findByText('Δώστε έγκυρο αναγνωριστικό UUID.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Ασφαλιστήριο POL000000007' }));
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`/policies/${fx.policyId}`);
    });
  });

  it('starts empty on first use', () => {
    renderScreen(<PoliciesHomePage />, { path: '/policies', url: '/policies' });
    expect(screen.getByText('Δεν υπάρχουν πρόσφατα ασφαλιστήρια')).toBeInTheDocument();
  });
});
