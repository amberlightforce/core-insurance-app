import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { mockApi, renderScreen, type MockRoute } from '../../../test/mockApi';
import type { DelinquencyCase } from './api';
import { DelinquencyDetailPage } from './DelinquencyDetailPage';
import { DelinquencyListPage } from './DelinquencyListPage';

vi.setConfig({ testTimeout: 60_000 });

const id = 'e0000000-1111-4222-8333-000000000001';

beforeEach(() => {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'billing', name: 'billing', roles: ['Staff.Billing'] },
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function kase(over: Partial<DelinquencyCase> = {}): DelinquencyCase {
  return {
    delinquencyId: id,
    billingAccountId: 'acc00000-1111-4222-8333-000000000001',
    state: 'CLOCK_RUNNING',
    overdueAmount: { amount: '120.00', currency: 'EUR' },
    openedAt: '2026-10-01T09:00:00Z',
    notificationDate: '2026-10-05',
    deadline: new Date(Date.now() + 10 * 86_400_000).toISOString(),
    contacts: [],
    ...over,
  };
}

describe('delinquency list', () => {
  it('shows status, amount and days left', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/bil/v1/delinquencies',
        respond: () => ({ body: { items: [{ process: kase() }], nextCursor: null, limit: 100 } }),
      },
    ]);
    renderScreen(<DelinquencyListPage />, {
      path: '/billing/delinquency',
      url: '/billing/delinquency',
    });
    const grid = await screen.findByRole('grid', { name: 'Υποθέσεις καθυστέρησης' });
    expect(within(grid).getByText('Τρέχει χρονόμετρο')).toBeInTheDocument();
    expect(within(grid).getByText('120,00 €')).toBeInTheDocument();
    expect(within(grid).getByText(/ημέρες απομένουν/)).toBeInTheDocument();
  });
});

describe('delinquency detail', () => {
  it('shows the timeline and records a contact with an idempotency key', async () => {
    const posts: { body: unknown; key: string | null }[] = [];
    const routes: MockRoute[] = [
      {
        method: 'GET',
        path: `/api/bil/v1/delinquencies/${id}`,
        respond: () => ({ body: { process: kase() } }),
      },
      {
        method: 'POST',
        path: '/api/bil/v1/delinquencies/record-contact',
        respond: (request) => {
          posts.push({ body: request.body, key: request.headers.get('Idempotency-Key') });
          return { body: {} };
        },
      },
    ];
    mockApi(routes);
    renderScreen(<DelinquencyDetailPage />, {
      path: '/billing/delinquency/:delinquencyId',
      url: `/billing/delinquency/${id}`,
    });
    expect(await screen.findByRole('list', { name: 'Χρονολόγιο' })).toBeInTheDocument();
    const form = screen.getByRole('form', { name: 'Καταγραφή επικοινωνίας' });
    await userEvent.type(within(form).getByLabelText('Αποτέλεσμα'), 'Θα πληρώσει');
    await userEvent.click(within(form).getByRole('button', { name: 'Καταγραφή' }));
    await vi.waitFor(() => {
      expect(posts).toHaveLength(1);
    });
    expect(posts[0]?.key).toBeTruthy();
    expect(posts[0]?.body).toMatchObject({ data: { delinquencyId: id, channel: 'PHONE' } });
  });

  it('explains the missing clock when the letter was returned', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/bil/v1/delinquencies/${id}`,
        respond: () => ({
          body: { process: kase({ state: 'NOTICE_RETURNED', deadline: null }) },
        }),
      },
    ]);
    renderScreen(<DelinquencyDetailPage />, {
      path: '/billing/delinquency/:delinquencyId',
      url: `/billing/delinquency/${id}`,
    });
    expect(await screen.findByText(/Δεν ξεκινά χρονόμετρο/)).toBeInTheDocument();
  });
});
