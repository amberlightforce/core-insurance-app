import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { ReportsPage } from './ReportsPage';
import { groupByStatus, sumByCurrency } from './summarise';

vi.setConfig({ testTimeout: 30_000 });

beforeEach(async () => {
  await i18n.changeLanguage('en');
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
  localStorage.clear();
});

function signIn(id: string) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id, name: id, roles: ['Staff.Finance'], actorKey: `USER:${id}` },
    }),
  );
}

describe('summarise', () => {
  it('sums decimals exactly per currency', () => {
    expect(
      sumByCurrency([
        { amount: '0.10', currency: 'EUR' },
        { amount: '0.20', currency: 'EUR' },
        { amount: '5.00', currency: 'USD' },
      ]),
    ).toEqual({ EUR: '0.30', USD: '5.00' });
  });

  it('groups by status', () => {
    const rows = groupByStatus(
      [
        { s: 'A', m: { amount: '1.00', currency: 'EUR' } },
        { s: 'A', m: { amount: '2.00', currency: 'EUR' } },
        { s: 'B', m: { amount: '3.00', currency: 'EUR' } },
      ],
      (x) => x.s,
      (x) => x.m,
    );
    expect(rows.map((r) => [r.status, r.count])).toEqual([
      ['A', 2],
      ['B', 1],
    ]);
  });
});

const receivable = (status: string, open: string) => ({
  receivableId: crypto.randomUUID(),
  sourceType: 'CLM_CLAIM_PAYMENT',
  sourceId: 's',
  counterpartyPartyId: crypto.randomUUID(),
  purpose: 'RECOVERY',
  amount: { amount: open, currency: 'EUR' },
  openAmount: { amount: open, currency: 'EUR' },
  status,
  dueDate: '2026-11-01',
  billingAccountId: crypto.randomUUID(),
  paymentReference: 'REF',
  registeredAt: '2026-10-01T00:00:00Z',
  recordVersion: 1,
});

const runId = 'b0000000-1111-4222-8333-000000000001';
const otherRunId = 'b0000000-1111-4222-8333-000000000002';

const kpiRoute: MockRoute = {
  method: 'GET',
  path: '/api/dat/v1/kpis/get',
  respond: () => ({
    body: {
      values: {
        GWP: { amount: '120000.00', currency: 'EUR' },
        CLAIMS_PAID: { amount: '30000.00', currency: 'EUR' },
        OPEN_RESERVE: { amount: '10000.00', currency: 'EUR' },
        LOSS_RATIO: { ratio: 0.3333 },
      },
    },
  }),
};
const billingRoutes: MockRoute[] = [
  { method: 'GET', path: '/api/bil/v1/receivables', respond: () => ({ body: { items: [] } }) },
  { method: 'GET', path: '/api/bil/v1/delinquencies', respond: () => ({ body: { items: [] } }) },
];
const run = (id: string, over: Record<string, unknown> = {}) => ({
  runId: id,
  mart: 'PORTFOLIO_PREMIUM',
  period: '2026-10',
  productCode: '',
  validAt: '2026-11-01T00:00:00Z',
  knownAt: '2026-11-01T00:00:00Z',
  status: 'COMPLETED',
  snapshotHash: 'abc123',
  legalStatus: 'NotRegulatory',
  definitionVersion: '1.0',
  figureCount: 1,
  createdAt: '2026-11-01T10:00:00Z',
  createdBy: 'dev:maker',
  signOff: null,
  adjustments: [],
  ...over,
});
const figure = {
  key: 'GWP|MOTOR|2026-10|',
  metric: 'GWP',
  productCode: 'MOTOR',
  month: '2026-10',
  dimension: '',
  value: '1000.0000',
  adjustment: '0',
  adjustedValue: '1000.0000',
  unit: 'EUR',
  sourceCount: 3,
};
/** dat.Mart.list: the summaries of the given runs (newest first). */
const listRoute = (...runs: { id: string; over?: Record<string, unknown> }[]): MockRoute => ({
  method: 'GET',
  path: '/api/dat/v1/marts/runs',
  respond: () => ({
    body: {
      items: runs.map(({ id, over }) => {
        const full = run(id, over);
        return {
          runId: full.runId,
          mart: full.mart,
          period: full.period,
          productCode: full.productCode,
          status: full.status,
          snapshotHash: full.snapshotHash,
          legalStatus: full.legalStatus,
          figureCount: full.figureCount,
          createdAt: full.createdAt,
          createdBy: full.createdBy,
          signOff: full.signOff,
        };
      }),
      nextCursor: null,
    },
  }),
});
const runRoute = (id: string, over: Record<string, unknown> = {}): MockRoute => ({
  method: 'GET',
  path: `/api/dat/v1/marts/${id}`,
  respond: () => ({ body: { run: run(id, over), figures: [figure] } }),
});

