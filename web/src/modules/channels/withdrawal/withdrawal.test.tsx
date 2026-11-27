import { screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, renderScreen } from '../../../test/mockApi';
import { WithdrawalDetailPage } from './WithdrawalDetailPage';
import { WithdrawalListPage } from './WithdrawalListPage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
const reqId = 'b1000000-1111-4222-8333-000000000001';
describe('staff screens', () => {
  it('lists requests', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/chn/v1/withdrawals',
        respond: () => ({
          body: {
            items: [
              {
                withdrawalRequestId: reqId,
                requestNumber: 'WDR000000001',
                status: 'CONFIRMED',
                policyId,
              },
            ],
            nextCursor: null,
          },
        }),
      },
    ]);
    const { container } = renderScreen(<WithdrawalListPage />, { path: '/l', url: '/l' });
    await screen.findByText('WDR000000001');
    // Scoped to the page: earlier tests leave toast/portal nodes outside the cleaned-up container.
    await expectNoA11yViolations(container);
  });

  it('closes a confirmed request', async () => {
    let status = 'CONFIRMED';
    const api = mockApi([
      {
        method: 'GET',
        path: `/api/chn/v1/withdrawals/${reqId}`,
        respond: () => ({
          body: { withdrawalRequestId: reqId, requestNumber: 'WDR000000001', status, policyId },
        }),
      },
      {
        method: 'POST',
        path: `/api/chn/v1/withdrawals/${reqId}/close`,
        respond: () => {
          status = 'PROCESSED';
          return { body: { requestNumber: 'WDR000000001', status } };
        },
      },
    ]);
    const { user } = renderScreen(<WithdrawalDetailPage />, { path: '/d/:id', url: `/d/${reqId}` });
    await user.click(await screen.findByRole('button', { name: 'Ολοκληρώθηκε η επιστροφή' }));
    await waitFor(() => {
      expect(api.callsTo('POST', `/api/chn/v1/withdrawals/${reqId}/close`)).toHaveLength(1);
    });
    expect(api.callsTo('POST', `/api/chn/v1/withdrawals/${reqId}/close`)[0]?.body).toEqual({
      outcome: 'PROCESSED',
    });
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Ολοκληρώθηκε η επιστροφή' })).toBeNull();
    });
  });
});
