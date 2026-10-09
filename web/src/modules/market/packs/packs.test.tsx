import { screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import i18n from '../../../i18n';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { expectNoA11yViolations } from '../../../test/axe';
import { ActivationPage } from './ActivationPage';
import { ActivationDialog } from './ActivationDialog';
import { PackDetailPage } from './PackDetailPage';
import { PackRegistryPage } from './PackRegistryPage';
import { RequirePackRole } from './RequirePackRole';
import * as fx from './fixtures';

const session = vi.hoisted(() => ({
  user: {
    id: 'designauth',
    actorKey: 'USER:dev:designauth',
    name: 'Synthetic checker',
    roles: ['Platform.DesignAuthority'],
  },
}));
vi.mock('../../../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../dev-auth/devAuth')>()),
  useDevSession: () => ({ user: session.user }),
}));
vi.setConfig({ testTimeout: 60_000 });
beforeEach(async () => {
  session.user = {
    id: 'designauth',
    actorKey: 'USER:dev:designauth',
    name: 'Synthetic checker',
    roles: ['Platform.DesignAuthority'],
  };
  await i18n.changeLanguage('en');
});
afterEach(() => vi.unstubAllGlobals());

describe('pack lifecycle and decisions', () => {
  it('shows loading then a request failure with retry', async () => {
    let finish: (() => void) | undefined;
    const pending = new Promise<void>((resolve) => {
      finish = resolve;
    });
    mockApi([
      {
        method: 'GET',
        path: '/api/mkt/v1/packs',
        respond: async () => {
          await pending;
          return problem(500, 'MKT-ERR-UNAVAILABLE', 'Synthetic registry unavailable');
        },
      },
    ]);
    renderScreen(<PackRegistryPage />, { path: '/admin/packs', url: '/admin/packs' });
    expect(screen.getAllByText('Loading…').length).toBeGreaterThan(0);
    finish?.();
    await screen.findByText('Synthetic registry unavailable');
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });

  it('guards the pack route for an unrelated role without an API request', async () => {
    session.user.roles = ['Staff.ClaimsHandler'];
    const api = mockApi([]);
    renderScreen(<RequirePackRole />, { path: '/admin/packs', url: '/admin/packs' });
    expect(screen.getByText('No permission')).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });
  it('requires a valid reason and the exact dry-run preview before a real request', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/mkt/v1/packs/rollback',
        respond: ({ url }) => ({
          body:
            url.searchParams.get('dryRun') === 'true'
              ? { preview: fx.preview, activationId: null }
              : { preview: null, activationId: fx.activationId },
        }),
      },
    ]);
    const submitted = vi.fn();
    const { user } = renderScreen(
      <ActivationDialog
        pack={{
          ...fx.pack,
          activeVersions: fx.pack.activeVersions.map((a) => ({ ...a, version: '0.2.0' })),
        }}
        kind="ROLLBACK"
        isOpen
        onClose={() => {}}
        onSubmitted={submitted}
      />,
      { path: '/admin/packs', url: '/admin/packs' },
    );
    await user.click(screen.getByRole('button', { name: /Roll back to version/ }));
    await user.click(await screen.findByRole('option', { name: /0.1.0/ }));
    const reason = screen.getByRole('textbox', { name: /Reason/ });
    await user.type(reason, 'Too short');
    expect(screen.getByRole('button', { name: 'Preview' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(screen.getByRole('button', { name: 'Submit request' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await user.clear(reason);
    await user.type(reason, 'Synthetic correction reason');
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText(/^Key changes/);
    expect(api.calls[0]?.url.searchParams.get('dryRun')).toBe('true');
    expect(api.calls).toHaveLength(1);
    expect(screen.getAllByText(/tax.treatment.rule.synthetic_/)).toHaveLength(8);
    await user.type(reason, ' updated');
    expect(screen.getByRole('button', { name: 'Submit request' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Submit request' })).not.toHaveAttribute(
        'aria-disabled',
        'true',
      ),
    );
    await user.click(screen.getByRole('button', { name: 'Submit request' }));
    await waitFor(() => expect(submitted).toHaveBeenCalledWith(fx.activationId));
    expect(api.calls).toHaveLength(3);
    expect(api.calls[2]?.url.searchParams.has('dryRun')).toBe(false);
    expect(api.calls[2]?.body).toEqual({
      pack: 'GR',
      legalEntity: 'GR-TEST',
      toVersion: '0.1.0',
      reason: 'Synthetic correction reason updated',
    });
    expect(api.calls[2]?.headers.get('Idempotency-Key')).not.toBe(
      api.calls[1]?.headers.get('Idempotency-Key'),
    );
  });
  it('shows the rolled-back version with its effective window and issued hash', async () => {
    mockApi([
      { method: 'GET', path: `/api/mkt/v1/packs/${fx.packId}`, respond: () => ({ body: fx.pack }) },
    ]);
    const { container } = renderScreen(<PackDetailPage />, {
      path: '/admin/packs/:packId',
      url: `/admin/packs/${fx.packId}`,
    });
    await screen.findByTestId('version-0.2.0');
    expect(screen.getByTestId('version-0.2.0')).toHaveAttribute('data-state', 'ROLLED_BACK');
    expect(screen.getAllByText(fx.issuedHash).length).toBeGreaterThan(0);
    expect(screen.getByText('Window in force')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it.each(['USER:dev:designauth', 'designauth', 'SERVICE:worker'])(
    'keeps maker or unverified actor %s read-only',
    async (requestedBy) => {
      const api = mockApi([
        {
          method: 'GET',
          path: `/api/mkt/v1/pack-activations/${fx.activationId}`,
          respond: () => ({ body: { ...fx.pendingActivation, requestedBy } }),
        },
      ]);
      renderScreen(<ActivationPage />, {
        path: '/admin/packs/:packId/activations/:activationId',
        url: `/admin/packs/${fx.packId}/activations/${fx.activationId}`,
      });
      await screen.findByText('Waiting for a checker');
      expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
      expect(api.callsTo('POST', '/api/mkt/v1/pack-activations/decide')).toHaveLength(0);
    },
  );

  it('keeps a checker without a server actor key read-only', async () => {
    session.user.actorKey = '';
    mockApi([
      {
        method: 'GET',
        path: `/api/mkt/v1/pack-activations/${fx.activationId}`,
        respond: () => ({ body: fx.pendingActivation }),
      },
    ]);
    renderScreen(<ActivationPage />, {
      path: '/admin/packs/:packId/activations/:activationId',
      url: `/admin/packs/${fx.packId}/activations/${fx.activationId}`,
    });
    await screen.findByText(/maker identity could not be verified/);
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });

  it('requires a decision reason and sends one explicit checker approval', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: `/api/mkt/v1/pack-activations/${fx.activationId}`,
        respond: () => ({ body: fx.pendingActivation }),
      },
      {
        method: 'POST',
        path: '/api/mkt/v1/pack-activations/decide',
        respond: () => ({ body: { activation: fx.pack.activationHistory[0] } }),
      },
    ]);
    const { user } = renderScreen(<ActivationPage />, {
      path: '/admin/packs/:packId/activations/:activationId',
      url: `/admin/packs/${fx.packId}/activations/${fx.activationId}`,
    });
    await user.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(api.callsTo('POST', '/api/mkt/v1/pack-activations/decide')).toHaveLength(0);
    await screen.findByText('Write the reason for the decision.');
    await user.type(
      screen.getByRole('textbox', { name: /Reason for the decision/ }),
      'Synthetic checker approves',
    );
    await user.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() =>
      expect(api.callsTo('POST', '/api/mkt/v1/pack-activations/decide')).toHaveLength(1),
    );
    expect(api.callsTo('POST', '/api/mkt/v1/pack-activations/decide')[0]?.body).toEqual({
      activationId: fx.activationId,
      decision: 'APPROVE',
      reason: 'Synthetic checker approves',
    });
  });

  it('shows an empty registry without a navigation loop', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/mkt/v1/packs',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    renderScreen(<PackRegistryPage />, { path: '/admin/packs', url: '/admin/packs' });
    await screen.findByText('No packs');
    expect(api.callsTo('GET', '/api/mkt/v1/packs')).toHaveLength(1);
  });
});
