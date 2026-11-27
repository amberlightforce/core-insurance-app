import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { WithdrawalPage } from './WithdrawalPage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
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

const render = () => renderScreen(<WithdrawalPage />, { path: '/w', url: '/w' });

describe('portal WithdrawalPage', () => {
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
    const { user } = render();
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
    // Public flow: no bearer token is sent when nobody is signed in.
    expect(api.calls.every((c) => !c.headers.has('Authorization'))).toBe(true);
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
    const { user } = render();
    await fillAndSubmit(user);
    await screen.findByText('Η προθεσμία υπαναχώρησης έχει λήξει');
    expect(screen.queryByRole('button', { name: 'Επιβεβαίωση υπαναχώρησης' })).toBeNull();
  });

  it('asks the customer to wait when the API rate-limits (429)', async () => {
    mockApi([
      {
        method: 'POST',
        path: '/api/chn/v1/withdrawals/declare',
        respond: () => problem(429, 'PLT-ERR-RATE-LIMITED', 'Too many requests'),
      },
    ]);
    const { user } = render();
    await fillAndSubmit(user);
    expect(
      await screen.findByText('Πάρα πολλές προσπάθειες. Περιμένετε ένα λεπτό και δοκιμάστε ξανά.'),
    ).toBeInTheDocument();
  });

  it('validates before calling the API', async () => {
    const api = mockApi([]);
    const { user } = render();
    await user.click(screen.getByRole('button', { name: 'Υποβολή δήλωσης' }));
    await screen.findByText('Συμπληρώστε μια έγκυρη διεύθυνση e-mail.');
    expect(api.calls).toHaveLength(0);
  });
});
