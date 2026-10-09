import { describe, expect, it } from 'vitest';
import { draftErrors } from './editorModel';
import { treatyDraft } from './fixtures';
import { allocateLargestRemainder, pctToMicro, toMinor } from './money';

describe('treaty draft validation and exact money', () => {
  it('accepts a complete treaty and exact fractional signed lines', () => {
    expect(draftErrors(treatyDraft)).toEqual([]);
    expect(
      draftErrors({
        ...treatyDraft,
        participations: [
          { reinsurerPartyId: 'a', signedLinePct: '33.333333', lead: true },
          { reinsurerPartyId: 'b', signedLinePct: '66.666667', lead: false },
        ],
      }),
    ).toEqual([]);
  });
  it('refuses split lines that do not match placed share, duplicate reinsurers and two leads', () => {
    expect(
      draftErrors({
        ...treatyDraft,
        participations: [
          { reinsurerPartyId: 'a', signedLinePct: '33.333333', lead: true },
          { reinsurerPartyId: 'a', signedLinePct: '66.666666', lead: true },
        ],
      }),
    ).toEqual(expect.arrayContaining(['signed', 'participants', 'lead']));
  });
  it('refuses invalid money and excess precision instead of silently rounding or zeroing', () => {
    expect(() => toMinor('bad')).toThrow();
    expect(() => toMinor('0.001')).toThrow();
    expect(() => pctToMicro('0.0000001')).toThrow();
    expect(
      draftErrors({
        ...treatyDraft,
        layers: [{ ...treatyDraft.layers[0]!, limit: { amount: '0.001', currency: 'EUR' } }],
      }),
    ).toContain('layers');
  });
  it('allocates every cent exactly without negative shares at large monetary values', () => {
    const total = 9_007_199_254_740_993n;
    const shares = allocateLargestRemainder(total, [33333333n, 66666667n]);
    expect(shares.reduce((sum, share) => sum + share, 0n)).toBe(total);
    expect(shares.every((share) => share >= 0n)).toBe(true);
    expect(() => allocateLargestRemainder(1n, [0n, 0n])).toThrow();
    expect(() => allocateLargestRemainder(1n, [-1n, 2n])).toThrow();
  });
});
