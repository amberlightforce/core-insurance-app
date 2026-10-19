import { isApiError } from '../../api/client';
import { closeGuardReasons } from './codes';

/**
 * The close-guard reasons per exposure from a CLM-ERR-CLOSE-GUARD problem. SL4 sends `errors[]` items
 * `{ code, exposureIds[] }` (D-SL4-14); a claim-level guard has no exposure (key ''). The older extension members
 * (exposure → "REASON,REASON") are still understood.
 */
export function closeGuardFindings(
  error: unknown,
): { exposure: string; reasons: string[] }[] | null {
  if (!isApiError(error) || error.code !== 'CLM-ERR-CLOSE-GUARD') return null;
  const byExposure = new Map<string, string[]>();
  const add = (exposure: string, reason: string) => {
    byExposure.set(exposure, [...(byExposure.get(exposure) ?? []), reason]);
  };
  const items = (error.problem.errors ?? []) as unknown as readonly {
    code?: unknown;
    exposureIds?: unknown;
  }[];
  for (const item of items) {
    if (typeof item.code !== 'string' || !Array.isArray(item.exposureIds)) continue;
    const ids = item.exposureIds.filter((id): id is string => typeof id === 'string');
    if (ids.length === 0) add('', item.code);
    for (const id of ids) add(id, item.code);
  }
  if (byExposure.size === 0) {
    const known = new Set<string>(closeGuardReasons);
    for (const [member, value] of Object.entries(error.problem)) {
      if (typeof value !== 'string') continue;
      const reasons = value.split(',').map((r) => r.trim());
      if (reasons.length > 0 && reasons.every((r) => known.has(r))) {
        byExposure.set(member, reasons);
      }
    }
  }
  return [...byExposure].map(([exposure, reasons]) => ({ exposure, reasons }));
}

/** The illustrative authority hint from dry-run or submit checks: only when every check has a decision. */
export function authorityHint(
  checks: readonly unknown[] | undefined,
): 'within' | 'referral' | null {
  if (!checks || checks.length === 0) return null;
  const decisions = checks.map((c) =>
    typeof c === 'object' && c !== null ? (c as { decision?: unknown }).decision : undefined,
  );
  if (decisions.some((d) => d === 'DENY')) return null;
  if (decisions.some((d) => d === 'REFER')) return 'referral';
  return decisions.every((d) => d === 'ALLOW') ? 'within' : null;
}
