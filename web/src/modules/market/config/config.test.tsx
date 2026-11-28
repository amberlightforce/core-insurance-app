import { screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ConfigChangeRequestView } from '../../../api/types';
import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, renderScreen } from '../../../test/mockApi';
import { ConfigDetailPage } from './ConfigDetailPage';
import { ConfigFormPage } from './ConfigFormPage';
import { ConfigListPage } from './ConfigListPage';

const session = vi.hoisted(() => ({
  user: {
    id: 'u',
    actorKey: 'USER:dev:checker',
    name: 'Synthetic',
    roles: ['Platform.DesignAuthority'],
  },
}));
vi.mock('../../../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../dev-auth/devAuth')>()),
  useDevSession: () => ({ user: session.user }),
}));
vi.setConfig({ testTimeout: 60_000 });

const id = '11111111-1111-4111-8111-111111111111';
const hash = 'a'.repeat(64);
const request = (over: Partial<ConfigChangeRequestView> = {}): ConfigChangeRequestView => ({
  id,
  status: 'PENDING_APPROVAL',
  cause: 'MANUAL',
  effectiveAt: null,
  reason: 'Synthetic rate update',
  country: 'GR',
  baseHash: hash,
  items: [
    {
      key: 'tax.ipt.rate.general',
      value: '0.15',
      legalStatus: 'Unverified',
      sourceRef: 'Synthetic source',
      motorPath: false,
      validFrom: null,
      validTo: null,
      note: null,
    },
  ],
  createdBy: 'USER:dev:maker',
  decidedBy: null,
  decisionReason: null,
  approvalRequestId: null,
  resultingHash: null,
  appliedAt: null,
  createdAt: '2026-11-28T08:00:00Z',
  recordVersion: 1,
  ...over,
});
const previewRun = (stale: boolean) => ({
  previewRun: {
    requestId: id,
    baseHash: hash,
    currentHash: hash,
    stale,
    conflicts: stale
      ? [{ key: 'tax.ipt.rate.general', baseValue: '0.12', currentValue: '0.13' }]
      : [],
    items: [
      {
        key: 'tax.ipt.rate.general',
        legalStatus: 'Unverified',
        validFrom: null,
        referenceDate: '2026-11-28',
        before: '0.12',
        after: '0.15',
        changed: true,
      },
    ],
    resultingHash: stale ? null : hash,
    effectiveAt: null,
  },
});
const detailRoutes = (req: ConfigChangeRequestView, stale = false) => [
  {
    method: 'GET',
    path: `/api/mkt/v1/config-change-requests/${id}`,
    respond: () => ({ body: req }),
  },
  {
    method: 'POST',
    path: '/api/mkt/v1/config-change-requests/preview',
    respond: () => ({ body: previewRun(stale) }),
  },
];
const detail = () =>
  renderScreen(<ConfigDetailPage />, {
    path: '/admin/config/:id',
    url: `/admin/config/${id}`,
  });

beforeEach(async () => {
  session.user = {
    id: 'u',
    actorKey: 'USER:dev:checker',
    name: 'Synthetic',
    roles: ['Platform.DesignAuthority'],
  };
  await i18n.changeLanguage('en');
});
afterEach(() => vi.unstubAllGlobals());

