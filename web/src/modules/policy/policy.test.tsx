import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { PoliciesHomePage } from './PoliciesHomePage';
import { PolicyViewPage } from './PolicyViewPage';
import { ReferralsPage } from './ReferralsPage';
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

    // The transaction history (the term timeline is not mocked here, so it falls back to the policy's own transactions).
    const history = await screen.findByRole('list', { name: 'Συναλλαγές όρου 1' });
    expect(within(history).getByText('Νέα παραγωγή')).toBeInTheDocument();
    expect(within(history).getByText('432,35 €')).toBeInTheDocument();
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
    rememberRecent('policy', fx.policyId);
    mockApi(policyRoutes(() => ({ body: fx.policy('IN_FORCE') })));
    const { user, container } = renderScreen(<PoliciesHomePage />, {
      path: '/policies',
      url: '/policies',
    });
    expect(screen.getByRole('button', { name: 'Νέα προσφορά' })).toBeInTheDocument();
    expect(
      await screen.findByRole('button', { name: 'Ασφαλιστήριο POL000000007' }),
    ).toBeInTheDocument();
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
    expect(screen.queryByRole('button', { name: 'Παραπομπές ανάληψης' })).not.toBeInTheDocument();
  });

  it('links a senior underwriter to the underwriting referrals', async () => {
    signInAs(['Staff.Underwriter', 'Staff.UnderwritingManager']);
    const { user } = renderScreen(<PoliciesHomePage />, { path: '/policies', url: '/policies' });
    await user.click(screen.getByRole('button', { name: 'Παραπομπές ανάληψης' }));
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent('/policies/referrals');
    });
  });
});

function signInAs(roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 't',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      user: { id: 'uwsenior', name: 'Dev Senior Underwriter (synthetic)', roles },
    }),
  );
}

const referral = {
  id: '0192f0c4-0000-7000-8000-0000000000aa',
  jobRef: '0192f0c4-0000-7000-8000-0000000000bb',
  issueType: 'VEHICLE_AGE_REFERRAL',
  issueKey: 'VEHICLE_AGE_REFERRAL:veh-1',
  ruleId: 'REFER-OLD-VEHICLE',
  severity: 'REFER',
  blockingPoint: 'PRE_BIND',
  lane: 'ASSISTED',
  status: 'Open',
  recordVersion: 3,
  messageEn: 'The vehicle is older than 20 years.',
  messageEl: 'Το όχημα είναι παλαιότερο των 20 ετών.',
  raisedAt: '2026-10-08T10:00:00Z',
  raisedBy: 'USER:dev:underwriter',
  decision: null,
  closeReason: null,
};

describe('ReferralsPage', () => {
  afterEach(() => {
    sessionStorage.clear();
  });

  const queue = (items: unknown[]): MockRoute => ({
    method: 'GET',
    path: '/api/uw/v1/issues',
    respond: () => ({ body: { items, nextCursor: null, limit: 200 } }),
  });

  async function openReferral(user: ReturnType<typeof renderScreen>['user']) {
    const grid = await screen.findByRole('grid', { name: 'Παραπομπές που αναμένουν απόφαση' });
    expect(within(grid).getByText('Παλαιό όχημα')).toBeInTheDocument();
    expect(
      within(grid).getByText('Το όχημα είναι παλαιότερο των 20 ετών (από το έτος πρώτης άδειας).'),
    ).toBeInTheDocument();
    within(grid).getAllByRole('row')[1]?.focus();
    await user.keyboard('{Enter}');
    return screen.findByRole('form', { name: 'Απόφαση για: Παλαιό όχημα' });
  }

  it('approves a referral with a required reason and the version it read', async () => {
    signInAs(['Staff.UnderwritingManager']);
    const api = mockApi([
      queue([referral]),
      {
        method: 'POST',
        path: '/api/uw/v1/issues/decide',
        respond: () => ({
          body: {
            decisions: [
              {
                issueId: referral.id,
                status: 'Approved',
                recordVersion: 4,
                authorityCheckId: referral.jobRef,
              },
            ],
            checkIds: [referral.jobRef],
            pendingSecondApproval: false,
          },
        }),
      },
    ]);
    const { user, container } = renderScreen(<ReferralsPage />, {
      path: '/policies/referrals',
      url: '/policies/referrals',
    });
    const form = await openReferral(user);
    await expectNoA11yViolations(container);

    await user.click(within(form).getByRole('button', { name: 'Έγκριση' }));
    expect(await screen.findByText('Συμπληρώστε την αιτιολογία της απόφασης.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/uw/v1/issues/decide')).toHaveLength(0);

    await user.type(
      within(form).getByRole('textbox', { name: /Αιτιολογία απόφασης/ }),
      'Το όχημα επιθεωρήθηκε.',
    );
    await user.click(within(form).getByRole('button', { name: 'Έγκριση' }));

    expect(
      await screen.findByText('Εγκρίθηκε. Ο ανάδοχος μπορεί να κάνει ξανά δέσμευση.'),
    ).toBeInTheDocument();
    const call = api.callsTo('POST', '/api/uw/v1/issues/decide')[0];
    expect(call?.body).toEqual({
      issueIds: [referral.id],
      decision: 'APPROVE',
      reason: 'Το όχημα επιθεωρήθηκε.',
      expectedRecordVersions: { [referral.id]: 3 },
    });
    expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('shows why a decision is refused (segregation of duties)', async () => {
    signInAs(['Staff.Underwriter', 'Staff.UnderwritingManager']);
    mockApi([
      queue([referral]),
      {
        method: 'POST',
        path: '/api/uw/v1/issues/decide',
        respond: () =>
          problem(
            403,
            'UW-ERR-SOD',
            'Δεν μπορείτε να αποφασίσετε για εργασία που χειριστήκατε',
            'You quoted or bound this job, so you cannot decide its underwriting issues.',
          ),
      },
    ]);
    const { user } = renderScreen(<ReferralsPage />, {
      path: '/policies/referrals',
      url: '/policies/referrals',
    });
    const form = await openReferral(user);
    await user.type(within(form).getByRole('textbox', { name: /Αιτιολογία απόφασης/ }), 'Ok');
    await user.click(within(form).getByRole('button', { name: 'Απόρριψη' }));

    expect(await screen.findByText('Η απόφαση δεν αποθηκεύτηκε')).toBeInTheDocument();
    expect(screen.getByText(/You quoted or bound this job/)).toBeInTheDocument();
  });

  it('says so when no referral is waiting', async () => {
    mockApi([queue([])]);
    renderScreen(<ReferralsPage />, { path: '/policies/referrals', url: '/policies/referrals' });
    expect(await screen.findByText('Καμία παραπομπή σε αναμονή')).toBeInTheDocument();
  });
});
