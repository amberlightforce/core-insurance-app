import { screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { DevSession } from '../../../dev-auth/devAuth';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import type { DsarCase } from './api';
import { DsarCasePage } from './DsarCasePage';
import { DsarListPage } from './DsarListPage';
import { RequireDsarRole } from './RequireDsarRole';

vi.setConfig({ testTimeout: 60_000 });

const id = '11111111-1111-4111-8111-111111111111';
const party = '22222222-2222-4222-8222-222222222222';

function signIn(roles: string[]) {
  const session: DevSession = {
    accessToken: 'test',
    expiresAt: '2099-01-01T00:00:00Z',
    user: { id: 'dpo', name: 'dpo', roles, actorKey: 'USER:dev:dpo' },
  };
  sessionStorage.setItem('coreins.devSession', JSON.stringify(session));
}

function dsar(over: Partial<DsarCase['request']> = {}, tasks?: DsarCase['tasks']): DsarCase {
  return {
    request: {
      dsarId: id,
      requestType: 'ERASURE',
      status: 'IN_REVIEW',
      subjectPartyId: party,
      createdBy: 'USER:dev:other',
      receivedAt: '2026-10-01T09:00:00Z',
      deadline: '2026-10-31T09:00:00Z',
      extended: false,
      identityVerified: true,
      legalHold: false,
      blockers: ['NOT_BUILT:DOC', 'APPROVAL_REQUIRED'],
      ...over,
    },
    tasks: tasks ?? [
      {
        taskId: '33333333-3333-4333-8333-333333333333',
        module: 'PTY',
        status: 'DONE',
        phase: 'EXPORT',
        attempts: 1,
        outcomes: [
          {
            module: 'PTY',
            recordClass: 'PARTY',
            kind: 'RESTRICTED',
            recordCount: 2,
            legalBasis: 'GDPR 6(1)(b)',
            retentionEnd: '2031-01-01',
            exported: true,
          },
        ],
      },
      {
        taskId: '44444444-4444-4444-8444-444444444444',
        module: 'DOC',
        status: 'NOT_BUILT',
        phase: 'EXPORT',
        attempts: 0,
        outcomes: [],
      },
    ],
  };
}

beforeEach(() => {
  signIn(['Staff.ComplianceOfficer']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
  localStorage.clear();
});

const renderCase = () =>
  renderScreen(<DsarCasePage />, {
    path: '/compliance/dsar/:dsarId',
    url: `/compliance/dsar/${id}`,
  });

const disclosures = {
  method: 'GET',
  path: '/api/cmp/v1/disclosure-logs/query',
  respond: () => ({
    body: {
      items: [
        {
          entries: [
            {
              entryId: '55555555-5555-4555-8555-555555555555',
              recipient: 'Revenue Authority',
              purpose: 'Tax reporting',
              legalBasis: 'Legal obligation',
              categories: ['identity'],
              recordedBy: 'USER:dev:dpo',
              recordedAt: '2026-10-02T09:00:00Z',
            },
          ],
        },
      ],
      nextCursor: null,
    },
  }),
};

describe('DSAR console', () => {
  it('shows per-module fan-out, the NOT_BUILT blocker and the disclosure log', async () => {
    mockApi([
      { method: 'GET', path: `/api/cmp/v1/dsar/${id}`, respond: () => ({ body: dsar() }) },
      disclosures,
    ]);
    renderCase();
    expect(
      await screen.findByText('Δεν έχει υλοποιηθεί', { selector: 'span' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/εμποδίζουν την ολοκλήρωση: DOC/)).toBeInTheDocument();
    expect(screen.getAllByText('PTY').length).toBeGreaterThan(0);
    expect(screen.getByText('APPROVAL_REQUIRED')).toBeInTheDocument();
    expect(await screen.findByText('Revenue Authority')).toBeInTheDocument();
  });

  it('approves an erasure with a reason and sends the action, never an approver', async () => {
    const api = mockApi([
      { method: 'GET', path: `/api/cmp/v1/dsar/${id}`, respond: () => ({ body: dsar() }) },
      {
        method: 'POST',
        path: '/api/cmp/v1/dsar/approve',
        respond: () => ({ body: dsar({ approvalAction: 'ERASE', approvedBy: 'USER:dev:dpo' }) }),
      },
      disclosures,
    ]);
    const { user } = renderCase();
    await user.click(
      await screen.findByRole('button', { name: 'Έγκριση διαγραφής (δεύτερος DPO)' }),
    );
    await user.type(screen.getByLabelText(/Αιτιολογία/), 'Subject withdrew consent');
    await user.click(screen.getByRole('button', { name: 'Έγκριση' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/cmp/v1/dsar/approve')).toHaveLength(1);
    });
    const call = api.callsTo('POST', '/api/cmp/v1/dsar/approve')[0];
    expect(call?.body).toEqual({ id, action: 'ERASE', reason: 'Subject withdrew consent' });
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
  });

  it('shows the server refusal when the case creator tries to approve', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/cmp/v1/dsar/${id}`,
        respond: () => ({ body: dsar({ createdBy: 'USER:dev:dpo' }) }),
      },
      {
        method: 'POST',
        path: '/api/cmp/v1/dsar/approve',
        respond: () =>
          problem(403, 'CMP-ERR-APPROVAL-SAME-USER', 'Forbidden', 'The approver must differ.'),
      },
      disclosures,
    ]);
    const { user } = renderCase();
    expect(await screen.findByText('Εσείς ανοίξατε αυτό το αίτημα')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Έγκριση διαγραφής (δεύτερος DPO)' }));
    await user.type(screen.getByLabelText(/Αιτιολογία/), 'x');
    await user.click(screen.getByRole('button', { name: 'Έγκριση' }));
    expect(await screen.findByText('CMP-ERR-APPROVAL-SAME-USER')).toBeInTheDocument();
  });

  it('opens a request for a party and navigates to it', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/dsar/query',
        respond: () => ({ body: { items: [dsar()], nextCursor: null } }),
      },
      {
        method: 'POST',
        path: '/api/cmp/v1/dsar',
        respond: () => ({ body: dsar({ status: 'RECEIVED' }) }),
      },
    ]);
    const { user } = renderScreen(<DsarListPage />, {
      path: '/compliance/dsar',
      url: '/compliance/dsar',
    });
    expect(await screen.findByText('Διαγραφή (άρθρο 17)')).toBeInTheDocument();
    await user.type(screen.getByLabelText(/Υποκείμενο δεδομένων \(id μέρους\)/), party);
    await user.click(screen.getByRole('button', { name: 'Άνοιγμα αιτήματος' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/cmp/v1/dsar')).toHaveLength(1);
    });
    expect(api.callsTo('POST', '/api/cmp/v1/dsar')[0]?.body).toEqual({
      subjectPartyId: party,
      requestType: 'ACCESS',
      legalHold: false,
    });
  });

  it('shows the no-permission state to other roles', () => {
    signIn(['Staff.Billing']);
    renderScreen(<RequireDsarRole />, { path: '/compliance/dsar', url: '/compliance/dsar' });
    expect(screen.getByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });
});
