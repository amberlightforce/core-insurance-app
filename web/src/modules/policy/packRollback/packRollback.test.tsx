import { screen, waitFor } from '@testing-library/react';
import { beforeEach, afterEach, expect, it, vi } from 'vitest';
import i18n from '../../../i18n';
import { mockApi, renderScreen } from '../../../test/mockApi';
import { expectNoA11yViolations } from '../../../test/axe';
import { ExceptionDetailPage } from './ExceptionDetailPage';
import { ExceptionQueuePage } from './ExceptionQueuePage';
import { RequireReviewerRole } from './RequireReviewerRole';
import * as fx from './fixtures';

const session = vi.hoisted(() => ({
  user: { id: 'uwsenior', name: 'Synthetic reviewer', roles: ['Staff.UnderwritingManager'] },
}));
vi.mock('../../../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../dev-auth/devAuth')>()),
  useDevSession: () => ({ user: session.user }),
}));
vi.setConfig({ testTimeout: 60_000 });
beforeEach(async () => {
  session.user.roles = ['Staff.UnderwritingManager'];
  await i18n.changeLanguage('en');
});
afterEach(() => vi.unstubAllGlobals());

it('records an explicit outcome and reason without re-rating the policy', async () => {
  const api = mockApi([
    {
      method: 'GET',
      path: `/api/pol/v1/pack-rollback-exceptions/${fx.exceptionId}`,
      respond: () => ({ body: fx.exception }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/pack-rollback-exceptions/review',
      respond: () => ({ body: fx.exception }),
    },
  ]);
  const { user, container } = renderScreen(<ExceptionDetailPage />, {
    path: '/policies/pack-rollback/:exceptionId',
    url: `/policies/pack-rollback/${fx.exceptionId}`,
  });
  await screen.findByText('Correction is not available yet');
  expect(screen.getAllByText('Issuance').length).toBeGreaterThan(0);
  expect(screen.queryByText('ISSUANCE')).not.toBeInTheDocument();
  expect(screen.getByText('Not created (work management is not built yet)')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /Re-rate/ })).not.toBeInTheDocument();
  await expectNoA11yViolations(container);
  await user.click(screen.getByRole('button', { name: 'Review…' }));
  await user.click(screen.getByRole('button', { name: 'Record review' }));
  expect(api.callsTo('POST', '/api/pol/v1/pack-rollback-exceptions/review')).toHaveLength(0);
  await screen.findByText('Choose an outcome.');
  await user.click(screen.getByRole('button', { name: /Outcome/ }));
  await user.click(await screen.findByRole('option', { name: 'Correction required' }));
  await user.type(
    screen.getByRole('textbox', { name: /Reason/ }),
    'Synthetic manual correction required',
  );
  await user.click(screen.getByRole('button', { name: 'Record review' }));
  await waitFor(() => {
    expect(api.callsTo('POST', '/api/pol/v1/pack-rollback-exceptions/review')).toHaveLength(1);
  });
  expect(api.callsTo('POST', '/api/pol/v1/pack-rollback-exceptions/review')[0]?.body).toEqual({
    exceptionId: fx.exceptionId,
    outcome: 'CORRECTION_REQUIRED',
    reason: 'Synthetic manual correction required',
  });
  expect(api.calls.filter((c) => c.method === 'POST')).toHaveLength(1);
});

it('filters open/reviewed exceptions using only status in the query', async () => {
  const api = mockApi([
    {
      method: 'GET',
      path: '/api/pol/v1/pack-rollback-exceptions',
      respond: () => ({ body: { items: [], nextCursor: null } }),
    },
  ]);
  const { user } = renderScreen(<ExceptionQueuePage />, {
    path: '/policies/pack-rollback',
    url: '/policies/pack-rollback',
  });
  await screen.findByText('No open exceptions');
  await user.click(screen.getByRole('radio', { name: 'Reviewed' }));
  await screen.findByText('No reviewed exceptions');
  expect(api.calls.map((c) => c.url.search)).toEqual(['?status=OPEN', '?status=REVIEWED']);
});

it('guards the exception route for an unrelated role', async () => {
  session.user.roles = ['Staff.ClaimsHandler'];
  const api = mockApi([]);
  renderScreen(<RequireReviewerRole />, {
    path: '/policies/pack-rollback',
    url: '/policies/pack-rollback',
  });
  await screen.findByText(/permission/i);
  expect(api.calls).toHaveLength(0);
});

it('loads the next exception page without dropping existing rows or changing the status filter', async () => {
  const api = mockApi([
    {
      method: 'GET',
      path: '/api/pol/v1/pack-rollback-exceptions',
      respond: ({ url }) => ({
        body: url.searchParams.has('cursor')
          ? {
              items: [
                {
                  ...fx.exception,
                  exceptionId: '018f8000-0000-7000-8000-000000000095',
                  policyNumber: 'SYNTHETIC-2026-0002',
                },
              ],
              nextCursor: null,
            }
          : { items: [fx.exception], nextCursor: 'opaque-next' },
      }),
    },
  ]);
  const { user } = renderScreen(<ExceptionQueuePage />, {
    path: '/policies/pack-rollback',
    url: '/policies/pack-rollback',
  });
  await screen.findByText('SYNTHETIC-2026-0001');
  await user.click(screen.getByRole('button', { name: 'Load more exceptions' }));
  await screen.findByText('SYNTHETIC-2026-0002');
  expect(screen.getByText('SYNTHETIC-2026-0001')).toBeInTheDocument();
  expect(api.calls).toHaveLength(2);
  expect(api.calls[1]?.url.searchParams.get('status')).toBe('OPEN');
  expect(api.calls[1]?.url.searchParams.get('cursor')).toBe('opaque-next');
  expect(screen.queryByRole('button', { name: 'Load more exceptions' })).not.toBeInTheDocument();
});
