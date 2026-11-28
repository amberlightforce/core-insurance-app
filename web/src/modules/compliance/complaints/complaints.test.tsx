import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { daysUntil, isOverdue, nextDeadline, type ComplaintItem } from './api';
import { ComplaintListPage } from './ComplaintListPage';

vi.setConfig({ testTimeout: 60_000 });

const day = 86_400_000;
const iso = (offset: number) => new Date(Date.now() + offset).toISOString();

const item = (
  n: number,
  status: NonNullable<NonNullable<ComplaintItem['complaint']>['status']>,
  offset: number,
  category = 'CLAIMS_HANDLING',
): ComplaintItem => ({
  number: `CMP-2026-000${String(n)}`,
  complaint: {
    complaintId: `00000000-0000-4000-8000-00000000000${String(n)}`,
    number: `CMP-2026-000${String(n)}`,
    status,
    channel: 'EMAIL',
    category,
    receivedAt: iso(-10 * day),
  },
  deadlines: [
    {
      clockCode: 'CMP_COMPLAINT_REPLY',
      status: 'RUNNING',
      deadline: iso(offset),
      instanceId: `c${String(n)}`,
    },
  ],
});

const list = (items: ComplaintItem[]) => ({
  method: 'GET' as const,
  path: '/api/cmp/v1/complaints/query',
  respond: () => ({ body: { items, nextCursor: null } }),
});

const render = () =>
  renderScreen(<ComplaintListPage />, {
    path: '/compliance/complaints',
    url: '/compliance/complaints',
  });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Complaints list', () => {
  it('lists complaints with a deadline countdown and an overdue marker', async () => {
    mockApi([list([item(1, 'RECEIVED', 5 * day), item(2, 'UNDER_INVESTIGATION', -3 * day)])]);
    render();
    expect(await screen.findByText('CMP-2026-0001')).toBeInTheDocument();
    expect(screen.getByText('CMP-2026-0002')).toBeInTheDocument();
    expect(screen.getByText(/απομένουν/)).toBeInTheDocument();
    expect(screen.getByText(/ημέρες καθυστέρηση/)).toBeInTheDocument();
    expect(screen.getAllByText('Εκπρόθεσμο').length).toBeGreaterThan(1);
    await expectNoA11yViolations();
  });

  it('narrows to overdue complaints', async () => {
    mockApi([list([item(1, 'RECEIVED', 5 * day), item(2, 'UNDER_INVESTIGATION', -3 * day)])]);
    const { user } = render();
    await screen.findByText('CMP-2026-0001');
    await user.click(screen.getByRole('checkbox', { name: 'Μόνο εκπρόθεσμα' }));
    expect(screen.queryByText('CMP-2026-0001')).not.toBeInTheDocument();
    expect(screen.getByText('CMP-2026-0002')).toBeInTheDocument();
  });

  it('sends the status filter to the server', async () => {
    const api = mockApi([list([item(1, 'RECEIVED', 5 * day)])]);
    const { user } = render();
    await screen.findByText('CMP-2026-0001');
    const [statusSelect] = screen.getAllByRole('button', { name: /Κατάσταση/ });
    if (!statusSelect) throw new Error('status select missing');
    await user.click(statusSelect);
    await user.click(await screen.findByRole('option', { name: 'Κλιμακώθηκε' }));
    await vi.waitFor(() => {
      expect(
        api
          .callsTo('GET', '/api/cmp/v1/complaints/query')
          .some((c) => c.url.searchParams.get('status') === 'ESCALATED'),
      ).toBe(true);
    });
  });

  it('shows the empty state', async () => {
    mockApi([list([])]);
    render();
    expect(
      await screen.findByText('Κανένα παράπονο δεν ταιριάζει με αυτά τα φίλτρα.'),
    ).toBeInTheDocument();
  });

  it('shows a retryable error', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/complaints/query',
        respond: () => problem(500, 'X', 'Complaint failure'),
      },
    ]);
    render();
    expect(await screen.findByText('Complaint failure')).toBeInTheDocument();
  });

  it('shows a permission banner on 403', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/complaints/query',
        respond: () => problem(403, 'FORBIDDEN', 'Forbidden'),
      },
    ]);
    render();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Επανάληψη/ })).not.toBeInTheDocument();
  });
});

describe('complaint deadline helpers', () => {
  it('picks the earliest ticking clock and counts days', () => {
    const c: ComplaintItem = {
      complaint: { status: 'RECEIVED' },
      deadlines: [
        { status: 'MET', deadline: '2026-01-01T00:00:00Z' },
        { status: 'RUNNING', deadline: '2026-03-01T00:00:00Z' },
        { status: 'WARNED', deadline: '2026-02-01T00:00:00Z' },
      ],
    };
    expect(nextDeadline(c)?.deadline).toBe('2026-02-01T00:00:00Z');
    const now = Date.parse('2026-01-30T12:00:00Z');
    expect(daysUntil('2026-02-01T00:00:00Z', now)).toBe(2);
    expect(daysUntil('2026-01-29T00:00:00Z', now)).toBe(-2);
    expect(isOverdue(c, Date.parse('2026-02-02T00:00:00Z'))).toBe(true);
    expect(
      isOverdue({ ...c, complaint: { status: 'CLOSED' } }, Date.parse('2026-02-02T00:00:00Z')),
    ).toBe(false);
  });
});
