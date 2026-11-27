import { screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { RequireRole } from '../auth/RequireRole';
import {
  aiAdminRoles,
  claimsRoles,
  dsarRoles,
  packRoles,
  reinsuranceRoles,
  withdrawalRoles,
} from '../auth/roles';
import i18n from '../i18n';
import { mockApi, problem, renderScreen } from '../test/mockApi';
import { ApprovalsInboxPage } from './claims/ApprovalsInboxPage';
import { DsarListPage } from './compliance/dsar/DsarListPage';
import { WithdrawalListPage } from './channels/withdrawal/WithdrawalListPage';
import { PackRegistryPage } from './market/packs/PackRegistryPage';
import { AiControlPage } from './platform/ai/AiControlPage';
import { ReinsuranceHomePage } from './reinsurance/ReinsuranceHomePage';

/** NX-TST-09: the three states every list screen must show besides the happy path (empty, error, forbidden). */
const session = vi.hoisted(() => ({
  user: { id: 'u', actorKey: 'USER:dev:u', name: 'Synthetic user', roles: [] as string[] },
}));
vi.mock('../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../dev-auth/devAuth')>()),
  useDevSession: () => ({ user: session.user }),
}));
vi.setConfig({ testTimeout: 60_000 });

const emptyPage = { items: [], nextCursor: null };

beforeEach(async () => {
  session.user.roles = ['Platform.Admin'];
  await i18n.changeLanguage('en');
});
afterEach(() => vi.unstubAllGlobals());

interface Screen {
  name: string;
  element: ReactElement;
  path: string;
  roles: readonly string[];
  /** The list endpoint (method, path) that feeds the screen. */
  route: { method: 'GET' | 'POST'; path: string };
  emptyText: string | RegExp | (() => string);
}

const screens: Screen[] = [
  {
    name: 'claims approvals inbox',
    element: <ApprovalsInboxPage />,
    path: '/claims/approvals',
    roles: claimsRoles,
    route: { method: 'GET', path: '/api/plt/v1/approval' },
    emptyText: () => i18n.t('approvals.emptyTitle', { ns: 'claims' }),
  },
  {
    name: 'reinsurance registry',
    element: <ReinsuranceHomePage />,
    path: '/reinsurance',
    roles: reinsuranceRoles,
    route: { method: 'GET', path: '/api/ri/v1/contracts' },
    emptyText: () => i18n.t('registry.emptyTitle', { ns: 'reinsurance' }),
  },
  {
    name: 'DSAR list',
    element: <DsarListPage />,
    path: '/compliance/dsar',
    roles: dsarRoles,
    route: { method: 'GET', path: '/api/cmp/v1/dsar/query' },
    emptyText: /no .*(request|case)|nothing/i,
  },
  {
    name: 'pack registry',
    element: <PackRegistryPage />,
    path: '/admin/packs',
    roles: packRoles,
    route: { method: 'GET', path: '/api/mkt/v1/packs' },
    emptyText: () => i18n.t('packs.registry.emptyTitle', { ns: 'market' }),
  },
  {
    name: 'withdrawal list',
    element: <WithdrawalListPage />,
    path: '/channels/withdrawals',
    roles: withdrawalRoles,
    route: { method: 'GET', path: '/api/chn/v1/withdrawals' },
    emptyText: /no .*withdrawal|nothing/i,
  },
];

describe.each(screens)('$name states', (s) => {
  it('shows the empty state when the list is empty', async () => {
    mockApi([{ ...s.route, respond: () => ({ body: emptyPage }) }]);
    renderScreen(s.element, { path: s.path, url: s.path });
    expect(
      await screen.findByText(typeof s.emptyText === 'function' ? s.emptyText() : s.emptyText),
    ).toBeInTheDocument();
  });

  it('shows the problem title when the request fails', async () => {
    mockApi([
      { ...s.route, respond: () => problem(500, 'SYN-ERR', 'Synthetic list failure for tests') },
    ]);
    renderScreen(s.element, { path: s.path, url: s.path });
    expect(await screen.findByText('Synthetic list failure for tests')).toBeInTheDocument();
  });

  it('shows the no-permission state without any API call for an unrelated role', () => {
    session.user.roles = ['Customer.Portal'];
    const api = mockApi([]);
    renderScreen(<RequireRole roles={s.roles} />, { path: s.path, url: s.path });
    expect(screen.getByText('No permission')).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });
});

describe('AI control plane states', () => {
  it('shows the no-permission state for a non-governor', () => {
    session.user.roles = ['Staff.ClaimsHandler'];
    const api = mockApi([]);
    renderScreen(<RequireRole roles={aiAdminRoles} />, { path: '/admin/ai', url: '/admin/ai' });
    expect(screen.getByText('No permission')).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });

  it('renders the control page even when a feature state cannot be loaded', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/plt/v1/ai-toggle/resolve',
        respond: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Synthetic forbidden'),
      },
    ]);
    renderScreen(<AiControlPage />, { path: '/admin/ai', url: '/admin/ai' });
    await vi.waitFor(() => {
      expect(api.callsTo('POST', '/api/plt/v1/ai-toggle/resolve').length).toBeGreaterThan(0);
    });
    expect(screen.getByRole('heading', { level: 1 })).toBeInTheDocument();
  });
});
