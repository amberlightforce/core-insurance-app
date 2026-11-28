import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { scalar } from './api';
import { ComplianceHomePage } from './ComplianceHomePage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Compliance landing', () => {
  it('lists open statutory clocks and complaints and links to the DSAR console', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/clocks/query',
        respond: () => ({
          body: {
            items: [
              {
                instanceId: '11111111-1111-4111-8111-111111111111',
                clockCode: 'COMPLAINT_REPLY',
                subjectRef: { type: 'Complaint', id: 'CMP-2026-0001' },
                status: 'BREACHED',
                deadline: '2026-10-01T09:00:00Z',
              },
              {
                instanceId: '22222222-2222-4222-8222-222222222222',
                clockCode: 'COMPLAINT_ACK',
                subjectRef: { type: 'Complaint', id: 'CMP-2026-0002' },
                status: 'RUNNING',
                deadline: '2026-11-01T09:00:00Z',
              },
            ],
            nextCursor: null,
          },
        }),
      },
      {
        method: 'GET',
        path: '/api/cmp/v1/complaints/query',
        respond: () => ({
          body: {
            items: [
              {
                number: 'CMP-2026-0001',
                complaint: { status: 'RECEIVED' },
                deadlines: [
                  {
                    clockCode: 'CMP_COMPLAINT_REPLY',
                    status: 'RUNNING',
                    deadline: '2026-10-01T09:00:00Z',
                  },
                ],
              },
            ],
            nextCursor: null,
          },
        }),
      },
    ]);
    renderScreen(<ComplianceHomePage />, { path: '/compliance', url: '/compliance' });
    expect(await screen.findByText('COMPLAINT_REPLY')).toBeInTheDocument();
    expect(screen.getByText('Παραβιάστηκε')).toBeInTheDocument();
    expect(screen.getByText('Σε εξέλιξη')).toBeInTheDocument();
    expect(await screen.findByText('RECEIVED')).toBeInTheDocument();
    await expectNoA11yViolations();
    expect(screen.getByRole('button', { name: 'Άνοιγμα κονσόλας DSAR' })).toBeInTheDocument();
  });

  it('shows an error state per section when the module does not serve the queries', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/clocks/query',
        respond: () => problem(500, 'X', 'Clock failure'),
      },
      {
        method: 'GET',
        path: '/api/cmp/v1/complaints/query',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    renderScreen(<ComplianceHomePage />, { path: '/compliance', url: '/compliance' });
    expect(await screen.findByText('Clock failure')).toBeInTheDocument();
    expect(await screen.findByText('Δεν υπάρχουν καταχωρημένα παράπονα.')).toBeInTheDocument();
  });

  it('reads opaque contract values defensively (clock subjects)', () => {
    expect(scalar({ value: 'X-1' }, ['value'])).toBe('X-1');
    expect(scalar(null)).toBe('');
  });
});