describe('mart reports', () => {
  it('runs a mart, shows the KPI strip and figures, and disables sign-off for the maker', async () => {
    signIn('dev:maker');
    const api = mockApi([
      ...billingRoutes,
      kpiRoute,
      {
        method: 'POST',
        path: '/api/dat/v1/marts/run',
        respond: () => ({ status: 202, body: { runId, status: 'COMPLETED' } }),
      },
      listRoute(),
      runRoute(runId),
    ]);
    const { user, container } = renderScreen(<ReportsPage />, {
      path: '/reports',
      url: '/reports',
    });
    expect(await screen.findByText(/Gross written premium/)).toBeInTheDocument();
    const card = await screen.findByRole('region', { name: 'Portfolio premium' });
    await user.click(within(card).getByRole('button', { name: 'Run report' }));
    expect(await screen.findByText('MOTOR')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/dat/v1/marts/run')[0]?.body).toMatchObject({
      mart: 'PORTFOLIO_PREMIUM',
    });
    expect(screen.getByRole('button', { name: 'Sign off' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await expectNoA11yViolations(container);
  });

  it('lets another user sign off, and shows the problem when an unsigned export is refused', async () => {
    signIn('dev:checker');
    const api = mockApi([
      ...billingRoutes,
      kpiRoute,
      listRoute({ id: runId }),
      runRoute(runId),
      {
        method: 'POST',
        path: '/api/dat/v1/marts/export',
        respond: () => problem(422, 'DAT-ERR-024', 'The run is not signed off'),
      },
    ]);
    const { user } = renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    await user.click(await screen.findByRole('button', { name: /Open Portfolio premium run/ }));
    const sign = await screen.findByRole('button', { name: 'Sign off' });
    expect(sign).not.toHaveAttribute('aria-disabled', 'true');
    await user.click(screen.getByRole('button', { name: 'Export CSV' }));
    expect(await screen.findByText('The export was refused')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/dat/v1/marts/export')).toHaveLength(1);
  });

  it('disables sign-off for a person who adjusted a figure', async () => {
    signIn('dev:adjuster');
    mockApi([
      ...billingRoutes,
      kpiRoute,
      listRoute({ id: runId }),
      runRoute(runId, {
        adjustments: [
          {
            adjustmentId: 'a1',
            figureKey: figure.key,
            amount: '5.0000',
            currency: 'EUR',
            reason: 'fix',
            evidence: 'T-1',
            createdBy: 'dev:adjuster',
          },
        ],
      }),
    ]);
    const { user } = renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    await user.click(await screen.findByRole('button', { name: /Open Portfolio premium run/ }));
    expect(await screen.findByRole('button', { name: 'Sign off' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  it('submits an adjustment with reason and evidence', async () => {
    signIn('dev:checker');
    const api = mockApi([
      ...billingRoutes,
      kpiRoute,
      listRoute({ id: runId }),
      runRoute(runId),
      {
        method: 'POST',
        path: '/api/dat/v1/marts/adjust',
        respond: () => ({ body: { adjustedValue: '1010.0000' } }),
      },
    ]);
    const { user } = renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    await user.click(await screen.findByRole('button', { name: /Open Portfolio premium run/ }));
    await user.click(
      await screen.findByRole('button', { name: 'Adjust figure GWP|MOTOR|2026-10|' }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/Adjustment \(EUR\)/), '10.00');
    await user.type(within(dialog).getByLabelText('Reason'), 'late endorsement');
    await user.type(within(dialog).getByLabelText('Evidence reference'), 'T-42');
    await user.click(within(dialog).getByRole('button', { name: 'Add adjustment' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/dat/v1/marts/adjust')).toHaveLength(1);
    });
    expect(api.callsTo('POST', '/api/dat/v1/marts/adjust')[0]?.body).toMatchObject({
      runId,
      figureKey: figure.key,
      amount: { amount: '10.00', currency: 'EUR' },
      reason: 'late endorsement',
      evidence: 'T-42',
    });
  });

  it('compares two runs of the same mart', async () => {
    signIn('dev:checker');
    mockApi([
      ...billingRoutes,
      kpiRoute,
      listRoute({ id: runId }, { id: otherRunId, over: { createdAt: '2026-11-02T10:00:00Z' } }),
      runRoute(runId),
      runRoute(otherRunId, { createdAt: '2026-11-02T10:00:00Z' }),
      {
        method: 'GET',
        path: '/api/dat/v1/marts/compare',
        respond: () => ({
          body: {
            figures: { compared: 2, changed: 1 },
            diff: [
              {
                key: 'GWP|MOTOR|2026-10|',
                a: 1000,
                b: 1100,
                delta: 100,
                unit: 'EUR',
                status: 'CHANGED',
              },
            ],
          },
        }),
      },
    ]);
    const { user } = renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    const open = await screen.findAllByRole('button', { name: /Open Portfolio premium run/ });
    const [first] = open;
    if (!first) throw new Error('no run');
    await user.click(first);
    await user.click(await screen.findByRole('button', { name: /Compare with/ }));
    await user.click(await screen.findByRole('option'));
    expect(await screen.findByText('1 of 2 figures differ.')).toBeInTheDocument();
    expect(screen.getByText('Changed')).toBeInTheDocument();
  });

  it('shows the no-permission banner when the KPI read is forbidden', async () => {
    signIn('dev:maker');
    mockApi([
      listRoute(),
      ...billingRoutes,
      {
        method: 'GET',
        path: '/api/dat/v1/kpis/get',
        respond: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden'),
      },
    ]);
    renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    expect(await screen.findByText(/permission/i)).toBeInTheDocument();
  });

  it('shows the empty history before any run', async () => {
    signIn('dev:maker');
    mockApi([...billingRoutes, kpiRoute, listRoute()]);
    renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    expect(await screen.findByText(/No reports have been run yet/)).toBeInTheDocument();
  });
});

describe('ReportsPage', () => {
  it('summarises receivables and delinquency from the live reads', async () => {
    mockApi([
      listRoute(),
      {
        method: 'GET',
        path: '/api/bil/v1/receivables',
        respond: () => ({
          body: { items: [receivable('OPEN', '100.00'), receivable('OPEN', '50.50')] },
        }),
      },
      {
        method: 'GET',
        path: '/api/bil/v1/delinquencies',
        respond: () => ({ body: { items: [] } }),
      },
      kpiRoute,
    ]);
    renderScreen(<ReportsPage />, { path: '/reports', url: '/reports' });
    expect(await screen.findByRole('heading', { level: 1, name: 'Reports' })).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByText(/150[.,]50/)).toBeInTheDocument();
    });
    expect(screen.getByText(/not available through the API yet/)).toBeInTheDocument();
  });
});
