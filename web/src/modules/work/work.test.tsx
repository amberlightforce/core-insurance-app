import { screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { WorkPage } from './WorkPage';

vi.setConfig({ testTimeout: 30_000 });

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

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const approvals = {
  items: [
    {
      request: {
        requestId: 'req-1',
        type: 'CLM.PAYMENT',
        status: 'PendingApproval',
        requestedAt: '2026-10-07T09:00:00Z',
      },
    },
  ],
  nextCursor: null,
};

const dsar = {
  items: [
    {
      request: {
        dsarId: 'dsar-1',
        requestType: 'ERASURE',
        status: 'RECEIVED',
        deadline: '2026-11-01T00:00:00Z',
      },
    },
    {
      request: {
        dsarId: 'dsar-2',
        requestType: 'ACCESS',
        status: 'COMPLETED',
        deadline: '2026-10-01T00:00:00Z',
      },
    },
  ],
  nextCursor: null,
};

describe('WorkPage', () => {
  it('shows only the queues the roles allow and never calls the others', async () => {
    signInAs(['Staff.ClaimsHandler']);
    const api = mockApi([
      { method: 'GET', path: '/api/plt/v1/approval', respond: () => ({ body: approvals }) },
    ]);
    renderScreen(<WorkPage />, { path: '/work', url: '/work' });

    const section = await screen.findByRole('region', { name: 'Εγκρίσεις αποζημιώσεων' });
    expect(await within(section).findAllByRole('link')).not.toHaveLength(0);
    expect(within(section).getByRole('link', { name: /Πληρωμή|CLM\.PAYMENT/ })).toHaveAttribute(
      'href',
      '/claims/approvals/req-1',
    );
    expect(screen.queryByRole('region', { name: 'Αιτήματα υποκειμένων δεδομένων' })).toBeNull();
    expect(screen.queryByRole('region', { name: 'Παραπομπές ανάληψης κινδύνου' })).toBeNull();
    expect(api.calls.every((c) => c.url.pathname === '/api/plt/v1/approval')).toBe(true);
  });

  it('keeps the other sections working when one queue fails', async () => {
    signInAs(['Staff.ClaimsManager', 'Staff.ComplianceOfficer']);
    mockApi([
      {
        method: 'GET',
        path: '/api/plt/v1/approval',
        respond: () => problem(500, 'PLT-ERR-INTERNAL', 'Σφάλμα διακομιστή'),
      },
      { method: 'GET', path: '/api/cmp/v1/dsar/query', respond: () => ({ body: dsar }) },
    ]);
    renderScreen(<WorkPage />, { path: '/work', url: '/work' });

    const failed = await screen.findByRole('region', { name: 'Εγκρίσεις αποζημιώσεων' });
    expect(await within(failed).findByText('Σφάλμα διακομιστή')).toBeInTheDocument();
    expect(within(failed).getByRole('button', { name: /Δοκιμή|Επανάληψη/ })).toBeInTheDocument();

    const ok = await screen.findByRole('region', { name: 'Αιτήματα υποκειμένων δεδομένων' });
    const link = await within(ok).findByRole('link', { name: 'Διαγραφή' });
    expect(link).toHaveAttribute('href', '/compliance/dsar/dsar-1');
    // The completed case is not work.
    expect(within(ok).queryByRole('link', { name: 'Πρόσβαση' })).toBeNull();
  });

  it('says so when no role has a queue', () => {
    signInAs(['Staff.Finance']);
    mockApi([]);
    renderScreen(<WorkPage />, { path: '/work', url: '/work' });
    expect(screen.getByText('Καμία ουρά εργασίας για τους ρόλους σας')).toBeInTheDocument();
  });
});
