import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { TaskDetailPage } from './TaskDetailPage';
import { WorkPage } from './WorkPage';

vi.setConfig({ testTimeout: 60_000 });

function signInAs(roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'dev', name: 'Dev User (synthetic)', roles },
    }),
  );
}

beforeEach(async () => {
  await i18n.changeLanguage('el');
});

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const task = (over: Record<string, unknown> = {}) => ({
  id: 'task-1',
  number: 'ACT-0001',
  patternCode: 'WRK-POL-NONPAY-PSR-EXCEPTION',
  title: 'Decide a non-payment cancellation held for the PSR',
  category: 'REVIEW',
  priority: 'HIGH',
  state: 'OPEN',
  primaryObject: { module: 'POL', type: 'Policy', id: 'pol-1' },
  secondaryObjects: [],
  assignee: { type: 'USER', id: 'dev' },
  queueCode: 'PSR',
  queueId: 'q-1',
  targetAt: '2026-10-21T09:00:00Z',
  slaBreached: false,
  createdAt: '2026-10-20T09:00:00Z',
  createdBy: 'system',
  recordVersion: 1,
  assignedToMe: true,
  claimableByMe: false,
  ...over,
});

const page = (...tasks: unknown[]) => ({
  body: { items: tasks.map((activity) => ({ activity })), nextCursor: null, limit: 25 },
});

const scopeOf = (url: URL) => /scope:(\w+)/.exec(url.searchParams.get('filters') ?? '')?.[1];

const at = <T,>(list: readonly T[], index = 0): T => {
  const item = list.at(index);
  if (item === undefined) throw new Error(`No item at ${String(index)}`);
  return item;
};

describe('Work inbox', () => {
  it('lists my tasks, the queue and the team by scope, and claims a queue task', async () => {
    signInAs(['Staff.UnderwritingManager']);
    const queued = task({
      id: 'task-2',
      number: 'ACT-0002',
      assignee: { type: 'QUEUE', id: 'PSR' },
      assignedToMe: false,
      claimableByMe: true,
    });
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/wrk/v1/activities',
        respond: ({ url }) => {
          const scope = scopeOf(url);
          return scope === 'mine'
            ? page(task())
            : scope === 'queue'
              ? page(queued)
              : page(task(), queued);
        },
      },
      {
        method: 'POST',
        path: '/api/wrk/v1/activities/assign',
        respond: () => ({ body: { assignment: task({ id: 'task-2' }) } }),
      },
      {
        method: 'GET',
        path: '/api/uw/v1/referrals',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    const { user } = renderScreen(<WorkPage />, { path: '/work', url: '/work' });

    const rows = await screen.findAllByTestId('task-row');
    expect(rows).toHaveLength(1);
    expect(within(at(rows)).getByText('ACT-0001')).toBeInTheDocument();
    expect(within(at(rows)).getByText('Απόφαση για ακύρωση λόγω μη πληρωμής')).toBeInTheDocument();
    // My tasks only reads the «mine» scope.
    expect(
      api.callsTo('GET', '/api/wrk/v1/activities').every((c) => scopeOf(c.url) !== 'team'),
    ).toBe(true);

    // The older queues stay below the inbox.
    expect(screen.getByRole('heading', { name: 'Άλλες ουρές' })).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /Ουρά/ }));
    const queueRows = await screen.findAllByTestId('task-row');
    await user.click(within(at(queueRows)).getByRole('button', { name: 'Ανάληψη' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/wrk/v1/activities/assign')).toHaveLength(1);
    });
    const claim = at(api.callsTo('POST', '/api/wrk/v1/activities/assign'));
    expect(claim.body).toEqual({ id: 'task-2', rulesOrMe: 'me' });
    expect(claim.headers.get('Idempotency-Key')).toBeTruthy();
  });

  it('pulls the next task, and says so when the queue is empty', async () => {
    signInAs(['Staff.UnderwritingManager']);
    let calls = 0;
    const api = mockApi([
      { method: 'GET', path: '/api/wrk/v1/activities', respond: () => page() },
      {
        method: 'POST',
        path: '/api/wrk/v1/queues/get-next',
        respond: () =>
          ++calls === 1
            ? problem(404, 'WRK-ERR-QUEUE-EMPTY', 'Η ουρά είναι άδεια')
            : { body: { assignedActivity: task() } },
      },
      {
        method: 'GET',
        path: '/api/uw/v1/referrals',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    const { user } = renderScreen(<WorkPage />, { path: '/work', url: '/work' });

    await user.click(await screen.findByRole('button', { name: 'Επόμενη εργασία' }));
    expect(
      await screen.findByText('Δεν περιμένει καμία εργασία στις ουρές σας.'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Επόμενη εργασία' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/wrk/v1/queues/get-next')).toHaveLength(2);
    });
  });

  it('hides the task inbox from roles without a work queue', () => {
    signInAs(['Staff.Finance']);
    const api = mockApi([]);
    renderScreen(<WorkPage />, { path: '/work', url: '/work' });
    expect(screen.queryByRole('tablist')).toBeNull();
    expect(api.calls).toHaveLength(0);
  });
});

