import type { ContractCreateRequest } from './api';
import { pctToMicro, toMinor } from './money';

/** Validate the complete draft, keeping all monetary and percentage arithmetic exact. */
export function draftErrors(draft: ContractCreateRequest): string[] {
  const errors: string[] = [];
  if (!Number.isInteger(draft.contractYear) || draft.contractYear < 1900) errors.push('year');
  if (!draft.period.from || !draft.period.to || draft.period.to <= draft.period.from)
    errors.push('period');
  if (draft.scope.productCodes.length === 0 || draft.scope.coverageCodes.length === 0)
    errors.push('scope');
  try {
    const placed = pctToMicro(draft.placedPct);
    const signed = draft.participations.map((p) => pctToMicro(p.signedLinePct));
    if (
      placed <= 0n ||
      placed > 100_000_000n ||
      signed.some((p) => p <= 0n) ||
      signed.reduce((sum, p) => sum + p, 0n) !== placed
    )
      errors.push('signed');
  } catch {
    errors.push('signed');
  }
  if (
    draft.participations.length === 0 ||
    draft.participations.some((p) => !p.reinsurerPartyId) ||
    new Set(draft.participations.map((p) => p.reinsurerPartyId)).size !==
      draft.participations.length
  )
    errors.push('participants');
  if (draft.participations.filter((p) => p.lead).length !== 1) errors.push('lead');
  try {
    if (
      draft.layers.length === 0 ||
      draft.layers.some(
        (l) =>
          toMinor(l.attachment.amount) < 0n ||
          toMinor(l.limit.amount) <= 0n ||
          toMinor(l.aad.amount) < 0n ||
          (l.aal && toMinor(l.aal.amount) <= 0n),
      )
    )
      errors.push('layers');
  } catch {
    errors.push('layers');
  }
  return errors;
}
