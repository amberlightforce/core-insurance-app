import { isApiError } from '../../api/client';
import { closeGuardReasons } from './codes';

/** The close-guard reasons per exposure from a CLM-ERR-CLOSE-GUARD problem (extension members: exposure → reasons). */
export function closeGuardFindings(
  error: unknown,
): { exposure: string; reasons: string[] }[] | null {
  if (!isApiError(error) || error.code !== 'CLM-ERR-CLOSE-GUARD') return null;
  const known = new Set<string>(closeGuardReasons);
  const findings: { exposure: string; reasons: string[] }[] = [];
  for (const [member, value] of Object.entries(error.problem)) {
    if (typeof value !== 'string') continue;
    const reasons = value.split(',').map((r) => r.trim());
    if (reasons.length > 0 && reasons.every((r) => known.has(r))) {
      findings.push({ exposure: member, reasons });
    }
  }
  return findings;
}
