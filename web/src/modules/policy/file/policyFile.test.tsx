import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import * as fx from '../../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../../test/mockApi';
import { PolicyViewPage } from '../PolicyViewPage';
import type { PolicyFileResponse, SnapshotGetResponse, TermTimelineResponse } from './api';

vi.setConfig({ testTimeout: 60_000 });

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const term1Id = '7e100000-0000-4000-8000-000000000001';
const term2Id = '7e100000-0000-4000-8000-000000000002';

function signInAs(roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 't',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      user: { id: 'dev', name: 'Dev User (synthetic)', roles },
    }),
  );
}

/** Term 1 (in force, 2026-10-08 → 2027-10-08) and a scheduled renewal term 2 (2027-10-08 → 2028-10-08). */
function policyAt(validAt: string, state1 = 'IN_FORCE'): PolicyFileResponse {
  const base = fx.policy('IN_FORCE');
  const term1 = { ...base.term, termId: term1Id, state: state1 } as unknown as NonNullable<
    PolicyFileResponse['term']
  >;
  if (validAt >= '2027-10-08') {
    return {
      ...base,
      policy: { ...base.policy, status: 'SCHEDULED' },
      term: {
        ...term1,
        termId: term2Id,
        termNumber: 2,
        state: 'SCHEDULED',
        period: { from: '2027-10-08T00:00:00+03:00', to: '2028-10-08T00:00:00+03:00' },
      },
      transactions: [
        {
          transactionId: 'tx-r1',
          kind: 'NEW_BUSINESS',
          sequence: 1,
          effectiveAt: '2027-10-08T00:00:00+03:00',
          recordedAt: '2027-08-20T09:00:00Z',
          premium: eur('450.00'),
          taxes: eur('0.00'),
          total: eur('450.00'),
        },
      ],
      charges: [],
      effectiveKnownAt: '2026-10-09T08:00:00Z',
    };
  }
  if (validAt < '2026-10-08') {
    return {
      policy: base.policy,
      transactions: [],
      charges: [],
      effectiveKnownAt: '2026-10-09T08:00:00Z',
    };
  }
  return {
    ...base,
    policy: { ...base.policy, status: state1 as 'IN_FORCE' },
    term: term1,
    transactions: [...base.transactions],
    charges: base.charges.map((c) => ({ ...c, transactionId: 'tx-1' })),
    effectiveKnownAt: '2026-10-09T08:00:00Z',
  };
}

const baseTerm = fx.policy().term as NonNullable<PolicyFileResponse['term']>;
const timelines: Record<string, TermTimelineResponse> = {
  [term1Id]: {
    policy: fx.policy().policy,
    term: baseTerm,
    effectiveKnownAt: '2026-10-09T08:00:00Z',
    transactions: [
      {
        transactionId: 'tx-1',
        kind: 'NEW_BUSINESS',
        sequence: 1,
        effectiveAt: '2026-10-08T00:00:00+03:00',
        recordedAt: '2026-10-07T20:20:00Z',
        premiumChange: eur('432.35'),
        reversed: false,
      },
      {
        transactionId: 'tx-2',
        kind: 'ENDORSEMENT_DEBIT',
        sequence: 2,
        effectiveAt: '2027-01-15T00:00:00+02:00',
        recordedAt: '2027-01-14T10:00:00Z',
        premiumChange: eur('20.10'),
        reversed: false,
      },
      {
        transactionId: 'tx-3',
        kind: 'ENDORSEMENT_CREDIT',
        sequence: 3,
        effectiveAt: '2027-03-01T00:00:00+02:00',
        recordedAt: '2027-02-28T10:00:00Z',
        premiumChange: eur('-12.05'),
        reversed: false,
      },
    ],
  } as TermTimelineResponse,
  [term2Id]: {
    policy: fx.policy().policy,
    term: baseTerm,
    effectiveKnownAt: '2026-10-09T08:00:00Z',
    transactions: [
      {
        transactionId: 'tx-r1',
        kind: 'NEW_BUSINESS',
        sequence: 1,
        effectiveAt: '2027-10-08T00:00:00+03:00',
        recordedAt: '2027-08-20T09:00:00Z',
        premiumChange: eur('450.00'),
        reversed: false,
      },
    ],
  } as TermTimelineResponse,
};

