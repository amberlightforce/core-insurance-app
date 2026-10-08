import { describe, expect, it } from 'vitest';

import type { ClaimPaymentView, ClaimView } from '../../api/types';
import { claimStages } from './stages';

const claim = (status: 'OPEN' | 'CLOSED', exposures: number) =>
  ({
    summary: { status },
    exposures: Array.from({ length: exposures }, (_, i) => ({ exposureId: String(i) })),
  }) as unknown as ClaimView;

const payment = (status: ClaimPaymentView['status']) => ({ status }) as ClaimPaymentView;

const states = (c: ClaimView, reserved: number | null, payments: ClaimPaymentView[] | null) =>
  claimStages(c, { reserved, payments }).map((s) => `${s.id}:${s.state}`);

describe('claimStages', () => {
  it('marks only recorded facts as done and never guesses first contact', () => {
    expect(states(claim('OPEN', 0), null, null)).toEqual([
      'notice:done',
      'contact:untracked',
      'assessment:current',
      'offer:todo',
      'payment:todo',
      'closing:todo',
    ]);
  });

  it('moves on with an exposure, a reserve and an issued payment', () => {
    expect(states(claim('OPEN', 1), 2500, [payment('PENDING')])).toContain('payment:current');
    expect(states(claim('OPEN', 1), 2500, [payment('CLEARED')])).toContain('closing:current');
  });

  it('shows a closed claim without a current stage, keeping skipped stages not done', () => {
    expect(states(claim('CLOSED', 1), 0, [])).toEqual([
      'notice:done',
      'contact:untracked',
      'assessment:done',
      'offer:todo',
      'payment:todo',
      'closing:done',
    ]);
  });
});
