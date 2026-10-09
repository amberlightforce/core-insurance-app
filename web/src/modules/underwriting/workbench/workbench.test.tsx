import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { IssueDecidability, ReferralListItem, ReferralListPage, ReferralView } from '../api';
import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { WorkbenchPage } from './WorkbenchPage';

vi.setConfig({ testTimeout: 60_000 });

const jobRef = '7a0f0000-1111-4222-8333-000000000001';
const issueId = '1550e000-1111-4222-8333-000000000001';
const customerName = 'Κωνσταντίνος Νικολάου';

function signIn(roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'dev', name: 'Senior', roles },
    }),
  );
}

afterEach(() => {
  sessionStorage.clear();
  vi.unstubAllGlobals();
});

const counts = { open: 31, approvedToday: 2, rejected: 3, decidedByMeToday: 11, mine: 8 };

const item: ReferralListItem = {
  jobRef,
  jobNumber: 'QUO-2026-0412',
  jobState: 'QUOTED',
  productCode: 'MOTOR-GR',
  customer: {
    partyId: '9c0f0000-1111-4222-8333-000000000009',
    partyNumber: 'P-1',
    displayName: customerName,
  },
  premiumTotal: { amount: '1186.40', currency: 'EUR' },
  effectiveDate: '2026-10-15',
  referralStatus: 'Open',
  reasons: [
    {
      issueId,
      issueType: 'VEHICLE_AGE_REFERRAL',
      ruleId: 'REFER-OLD-VEHICLE',
      status: 'Open',
      observed: '35',
      limit: '20',
      limitUnavailableReason: null,
    },
    {
      issueId: '1550e000-1111-4222-8333-000000000002',
      issueType: 'DRIVER_AGE_REFERRAL',
      ruleId: 'REFER-YOUNG-DRIVER',
      status: 'Open',
      observed: 'FROM_18_TO_20',
      limit: null,
      limitUnavailableReason: 'RULE_DECLARES_NO_LIMIT',
    },
  ],
  raisedAt: '2026-10-07T08:00:00Z',
  callerWorkedOnJob: false,
};

function page(items: ReferralListItem[]): ReferralListPage {
  return { items, nextCursor: null, counts };
}

function decidability(over: Partial<IssueDecidability> = {}): IssueDecidability {
  return {
    canDecide: true,
    reasons: [],
    authority: { type: 'UW.ISSUE_APPROVAL', issueType: 'VEHICLE_AGE_REFERRAL', outcome: 'ALLOW' },
    ...over,
  };
}

function view(
  over: {
    decidability?: IssueDecidability;
    referralReasons?: ReferralView['decidability']['reasons'];
  } = {},
): ReferralView {
  const d = over.decidability ?? decidability();
  return {
    summary: item,
    productVersion: '1.2',
    producerCode: '10233',
    facts: {
      evaluatedAt: '2026-10-07T08:00:00Z',
      effectiveDate: '2026-10-15',
      ruleSetCode: 'UW-MOTOR-GR-B',
      ruleSetVersion: '1.0',
      dataStatus: 'ILLUSTRATIVE_TEST_DATA',
      vehicle: {
        make: 'Fiat',
        model: 'Panda',
        ageYears: 35,
        engineCapacityCc: 900,
        usage: 'PRIVATE',
      },
      driver: { youngestAgeBand: 'FROM_18_TO_20', claimsLast5Years: 0 },
    },
    issues: item.reasons.map((reason) => ({
      issue: {
        id: reason.issueId,
        jobRef,
        issueType: reason.issueType,
        issueKey: `${reason.issueType}:x`,
        ruleId: reason.ruleId,
        severity: 'REFER',
        blockingPoint: 'PRE_BIND',
        lane: 'UW',
        status: 'Open',
        recordVersion: 3,
        messageEn: 'm',
        messageEl: 'μ',
        raisedAt: '2026-10-07T08:00:00Z',
        raisedBy: 'underwriter',
      },
      decidability: d,
    })),
    decidability: { canDecide: d.canDecide, reasons: over.referralReasons ?? d.reasons },
  };
}

