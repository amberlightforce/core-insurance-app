import { screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { DevSession } from '../../dev-auth/devAuth';
import { expectNoA11yViolations } from '../../test/axe';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import type { BatchResponse, BatchView } from './api';
import { BatchPage } from './BatchPage';
import { MigrationHomePage } from './MigrationHomePage';
import { parseItems } from './parseItems';
import { RequireRole } from '../../auth/RequireRole';
import { migrationRoles } from '../../auth/roles';

vi.setConfig({ testTimeout: 60_000 });

const batchId = '11111111-1111-4111-8111-111111111111';

function signIn(roles: string[]) {
  const session: DevSession = {
    accessToken: 'test',
    expiresAt: '2099-01-01T00:00:00Z',
    user: { id: 'mig', name: 'mig', roles, actorKey: 'USER:dev:mig' },
  };
  sessionStorage.setItem('coreins.devSession', JSON.stringify(session));
}

function response(over: Partial<BatchView> = {}): BatchResponse {
  return {
    batch: {
      batchId,
      name: 'Κύμα 1',
      status: 'DRAFT',
      baselineHash: 'abcdef0123456789abcdef',
      contentHash: 'c0ffee',
      dryRunDone: true,
      itemCount: 2,
      counts: { IMPORTED: 1, FAILED: 1 },
      ...over,
    },
    outcomes: [
      {
        seq: 1,
        module: 'PTY',
        objectType: 'Party',
        legacyId: 'L-1',
        outcome: 'IMPORTED',
      },
      {
        seq: 2,
        module: 'PTY',
        objectType: 'Party',
        legacyId: 'L-2',
        outcome: 'FAILED',
        errorCode: 'MIG-ERR-X',
        message: 'Άκυρο ΑΦΜ',
      },
    ],
  };
}

beforeEach(() => {
  signIn(['Staff.MigrationManager']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
  localStorage.clear();
});

const renderBatch = () =>
  renderScreen(<BatchPage />, {
    path: '/admin/migration/batches/:batchId',
    url: `/admin/migration/batches/${batchId}`,
  });

describe('migration batch screens', () => {
  it('shows the outcomes and starts a dry-run-checked batch after a confirmation', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: `/api/mig/v1/batches/${batchId}`,
        respond: () => ({ body: response() }),
      },
      {
        method: 'POST',
        path: '/api/mig/v1/batches/start',
        respond: () => ({
          body: response({ status: 'PENDING_APPROVAL', startedBy: 'USER:dev:mig' }),
        }),
      },
    ]);
    const { container, user } = renderBatch();
    expect(await screen.findByText('L-2')).toBeInTheDocument();
    expect(screen.getByText('MIG-ERR-X · Άκυρο ΑΦΜ')).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.click(screen.getByRole('button', { name: 'Έναρξη δέσμης' }));
    await user.click(await screen.findByRole('button', { name: 'Αίτηση έγκρισης έναρξης' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/mig/v1/batches/start')).toHaveLength(1);
    });
    const call = api.callsTo('POST', '/api/mig/v1/batches/start')[0];
    expect(call?.body).toEqual({ batchId, dryRun: false });
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
    // The decision bar now shows the approval step.
    expect(await screen.findByRole('button', { name: 'Έγκριση' })).toBeInTheDocument();
  });

  it('keeps the start disabled until a dry run matches', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/mig/v1/batches/${batchId}`,
        respond: () => ({ body: response({ dryRunDone: false }) }),
      },
    ]);
    renderBatch();
    const start = await screen.findByRole('button', { name: 'Έναρξη δέσμης' });
    expect(start).toBeDisabled();
  });

  it('shows the server refusal when the starter tries to approve', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/mig/v1/batches/${batchId}`,
        respond: () => ({
          body: response({ status: 'PENDING_APPROVAL', startedBy: 'USER:dev:mig' }),
        }),
      },
      {
        method: 'POST',
        path: '/api/mig/v1/batches/approve',
        respond: () => problem(403, 'MIG-ERR-SOD', 'Forbidden', 'Maker and checker must differ.'),
      },
    ]);
    const { user } = renderBatch();
    await user.click(await screen.findByRole('button', { name: 'Έγκριση' }));
    await user.click(await screen.findByRole('button', { name: 'Έγκριση και εκτέλεση' }));
    expect(await screen.findByText('MIG-ERR-SOD')).toBeInTheDocument();
  });

  it('creates a batch from pasted items and opens it', async () => {
    const items = [{ module: 'PTY', objectType: 'Party', legacyId: 'L-1', payload: { a: 1 } }];
    expect(parseItems(JSON.stringify(items))).toHaveLength(1);
    expect(parseItems('[{"module":"PTY"}]')).toBeNull();
    const api = mockApi([
      { method: 'POST', path: '/api/mig/v1/batches', respond: () => ({ body: response() }) },
    ]);
    const { user } = renderScreen(<MigrationHomePage />, {
      path: '/admin/migration',
      url: '/admin/migration',
    });
    await user.type(screen.getByLabelText('Όνομα δέσμης'), 'Κύμα 1');
    await user.click(screen.getByLabelText(/Στοιχεία \(JSON\)/));
    await user.paste(JSON.stringify(items));
    await user.click(screen.getByRole('button', { name: 'Δημιουργία δέσμης' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/mig/v1/batches')).toHaveLength(1);
    });
    expect(api.calls[0]?.body).toEqual({ name: 'Κύμα 1', items });
  });

  it('looks up routing and lists the result on the timeline', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/mig/v1/routing/resolve',
        respond: () => ({
          body: { masterState: 'Transitioning', validFrom: '2026-01-01', validTo: '2026-06-01' },
        }),
      },
    ]);
    const { user } = renderScreen(<MigrationHomePage />, {
      path: '/admin/migration',
      url: '/admin/migration',
    });
    await user.type(screen.getByLabelText(/Κλειδί/), 'POL-1');
    await user.type(screen.getByLabelText(/^Ημερομηνία/), '2026-02-01');
    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    expect(await screen.findByText('Σε μετάβαση')).toBeInTheDocument();
    expect(screen.getByText('2026-02-01')).toBeInTheDocument();
    expect(api.calls[0]?.body).toEqual({ objectType: 'Policy', key: 'POL-1', date: '2026-02-01' });
  });

  it('shows the no-permission state to other roles', () => {
    signIn(['Staff.Billing']);
    renderScreen(<RequireRole roles={migrationRoles} />, {
      path: '/admin/migration',
      url: '/admin/migration',
    });
    expect(screen.getByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });
});
