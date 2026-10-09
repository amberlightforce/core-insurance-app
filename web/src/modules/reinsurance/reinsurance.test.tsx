import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PartySearchItem } from '../../api/types';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { expectNoA11yViolations } from '../../test/axe';
import { DecisionBar } from './DecisionBar';
import { EditContractPage, NewContractPage } from './ContractEditorPage';
import { RequireReinsuranceRole } from './RequireReinsuranceRole';
import { ReinsuranceHomePage } from './ReinsuranceHomePage';
import { treaty } from './fixtures';

vi.setConfig({ testTimeout: 60_000 });
function signIn(id: string, roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id, name: id, roles },
    }),
  );
}
beforeEach(() => {
  signIn('riacct', ['Staff.ReinsuranceAccountant']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});
const catalogueRoutes: MockRoute[] = [
  {
    method: 'POST',
    path: '/api/pfc/v1/product-versions/resolve',
    respond: () => ({ body: fx.resolved }),
  },
  {
    method: 'GET',
    path: `/api/pfc/v1/catalogue/${fx.hash}`,
    respond: () => ({ body: fx.catalogue }),
  },
  {
    method: 'GET',
    path: `/api/pty/v1/parties/11111111-1111-4111-8111-111111111111`,
    respond: () => ({ body: { party: fx.party() } }),
  },
];
describe('reinsurance registry and treaty editor', () => {
  it('loads the status-filtered registry and allows accountants to create a treaty', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/ri/v1/contracts',
        respond: () => ({ body: { items: [treaty], nextCursor: null } }),
      },
    ]);
    const { container, user } = renderScreen(<ReinsuranceHomePage />, {
      path: '/reinsurance',
      url: '/reinsurance?status=DRAFT',
    });
    expect(await screen.findByText('RI000000001')).toBeInTheDocument();
    expect(screen.getByText('500.000,00 xs 250.000,00')).toBeInTheDocument();
    expect(api.calls[0]?.url.searchParams.get('status')).toBe('DRAFT');
    expect(api.callsTo('GET', `/api/ri/v1/contracts/${treaty.contractId}`)).toHaveLength(0);
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Νέα σύμβαση' }));
    expect(screen.getByTestId('location')).toHaveTextContent('/reinsurance/contracts/new');
  });
  it('clears an empty status filter without losing the registry route', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/ri/v1/contracts',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    const { user } = renderScreen(<ReinsuranceHomePage />, {
      path: '/reinsurance',
      url: '/reinsurance?status=ACTIVE',
    });
    await user.click(await screen.findByRole('button', { name: 'Εκκαθάριση φίλτρων' }));
    expect(screen.getByTestId('location')).toHaveTextContent('/reinsurance');
    await screen.findByText('Δεν υπάρχουν συμβάσεις ακόμη');
  });
  it('refuses a save without coverages, limits or participants', async () => {
    const api = mockApi(catalogueRoutes);
    const { user } = renderScreen(<NewContractPage />, {
      path: '/reinsurance/contracts/new',
      url: '/reinsurance/contracts/new',
    });
    await screen.findByRole('checkbox', { name: 'Αστική ευθύνη αυτοκινήτου' });
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση προχείρου' }));
    expect(await screen.findByText('Διορθώστε τα πεδία πριν την αποθήκευση')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/ri/v1/contracts')).toHaveLength(0);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
  it('refuses a natural person as a reinsurer and leaves the signed lines unchanged', async () => {
    mockApi([
      ...catalogueRoutes,
      {
        method: 'GET',
        path: `/api/ri/v1/contracts/${treaty.contractId}`,
        respond: () => ({ body: { contract: treaty } }),
      },
      {
        method: 'POST',
        path: '/api/pty/v1/parties/search',
        respond: () => ({
          body: {
            items: [
              {
                partyId: fx.partyId,
                partyNumber: 'P000000021',
                partyType: 'PERSON',
                displayName: 'Φυσικό Πρόσωπο',
                status: 'PROSPECT',
                matchQuality: 'EXACT',
                similarity: '1',
              } satisfies PartySearchItem,
            ],
            nextCursor: null,
          },
        }),
      },
    ]);
    const { user } = renderScreen(<EditContractPage />, {
      path: '/reinsurance/contracts/:contractId/edit',
      url: `/reinsurance/contracts/${treaty.contractId}/edit`,
    });
    const search = await screen.findByRole('searchbox', { name: 'Αναζήτηση αντασφαλιστή' });
    await user.type(search, 'Φυσικό{Enter}');
    await user.click(await screen.findByText('Φυσικό Πρόσωπο'));
    expect(await screen.findByText('Επιλέξτε νομικό πρόσωπο ως αντασφαλιστή')).toBeInTheDocument();
    expect(screen.getAllByRole('textbox', { name: /Υπογεγραμμένο ποσοστό/ })).toHaveLength(1);
  });
  it('saves a draft only after confirmation with optimistic-lock version and idempotency key', async () => {
    const api = mockApi([
      ...catalogueRoutes,
      {
        method: 'GET',
        path: `/api/ri/v1/contracts/${treaty.contractId}`,
        respond: () => ({ body: { contract: treaty } }),
      },
      {
        method: 'PATCH',
        path: `/api/ri/v1/contracts/${treaty.contractId}`,
        respond: () => ({ body: { contract: { ...treaty, recordVersion: 5 } } }),
      },
    ]);
    const { user } = renderScreen(<EditContractPage />, {
      path: '/reinsurance/contracts/:contractId/edit',
      url: `/reinsurance/contracts/${treaty.contractId}/edit`,
    });
    await screen.findByRole('checkbox', { name: 'Αστική ευθύνη αυτοκινήτου' });
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση προχείρου' }));
    expect(api.callsTo('PATCH', `/api/ri/v1/contracts/${treaty.contractId}`)).toHaveLength(0);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Αποθήκευση προχείρου' }));
    await waitFor(() => {
      expect(api.callsTo('PATCH', `/api/ri/v1/contracts/${treaty.contractId}`)).toHaveLength(1);
    });
    const call = api.callsTo('PATCH', `/api/ri/v1/contracts/${treaty.contractId}`)[0];
    if (!call) throw new Error('Expected update request');
    expect(call.body).toMatchObject({
      expectedRecordVersion: 4,
      placedPct: '100',
      layers: treaty.layers,
      participations: treaty.participations,
    });
    expect(call.headers.get('Idempotency-Key')).toBeTruthy();
  });
  it('shows the no-permission state to unrelated roles', async () => {
    signIn('claims', ['Staff.ClaimsHandler']);
    mockApi([]);
    renderScreen(<RequireReinsuranceRole />, { path: '/reinsurance', url: '/reinsurance' });
    expect(await screen.findByText(/πρόσβαση|δικαίωμα/i)).toBeInTheDocument();
  });
});
describe('maker-checker treaty approval', () => {
  it('hides approval from the treaty enterer even when they hold the manager role', () => {
    signIn('maker', ['Staff.ReinsuranceManager']);
    mockApi([]);
    renderScreen(<DecisionBar contract={{ ...treaty, status: 'PENDING_APPROVAL' }} />, {
      path: '/',
      url: '/',
    });
    expect(
      screen.getByText('Δεν μπορείτε να εγκρίνετε τη δική σας καταχώριση'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Έγκριση…' })).not.toBeInTheDocument();
  });
  it('explains a server separation-of-duties refusal in Greek', async () => {
    signIn('checker', ['Staff.ReinsuranceManager']);
    mockApi([
      {
        method: 'POST',
        path: '/api/ri/v1/contracts/approve',
        respond: () => problem(403, 'RI-ERR-SOD', 'SoD'),
      },
    ]);
    const { user } = renderScreen(
      <DecisionBar contract={{ ...treaty, status: 'PENDING_APPROVAL' }} />,
      { path: '/', url: '/' },
    );
    await user.click(screen.getByRole('button', { name: 'Έγκριση…' }));
    await user.click(screen.getByRole('button', { name: 'Επιβεβαίωση έγκρισης' }));
    expect(
      await screen.findByText(/Δεν μπορείτε να εγκρίνετε σύμβαση που καταχωρήσατε εσείς/),
    ).toBeInTheDocument();
  });
});