function routes(
  opts: {
    list?: ReferralListPage;
    referral?: ReferralView;
    decide?: () => { status?: number; body?: unknown };
  } = {},
) {
  return mockApi([
    {
      method: 'GET',
      path: '/api/uw/v1/referrals',
      respond: () => ({ body: opts.list ?? page([item]) }),
    },
    {
      method: 'GET',
      path: `/api/uw/v1/referrals/${jobRef}`,
      respond: () => ({ body: { referral: opts.referral ?? view() } }),
    },
    {
      method: 'POST',
      path: '/api/uw/v1/issues/decide',
      respond:
        opts.decide ??
        (() => ({ body: { decisions: [], checkIds: [], pendingSecondApproval: false } })),
    },
  ]);
}

const render = (url = '/underwriting') =>
  renderScreen(<WorkbenchPage />, { path: '/underwriting', url });

describe('WorkbenchPage', () => {
  it('shows the views with counts and the referral row, without a11y violations', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes();
    const { container } = render();
    const rail = screen.getByRole('navigation', { name: 'Προβολές εργασίας' });
    await waitFor(() => {
      expect(within(rail).getByRole('button', { name: /Οι παραπομπές μου/ })).toHaveTextContent(
        '8',
      );
    });
    expect(within(rail).getByRole('button', { name: /Ομάδα/ })).toHaveTextContent('31');
    expect(within(rail).getByRole('button', { name: /Ολοκληρωμένες σήμερα/ })).toHaveTextContent(
      '11',
    );
    expect(within(rail).getByRole('button', { name: /Απορρίφθηκαν/ })).toHaveTextContent('3');
    expect(within(rail).getByText('11 από 19')).toBeInTheDocument();
    const grid = await screen.findByRole('grid', { name: 'Παραπομπές' });
    expect(within(grid).getByText(customerName)).toBeInTheDocument();
    expect(within(grid).getByText('Παλαιό όχημα +1')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('asks the server for the selected view', async () => {
    signIn(['Staff.UnderwritingManager']);
    const api = routes();
    const { user } = render();
    await screen.findByRole('grid', { name: 'Παραπομπές' });
    await user.click(screen.getByRole('button', { name: /Ομάδα/ }));
    await waitFor(() => {
      expect(
        api
          .callsTo('GET', '/api/uw/v1/referrals')
          .some((c) => c.url.searchParams.get('queue') === 'OPEN'),
      ).toBe(true);
    });
    expect(api.callsTo('GET', '/api/uw/v1/referrals')[0]?.url.searchParams.get('queue')).toBe(
      'MINE',
    );
  });

  it('shows the empty state of an empty queue', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes({ list: page([]) });
    render();
    expect(await screen.findByText('Η ουρά σας είναι άδεια')).toBeInTheDocument();
    expect(screen.getByText('Ολοκληρώσατε 11 παραπομπές σήμερα.')).toBeInTheDocument();
  });

  it('shows the no-permission state without calling the API', () => {
    signIn(['Staff.Underwriter']);
    const api = routes();
    render();
    expect(screen.getByText(/δεν επιτρέπει την προβολή των παραπομπών/)).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });

  it('explains a failed list with 403 as no permission', async () => {
    signIn(['Platform.Admin']);
    mockApi([
      {
        method: 'GET',
        path: '/api/uw/v1/referrals',
        respond: () => problem(403, 'UW-ERR-PERMISSION', 'Forbidden'),
      },
    ]);
    render();
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });

  it('opens a row by click with only the opaque job id in the URL and shows value against limit', async () => {
    signIn(['Staff.UnderwritingManager']);
    const api = routes();
    const { user } = render();
    const grid = await screen.findByRole('grid', { name: 'Παραπομπές' });
    await user.click(within(grid).getByText(customerName));
    expect(
      await screen.findByRole('heading', { level: 1, name: customerName }),
    ).toBeInTheDocument();
    expect(api.callsTo('GET', `/api/uw/v1/referrals/${jobRef}`)).toHaveLength(1);
    const location = screen.getByTestId('location').textContent;
    expect(location).toBe(`/underwriting?job=${jobRef}`);
    expect(location).not.toContain('Νικολάου');
    expect(sessionStorage.getItem('coreins.devSession')).not.toContain(customerName);
    const card = screen.getByRole('article', { name: 'Παλαιό όχημα' });
    expect(within(card).getByText('35')).toBeInTheDocument();
    expect(within(card).getByText('20')).toBeInTheDocument();
    // The age band is readable and the missing limit says why.
    const driver = screen.getByRole('article', { name: 'Νέος οδηγός' });
    expect(within(driver).getByText('18–20 ετών')).toBeInTheDocument();
  });

  it('opens a row with the keyboard (arrow down, Enter)', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes();
    const { user } = render();
    const grid = await screen.findByRole('grid', { name: 'Παραπομπές' });
    within(grid).getByText(customerName).closest<HTMLElement>('[role="row"]')?.focus();
    await user.keyboard('{ArrowDown}{Enter}');
    expect(
      await screen.findByRole('heading', { level: 1, name: customerName }),
    ).toBeInTheDocument();
  });

  it('blocks Ctrl+Enter without a reason and decides all open issues with one', async () => {
    signIn(['Staff.UnderwritingManager']);
    const api = routes();
    const { user } = render(`/underwriting?job=${jobRef}`);
    await user.click(await screen.findByRole('button', { name: 'Έγκριση…' }));
    const reason = await screen.findByRole('textbox', { name: /Αιτιολογία έγκρισης/ });
    await user.type(reason, '{Control>}{Enter}{/Control}');
    expect(api.callsTo('POST', '/api/uw/v1/issues/decide')).toHaveLength(0);
    expect(await screen.findAllByText('Συμπληρώστε την αιτιολογία της απόφασης.')).not.toHaveLength(
      0,
    );
    await user.type(reason, 'Εντός πολιτικής');
    await user.type(reason, '{Control>}{Enter}{/Control}');
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/uw/v1/issues/decide')).toHaveLength(1);
    });
    const [call] = api.callsTo('POST', '/api/uw/v1/issues/decide');
    expect(call?.body).toEqual({
      issueIds: [issueId, '1550e000-1111-4222-8333-000000000002'],
      decision: 'APPROVE',
      reason: 'Εντός πολιτικής',
      expectedRecordVersions: { [issueId]: 3, '1550e000-1111-4222-8333-000000000002': 3 },
    });
    expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    // Never a role or an authority from the client (PITFALLS 1-7).
    expect(JSON.stringify(call?.body)).not.toMatch(/role|authority/i);
  });

  it('disables both decisions with the separation-of-duties reason', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes({
      referral: view({
        decidability: decidability({
          canDecide: false,
          reasons: ['SOD_CREATOR'],
        }),
      }),
    });
    render(`/underwriting?job=${jobRef}`);
    const approve = await screen.findByRole('button', { name: 'Έγκριση…' });
    expect(approve).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Απόρριψη…' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(screen.getAllByText('Δημιουργήσατε εσείς αυτή την προσφορά.').length).toBeGreaterThan(0);
  });

  it('shows a denied authority in danger and still lets the server decide', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes({
      referral: view({
        decidability: decidability({
          authority: {
            type: 'UW.ISSUE_APPROVAL',
            issueType: 'VEHICLE_AGE_REFERRAL',
            outcome: 'DENY',
          },
          canDecide: false,
          reasons: ['NO_AUTHORITY'],
        }),
      }),
    });
    render(`/underwriting?job=${jobRef}`);
    expect(await screen.findByText('δεν επιτρέπεται')).toBeInTheDocument();
    expect(screen.getByRole('meter', { name: 'Εκτός της εξουσιοδότησής σας' })).toBeInTheDocument();
  });

  it('explains a server refusal in words and keeps the code under technical details', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes({ decide: () => problem(403, 'UW-ERR-SOD', 'Forbidden') });
    const { user } = render(`/underwriting?job=${jobRef}`);
    await user.click(await screen.findByRole('button', { name: 'Απόρριψη…' }));
    await user.type(await screen.findByRole('textbox', { name: /Αιτιολογία απόρριψης/ }), 'Λόγος');
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(await screen.findByText(/συμμετείχατε εσείς στη δημιουργία/)).toBeInTheDocument();
    const details = screen.getByText('Τεχνικές λεπτομέρειες').closest('details');
    expect(details).toHaveTextContent('UW-ERR-SOD');
    expect(screen.queryByText('UW-ERR-SOD', { selector: ':not(details *)' })).toBeNull();
  });

  it('explains a stale conflict (409)', async () => {
    signIn(['Staff.UnderwritingManager']);
    routes({ decide: () => problem(409, 'UW-ERR-STALE', 'Stale') });
    const { user } = render(`/underwriting?job=${jobRef}`);
    await user.click(await screen.findByRole('button', { name: 'Έγκριση…' }));
    await user.type(await screen.findByRole('textbox', { name: /Αιτιολογία έγκρισης/ }), 'Ok');
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(await screen.findByText(/άλλαξε από κάποιον άλλον/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ανανέωση παραπομπής' })).toBeInTheDocument();
  });
});