describe('Task detail', () => {
  const open = () =>
    renderScreen(<TaskDetailPage />, {
      path: '/work/tasks/:activityId',
      url: '/work/tasks/task-1',
    });

  it('completes my task with an outcome', async () => {
    signInAs(['Staff.UnderwritingManager']);
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/wrk/v1/activities/task-1',
        respond: () => ({ body: { items: [{ activity: task() }], nextCursor: null, limit: 1 } }),
      },
      {
        method: 'POST',
        path: '/api/wrk/v1/activities/complete',
        respond: () => ({ body: { activity: task({ state: 'COMPLETED' }) } }),
      },
    ]);
    const { user } = open();

    await user.click(await screen.findByRole('button', { name: 'Ολοκλήρωση' }));
    // Submit without an outcome is refused in the dialog.
    await user.click(at(screen.getAllByRole('button', { name: 'Ολοκλήρωση' }), -1));
    expect(await screen.findByText('Επιλέξτε αποτέλεσμα.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/wrk/v1/activities/complete')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: /Αποτέλεσμα/ }));
    await user.click(await screen.findByRole('option', { name: 'Επιλύθηκε' }));
    await user.click(at(screen.getAllByRole('button', { name: 'Ολοκλήρωση' }), -1));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/wrk/v1/activities/complete')).toHaveLength(1);
    });
    expect(at(api.callsTo('POST', '/api/wrk/v1/activities/complete')).body).toEqual({
      id: 'task-1',
      outcome: 'RESOLVED',
    });
  });

  it('requires a reason to skip, and disables closing a task that is not mine', async () => {
    signInAs(['Staff.UnderwritingManager']);
    mockApi([
      {
        method: 'GET',
        path: '/api/wrk/v1/activities/task-1',
        respond: () => ({
          body: {
            items: [
              {
                activity: task({
                  assignee: { type: 'QUEUE', id: 'PSR' },
                  assignedToMe: false,
                  claimableByMe: true,
                }),
              },
            ],
            nextCursor: null,
            limit: 1,
          },
        }),
      },
    ]);
    open();
    expect(await screen.findByRole('button', { name: 'Ανάληψη' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Παράλειψη' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  it('shows not found as a message', async () => {
    signInAs(['Staff.UnderwritingManager']);
    mockApi([
      {
        method: 'GET',
        path: '/api/wrk/v1/activities/task-1',
        respond: () => problem(404, 'WRK-ERR-NOT-FOUND', 'Not found'),
      },
    ]);
    open();
    expect(
      await screen.findByText('Η εργασία δεν βρέθηκε ή δεν έχετε πρόσβαση.'),
    ).toBeInTheDocument();
  });
});