describe('configuration change requests', () => {
  it('lists requests and filters by status on the server', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/mkt/v1/config-change-requests',
        respond: () => ({ body: { items: [request()], nextCursor: null, limit: 50 } }),
      },
    ]);
    const { container, user } = renderScreen(<ConfigListPage />, {
      path: '/admin/config',
      url: '/admin/config',
    });
    await screen.findByText('Synthetic rate update');
    await expectNoA11yViolations(container);
    expect(screen.queryByRole('button', { name: 'New change request' })).not.toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: /Status/ })[0] as HTMLElement);
    await user.click(await screen.findByRole('option', { name: 'Applied' }));
    await waitFor(() => {
      expect(api.calls.some((c) => c.url.searchParams.get('status') === 'APPLIED')).toBe(true);
    });
  });

  it('shows the before and after values and lets a checker approve with a reason', async () => {
    const api = mockApi([
      ...detailRoutes(request()),
      {
        method: 'POST',
        path: '/api/mkt/v1/config-change-requests/approve',
        respond: () => ({ body: { request: request({ status: 'APPLIED' }) } }),
      },
    ]);
    const { user } = detail();
    await screen.findByText('tax.ipt.rate.general');
    expect(await screen.findByText('0.12')).toBeInTheDocument();
    expect(screen.getByText('Unverified')).toBeInTheDocument();
    expect(api.callsTo('POST', /preview$/)[0]?.headers.get('Idempotency-Key')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Approve and apply' }));
    await screen.findByText('Give a reason.');
    expect(api.callsTo('POST', /approve$/)).toHaveLength(0);
    await user.type(screen.getByRole('textbox', { name: /Reason/ }), 'Checked the source');
    await user.click(screen.getByRole('button', { name: 'Approve and apply' }));
    await screen.findByText('Approved and applied.');
    expect(api.callsTo('POST', /approve$/)[0]?.body).toEqual({
      requestId: id,
      reason: 'Checked the source',
    });
  });

  it('offers the maker no approve button on their own request', async () => {
    session.user = {
      id: 'm',
      actorKey: 'USER:dev:maker',
      name: 'Maker',
      roles: ['Platform.ReleaseManager', 'Platform.DesignAuthority'],
    };
    mockApi(detailRoutes(request()));
    detail();
    await screen.findByText('tax.ipt.rate.general');
    expect(screen.queryByRole('button', { name: 'Approve and apply' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Withdraw' })).toBeInTheDocument();
  });

  it('shows the rebase banner on a conflict and blocks approval', async () => {
    mockApi(detailRoutes(request(), true));
    detail();
    await screen.findByText('The configuration changed since this request was written');
    expect(screen.getByRole('button', { name: 'Approve and apply' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  it('lets the maker rebase a stale request', async () => {
    session.user = {
      id: 'm',
      actorKey: 'USER:dev:maker',
      name: 'Maker',
      roles: ['Platform.ReleaseManager'],
    };
    const api = mockApi([
      ...detailRoutes(request(), true),
      {
        method: 'POST',
        path: '/api/mkt/v1/config-change-requests/rebase',
        respond: () => ({
          body: { request: request({ status: 'DRAFT' }), previousBaseHash: hash, conflicts: [] },
        }),
      },
    ]);
    const { user } = detail();
    await screen.findByText('The configuration changed since this request was written');
    await user.type(screen.getByRole('textbox', { name: /Reason/ }), 'Newer rate seen');
    await user.click(screen.getByRole('button', { name: 'Rebase on newest state' }));
    await screen.findByText('Rebased; back to draft.');
    expect(api.callsTo('POST', /rebase$/)).toHaveLength(1);
  });

  it('shows a closed request read-only', async () => {
    mockApi(detailRoutes(request({ status: 'APPLIED', decidedBy: 'USER:dev:checker' })));
    detail();
    await screen.findByText('This request is closed.');
    expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
  });

  it('creates a draft once the required fields are filled', async () => {
    session.user = {
      id: 'm',
      actorKey: 'USER:dev:maker',
      name: 'Maker',
      roles: ['Platform.ReleaseManager'],
    };
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/mkt/v1/config-change-requests',
        respond: () => ({ body: { request: request({ status: 'DRAFT' }) } }),
      },
    ]);
    const { user } = renderScreen(<ConfigFormPage />, {
      path: '/admin/config/new',
      url: '/admin/config/new',
    });
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(api.calls).toHaveLength(0);
    await user.type(screen.getByRole('textbox', { name: /Reason for the change/ }), 'Rate update');
    await user.type(
      screen.getByRole('combobox', { name: /Configuration key/ }),
      'tax.ipt.rate.fire',
    );
    await user.tab();
    await user.type(screen.getByRole('textbox', { name: /^Value/ }), '0.20');
    await user.type(screen.getByRole('textbox', { name: /^Source/ }), 'Synthetic source');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => {
      expect(api.calls).toHaveLength(1);
    });
    expect(api.calls[0]?.body).toMatchObject({
      reason: 'Rate update',
      items: [{ key: 'tax.ipt.rate.fire', value: '0.20', sourceRef: 'Synthetic source' }],
    });
  });
});