interface Setup {
  state1?: string;
  timeline?: (termId: string) => { status?: number; body?: unknown };
  snapshot?: SnapshotGetResponse;
  invoices?: unknown;
}

function routes(setup: Setup = {}): MockRoute[] {
  return [
    {
      method: 'GET',
      path: `/api/pol/v1/policies/${fx.policyId}`,
      respond: (r) => ({ body: policyAt(r.url.searchParams.get('validAt') ?? '', setup.state1) }),
    },
    {
      method: 'GET',
      path: '/api/pol/v1/terms/timeline',
      respond: (r) => {
        const id = r.url.searchParams.get('termId') ?? '';
        return setup.timeline ? setup.timeline(id) : { body: timelines[id] };
      },
    },
    {
      method: 'GET',
      path: '/api/pol/v1/snapshots/get',
      respond: () => {
        const s =
          setup.snapshot ??
          ({
            snapshotRef: 'snap-1',
            validAt: '2026-10-08T21:00:00Z',
            knownAt: '2026-10-09T08:00:00Z',
            inForce: true,
            policy: {},
            supersession: { superseded: false },
          } as unknown as SnapshotGetResponse);
        return { body: s };
      },
    },
    {
      method: 'GET',
      path: '/api/bil/v1/invoices',
      respond: () => ({ body: setup.invoices ?? { items: [], nextCursor: null, limit: 50 } }),
    },
  ];
}

const view = () =>
  renderScreen(<PolicyViewPage />, {
    path: '/policies/:policyId',
    url: `/policies/${fx.policyId}`,
  });

beforeEach(async () => {
  await i18n.changeLanguage('el');
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
  localStorage.clear();
});

