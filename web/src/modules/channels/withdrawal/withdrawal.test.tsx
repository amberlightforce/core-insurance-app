import { screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, renderScreen } from '../../../test/mockApi';
import { WithdrawalDeclarePage } from './WithdrawalDeclarePage';
import { WithdrawalDetailPage } from './WithdrawalDetailPage';
import { WithdrawalListPage } from './WithdrawalListPage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
const reqId = 'b1000000-1111-4222-8333-000000000001';
const receipt = {
  requestNumber: 'WDR000000001',
  declaredAt: '2026-10-19T08:00:00Z',
  confirmBy: '2026-11-01T21:59:59Z',
};

async function fillAndSubmit(user: ReturnType<typeof renderScreen>['user']) {
  await user.type(screen.getByLabelText(/Ονοματεπώνυμο/), 'Μαρία Παπά');
  await user.type(screen.getByLabelText(/Αναγνωριστικό ασφαλιστηρίου/), policyId);
  await user.type(screen.getByLabelText(/E-mail/), 'maria@example.gr');
  await user.click(screen.getByRole('button', { name: 'Υποβολή δήλωσης' }));
}

describe('WithdrawalDeclarePage', () => {
  it('declares then confirms with the token', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/chn/v1/withdrawals/declare',
        respond: () => ({
          body: {
            requestNumber: receipt.requestNumber,
            status: 'DECLARED',
            confirmToken: 'tok-1',
            receipt,
          },
        }),
      },
      {
        method: 'POST',
        path: '/api/chn/v1/withdrawals/confirm',
        respond: () => ({
          body: {
            requestNumber: receipt.requestNumber,
            status: 'CONFIRMED',
            confirmedAt: '2026-10-19T08:05:00Z',
          },
        }),
      },
    ]);
    const { user } = renderScreen(<WithdrawalDeclarePage />, { path: '/w', url: '/w' });
    await fillAndSubmit(user);
    await user.click(await screen.findByRole('button', { name: 'Επιβεβαίωση υπαναχώρησης' }));
    await screen.findByText('Η υπαναχώρηση επιβεβαιώθηκε');
    expect(api.callsTo('POST', '/api/chn/v1/withdrawals/confirm')[0]?.body).toEqual({
      requestNumber: receipt.requestNumber,
      token: 'tok-1',
    });
    expect(api.callsTo('POST', '/api/chn/v1/withdrawals/declare')[0]?.body).toMatchObject({
      contractId: policyId,
      eMail: 'maria@example.gr',
    });
  });

  it('shows the expired state for a late declaration (HTTP 200, DECLINED)', async () => {
    mockApi([
      {
        method: 'POST',
        path: '/api/chn/v1/withdrawals/declare',
        respond: () => ({
          body: {
            requestNumber: receipt.requestNumber,
            status: 'DECLINED',
            declineCode: 'CHN-ERR-RIGHT-EXPIRED',
            receipt,
          },
        }),
      },
    ]);
    const { user } = renderScreen(<WithdrawalDeclarePage />, { path: '/w', url: '/w' });
    await fillAndSubmit(user);
    await screen.findByText('Η προθεσμία υπαναχώρησης έχει λήξει');
    expect(screen.queryByRole('button', { name: 'Επιβεβαίωση υπαναχώρησης' })).toBeNull();
  });

  it('validates before calling the API', async () => {
    const api = mockApi([]);
    const { user } = renderScreen(<WithdrawalDeclarePage />, { path: '/w', url: '/w' });
    await user.click(screen.getByRole('button', { name: 'Υποβολή δήλωσης' }));
    await screen.findByText('Εισαγάγετε έγκυρη διεύθυνση e-mail.');
    expect(api.calls).toHaveLength(0);
  });
});

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
    renderScreen(<WithdrawalListPage />, { path: '/l', url: '/l' });
    await screen.findByText('WDR000000001');
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
