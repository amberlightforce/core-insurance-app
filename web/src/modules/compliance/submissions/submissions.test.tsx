import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { DevSession } from '../../../dev-auth/devAuth';
import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import type { SubmissionView } from './api';
import { SubmissionPage } from './SubmissionPage';
import { SubmissionsListPage } from './SubmissionsListPage';

vi.setConfig({ testTimeout: 60_000 });

const id = '11111111-1111-4111-8111-111111111111';

function signIn(actor: string) {
  const session: DevSession = {
    accessToken: 'test',
    expiresAt: '2099-01-01T00:00:00Z',
    user: {
      id: actor,
      name: actor,
      roles: ['Staff.ComplianceOfficer'],
      actorKey: `USER:dev:${actor}`,
    },
  };
  sessionStorage.setItem('coreins.devSession', JSON.stringify(session));
}

const submission = (over: Partial<SubmissionView> = {}): SubmissionView => ({
  submissionId: id,
  number: 'SUB-2026-0001',
  version: 1,
  status: 'READY',
  submissionType: 'SOLVENCY_QRT',
  regulator: 'BOG',
  periodLabel: '2026-Q3',
  dueDate: '2026-11-15',
  dueLegalStatus: 'Verified',
  overdue: true,
  evidence: [{ kind: 'EVIDENCE_PACK', refId: 'archive-1', label: 'Q3 pack', addedBy: 'dev:prep' }],
  approvalStatus: 'PENDING',
  preparedBy: 'dev:prep',
  requestedBy: 'dev:prep',
  history: [
    {
      kind: 'TRANSITION',
      fromStatus: 'PLANNED',
      toStatus: 'IN_PREPARATION',
      actor: 'dev:prep',
      at: '2026-10-01T09:00:00Z',
    },
    { kind: 'COMMENT', actor: 'dev:prep', at: '2026-10-02T09:00:00Z', text: 'Pack checked' },
  ],
  ...over,
});

beforeEach(() => {
  signIn('checker');
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const renderDetail = () =>
  renderScreen(<SubmissionPage />, {
    path: '/compliance/submissions/:id',
    url: `/compliance/submissions/${id}`,
  });

describe('Submissions workbench', () => {
  it('lists submissions with an overdue marker', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/submissions/query',
        respond: () => ({ body: { items: [submission()], nextCursor: null } }),
      },
    ]);
    renderScreen(<SubmissionsListPage />, {
      path: '/compliance/submissions',
      url: '/compliance/submissions',
    });
    expect(await screen.findByText('SUB-2026-0001')).toBeInTheDocument();
    expect(screen.getAllByText('Εκπρόθεσμη').length).toBeGreaterThan(0);
    await expectNoA11yViolations();
  });

  it('shows the detail and lets another user approve', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: `/api/cmp/v1/submissions/${id}`,
        respond: () => ({ body: { submission: submission() } }),
      },
      {
        method: 'POST',
        path: '/api/cmp/v1/submissions/approve',
        respond: () => ({
          body: {
            submission: submission({ approvalStatus: 'APPROVED', approvedBy: 'dev:checker' }),
          },
        }),
      },
    ]);
    const { user } = renderDetail();
    expect(await screen.findByText('Pack checked')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Έγκριση' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Έγκριση' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/cmp/v1/submissions/approve')).toHaveLength(1);
    });
    expect(api.callsTo('POST', '/api/cmp/v1/submissions/approve')[0]?.body).toEqual({
      id,
      decision: 'APPROVE',
    });
  });

  it('does not offer approve to the preparer', async () => {
    signIn('prep');
    mockApi([
      {
        method: 'GET',
        path: `/api/cmp/v1/submissions/${id}`,
        respond: () => ({ body: { submission: submission() } }),
      },
    ]);
    renderDetail();
    expect(await screen.findByText(/Προετοιμάσατε ή ζητήσατε την έγκριση/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Έγκριση' })).not.toBeInTheDocument();
  });

  it('shows the server refusal when recording a filing', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/cmp/v1/submissions/${id}`,
        respond: () => ({
          body: { submission: submission({ approvalStatus: 'APPROVED', overdue: false }) },
        }),
      },
      {
        method: 'POST',
        path: '/api/cmp/v1/submissions/record-filing',
        respond: () => problem(409, 'CMP-ERR-SUBMISSION-NOT-APPROVED', 'Conflict', 'Not approved.'),
      },
    ]);
    const { user } = renderDetail();
    await user.click(await screen.findByRole('button', { name: 'Καταγραφή κατάθεσης' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/Αναφορά κατάθεσης/), 'REF-1');
    await user.click(within(dialog).getByRole('button', { name: 'Καταγραφή κατάθεσης' }));
    expect(await screen.findByText('CMP-ERR-SUBMISSION-NOT-APPROVED')).toBeInTheDocument();
  });
});