describe('Policy file (SL3-UI-POL-FILE)', () => {
  it('lays out terms, premium card and history grouped by term, newest first, with accessible markup', async () => {
    mockApi(routes());
    const { container } = view();
    await screen.findByRole('heading', { level: 1, name: 'Ασφαλιστήριο POL000000007' });

    // Term timeline: term 1 (current) and the renewal term 2.
    const strip = await screen.findByRole('list', { name: 'Όροι ασφάλισης' });
    await waitFor(() => {
      expect(within(strip).getAllByRole('listitem')).toHaveLength(2);
    });
    expect(within(strip).getByText('Όρος 2 · ανανέωση')).toBeInTheDocument();
    expect(screen.getByText('Εκκρεμεί ανανέωση', { exact: false })).toBeInTheDocument();

    // History: term 2 first, then term 1; inside term 1 the highest sequence first.
    const groups = await screen.findByRole('list', { name: 'Συναλλαγές ανά όρο' });
    const headings = within(groups).getAllByRole('heading', { level: 3 });
    expect(headings.map((h) => h.textContent)).toEqual(['Όρος 2', 'Όρος 1']);
    const term1List = await within(groups).findByRole('list', { name: 'Συναλλαγές όρου 1' });
    const rows = within(term1List).getAllByRole('listitem');
    expect(rows.map((r) => /#(\d)/.exec(r.textContent)?.[1])).toEqual(['3', '2', '1']);
    expect(within(rows[0] as HTMLElement).getByText('Αλλαγή')).toBeInTheDocument();
    expect(within(rows[2] as HTMLElement).getByText('Νέα παραγωγή')).toBeInTheDocument();
    const term2List = within(groups).getByRole('list', { name: 'Συναλλαγές όρου 2' });
    expect(within(term2List).getByText('Ανανέωση')).toBeInTheDocument();

    // Term premium card in BigInt minor units: written 452,45 (432,35 + 20,10), credits -12,05, net 440,40.
    const premium = screen.getByRole('region', { name: 'Ασφάλιστρο όρου' });
    expect(within(premium).getByText('440,40 €')).toBeInTheDocument();
    expect(within(premium).getByText(/Γραμμένο 452,45 €/)).toBeInTheDocument();
    expect(within(premium).getByText(/Πιστώσεις −12,05 €/)).toBeInTheDocument();

    // As known at is shown read-only.
    expect(screen.getByText('Γνωστό κατά')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('expands the charges of a transaction with the provisional tax badge', async () => {
    mockApi(routes());
    const { user } = view();
    const term1List = await screen.findByRole('list', { name: 'Συναλλαγές όρου 1' });
    const first = within(term1List).getAllByRole('listitem').at(-1) as HTMLElement;
    const toggle = within(first).getByRole('button', { name: /Χρεώσεις \(3\)/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const grid = within(first).getByRole('grid', { name: 'Χρεώσεις συναλλαγής 1' });
    expect(within(grid).getByText('PREM-MTPL')).toBeInTheDocument();
    expect(within(grid).getByText('GR-IPT')).toBeInTheDocument();
    expect(within(grid).getByText(/Προσωρινό/)).toBeInTheDocument();
  });

  it('shows billed and paid of the term with a link to the invoice', async () => {
    const invoice = { ...fx.invoice().invoice, policyTermId: term1Id };
    mockApi(
      routes({
        invoices: {
          items: [
            {
              invoice: { ...invoice, total: eur('479.20'), paid: eur('100.00') },
              fiscalStatus: fx.invoice().fiscalStatus,
            },
          ],
          nextCursor: null,
          limit: 50,
        },
      }),
    );
    view();
    const premium = await screen.findByRole('region', { name: 'Ασφάλιστρο όρου' });
    await waitFor(() => {
      expect(within(premium).getByText('479,20 €')).toBeInTheDocument();
    });
    expect(within(premium).getByText('100,00 €')).toBeInTheDocument();
    expect(
      within(premium).getByRole('button', { name: /Άνοιγμα τιμολογίου INV000000003/ }),
    ).toBeInTheDocument();
  });

  it('hides the action bar without the servicing permission', async () => {
    mockApi(routes());
    view();
    await screen.findByRole('heading', { level: 1, name: 'Ασφαλιστήριο POL000000007' });
    expect(
      screen.queryByRole('group', { name: 'Ενέργειες ασφαλιστηρίου' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ανάκληση' })).not.toBeInTheDocument();
  });

  it('offers Change, Cancel and Renew now to an underwriter and opens the servicing route', async () => {
    signInAs(['Staff.Underwriter']);
    mockApi(routes());
    const { user } = view();
    const bar = await screen.findByRole('group', { name: 'Ενέργειες ασφαλιστηρίου' });
    // A renewal term already exists in this fixture, so Renew now carries the reason.
    expect(within(bar).getByRole('button', { name: 'Αλλαγή' })).toBeEnabled();
    expect(within(bar).getByRole('button', { name: 'Ακύρωση' })).toBeEnabled();
    await waitFor(() => {
      expect(within(bar).getByRole('button', { name: 'Ανανέωση τώρα' })).toHaveAttribute(
        'aria-disabled',
        'true',
      );
    });
    await user.click(within(bar).getByRole('button', { name: 'Ακύρωση' }));
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`/policies/${fx.policyId}/cancel`);
    });
  });

  it('disables the actions with a reason when the term state does not allow them', async () => {
    signInAs(['Staff.Underwriter']);
    mockApi(routes({ state1: 'PENDING_CANCELLATION' }));
    view();
    const bar = await screen.findByRole('group', { name: 'Ενέργειες ασφαλιστηρίου' });
    for (const name of ['Αλλαγή', 'Ακύρωση', 'Ανανέωση τώρα']) {
      const button = within(bar).getByRole('button', { name });
      expect(button).toHaveAttribute('aria-disabled', 'true');
      expect(button).toHaveAccessibleDescription('Εκκρεμεί ήδη ακύρωση.');
    }
  });

  it('shows the supersession badge and banner when the snapshot is superseded', async () => {
    mockApi(
      routes({
        snapshot: {
          snapshotRef: 'snap-1',
          validAt: '2026-10-08T21:00:00Z',
          knownAt: '2026-10-09T08:00:00Z',
          inForce: true,
          policy: {},
          supersession: {
            superseded: true,
            successorRef: 'snap-2',
            supersededAt: '2026-10-09T07:00:00Z',
          },
        } as unknown as SnapshotGetResponse,
      }),
    );
    view();
    expect(await screen.findByText('Το στιγμιότυπο έχει αντικατασταθεί')).toBeInTheDocument();
    expect(screen.getAllByText(/Αντικαταστάθηκε/).length).toBeGreaterThan(0);
  });

  it('keeps the page useful when the term timeline is forbidden (403): falls back to the policy transactions', async () => {
    mockApi(routes({ timeline: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden') }));
    view();
    expect(
      (await screen.findAllByText(/Δεν έχετε δικαίωμα να δείτε το πλήρες ιστορικό/)).length,
    ).toBeGreaterThan(0);
    expect((await screen.findAllByText('Νέα παραγωγή')).length).toBeGreaterThan(0);
  });

  it('shows the loading state, then a not-found state', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => problem(404, 'POL-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    view();
    expect(screen.getAllByText('Φόρτωση…').length).toBeGreaterThan(0);
    expect(await screen.findByText('Το ασφαλιστήριο δεν βρέθηκε')).toBeInTheDocument();
  });

  it('shows the no-permission state when the policy is forbidden (403)', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden'),
      },
    ]);
    view();
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });

  it('shows an error state with retry on a server failure', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => problem(500, 'PLT-ERR-INTERNAL', 'Σφάλμα διακομιστή'),
      },
    ]);
    view();
    expect(
      await screen.findByRole('button', { name: /Δοκιμή ξανά|Επανάληψη/ }),
    ).toBeInTheDocument();
  });

  it('shows the empty history state when the policy has no transactions', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => ({
          body: { ...fx.policy('SCHEDULED'), term: undefined, transactions: [], charges: [] },
        }),
      },
      { method: 'GET', path: '/api/bil/v1/invoices', respond: () => ({ body: fx.invoiceList }) },
    ]);
    view();
    expect(await screen.findByText('Δεν υπάρχουν συναλλαγές')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    signInAs(['Staff.Underwriter']);
    mockApi(routes());
    view();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Policy POL000000007' }),
    ).toBeInTheDocument();
    expect(await screen.findByRole('list', { name: 'Policy terms' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Renew now' })).toBeInTheDocument();
    const groups = await screen.findByRole('list', { name: 'Transactions by term' });
    expect(await within(groups).findAllByText('Change')).not.toHaveLength(0);
    expect(screen.getByText('As known at')).toBeInTheDocument();
  });

  it('puts only opaque ids and dates in URLs, stores no personal data, and does not loop', async () => {
    const api = mockApi(routes());
    view();
    await screen.findByRole('list', { name: 'Συναλλαγές όρου 1' });
    for (const call of api.calls) {
      const params = [...call.url.searchParams.entries()];
      for (const [key, value] of params) {
        expect(`${key}=${value}`).toMatch(
          /^(validAt=\d{4}-\d{2}-\d{2}|policyId=[0-9a-f-]{36}|termId=[0-9a-f-]{36}|limit=\d+)$/,
        );
      }
      expect(call.url.pathname).not.toMatch(/ΙΚΧ|Toyota|POL0/);
    }
    const stored = JSON.stringify([Object.entries(sessionStorage), Object.entries(localStorage)]);
    expect(stored).not.toMatch(/ΙΚΧ-1234|Toyota|POL000000007/);
    // Settled: no endless re-fetching (policy x3 dates, 2 timelines, snapshot, invoices; a re-render must not add more).
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(api.calls.length).toBeLessThanOrEqual(10);
  });
});
