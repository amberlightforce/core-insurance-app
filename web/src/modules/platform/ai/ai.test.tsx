import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { mockApi, renderScreen } from '../../../test/mockApi';
import { AiControlPage } from './AiControlPage';
import { RequireRole } from '../../../auth/RequireRole';
import { aiAdminRoles } from '../../../auth/roles';

const session = vi.hoisted(() => ({ roles: ['Platform.Admin'] as string[] }));
vi.mock('../../../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../dev-auth/devAuth')>()),
  useDevSession: () => ({
    user: { id: 'su', actorKey: 'USER:dev:su', name: 'Synthetic admin', roles: session.roles },
  }),
}));
vi.setConfig({ testTimeout: 60_000 });

let kill = false;
const enabled = new Set<string>();
const routes = () => [
  {
    method: 'POST',
    path: '/api/plt/v1/ai-toggle/resolve',
    respond: (r: { body: unknown }) => {
      const featureId = (r.body as { featureId: string }).featureId;
      return {
        body: {
          featureId,
          enabled: enabled.has(featureId),
          allowed: enabled.has(featureId) && !kill,
          killSwitchActive: kill,
          registerStatus: 'APPROVED',
        },
      };
    },
  },
  {
    method: 'POST',
    path: '/api/plt/v1/ai-toggle/set',
    respond: (r: { body: unknown }) => {
      const b = r.body as { featureId: string; enabled: boolean };
      if (b.enabled) enabled.add(b.featureId);
      else enabled.delete(b.featureId);
      return { body: { featureId: b.featureId, enabled: b.enabled, version: 1 } };
    },
  },
  {
    method: 'POST',
    path: '/api/plt/v1/ai/kill-switch',
    respond: (r: { body: unknown }) => {
      kill = (r.body as { action: string }).action === 'Activate';
      return { body: { scope: 'GLOBAL', active: kill } };
    },
  },
];

beforeEach(async () => {
  kill = false;
  enabled.clear();
  session.roles = ['Platform.Admin'];
  await i18n.changeLanguage('en');
});
afterEach(() => vi.unstubAllGlobals());

describe('AI control page', () => {
  it('lists features, toggles one and logs it', async () => {
    const api = mockApi(routes());
    const { user } = renderScreen(<AiControlPage />, { path: '/admin/ai', url: '/admin/ai' });
    const sw = await screen.findByRole('switch', { name: 'Enable AI-CLM-01' });
    await waitFor(() => expect(sw).not.toBeDisabled());
    await user.click(sw);
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/plt/v1/ai-toggle/set')).toHaveLength(1);
    });
    expect(api.callsTo('POST', '/api/plt/v1/ai-toggle/set')[0]?.body).toEqual({
      featureId: 'AI-CLM-01',
      enabled: true,
    });
    const log = await screen.findByRole('table', { name: 'Interaction log (this session)' });
    expect(within(log).getByText('Enabled')).toBeInTheDocument();
  });

  it('asks for confirmation and a reason before activating the kill switch', async () => {
    const api = mockApi(routes());
    const { user } = renderScreen(<AiControlPage />, { path: '/admin/ai', url: '/admin/ai' });
    await user.click(await screen.findByRole('button', { name: 'Stop all AI' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(api.callsTo('POST', '/api/plt/v1/ai/kill-switch')).toHaveLength(0);
    await user.click(within(dialog).getByRole('button', { name: 'Stop all AI' }));
    expect(await within(dialog).findByText('Enter a reason.')).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox'), 'Incident 42');
    await user.click(within(dialog).getByRole('button', { name: 'Stop all AI' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/plt/v1/ai/kill-switch')).toHaveLength(1);
    });
    expect(api.callsTo('POST', '/api/plt/v1/ai/kill-switch')[0]?.body).toEqual({
      action: 'Activate',
      scope: 'GLOBAL',
      reason: 'Incident 42',
    });
    expect(await screen.findByText('All AI is stopped')).toBeInTheDocument();
  });

  it('guards the route for non-administrators', () => {
    session.roles = ['Staff.ClaimsHandler'];
    mockApi([]);
    renderScreen(<RequireRole roles={aiAdminRoles} />, { path: '/admin/ai', url: '/admin/ai' });
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
  });
});
