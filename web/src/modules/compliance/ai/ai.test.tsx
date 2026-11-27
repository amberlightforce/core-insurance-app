import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, renderScreen } from '../../../test/mockApi';
import { actionsFor } from './api';
import { AiRegisterPage } from './AiRegisterPage';

vi.setConfig({ testTimeout: 60_000 });
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AI-system register', () => {
  it('lists registered systems with the moves their status allows', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/cmp/v1/ai-systems',
        respond: () => ({
          body: {
            items: [
              {
                featureId: 'AI-UW-01',
                name: 'Underwriting triage',
                status: 'IN_REVIEW',
                recordVersion: 2,
              },
            ],
            nextCursor: null,
          },
        }),
      },
    ]);
    renderScreen(<AiRegisterPage />, {
      path: '/compliance/ai-systems',
      url: '/compliance/ai-systems',
    });
    expect(await screen.findByText('AI-UW-01')).toBeInTheDocument();
    expect(screen.getByText('Underwriting triage')).toBeInTheDocument();
  });

  it('mirrors the server transitions', () => {
    expect(actionsFor('APPROVED')).toEqual(['suspend', 'retire']);
    expect(actionsFor('RETIRED')).toEqual([]);
  });
});
