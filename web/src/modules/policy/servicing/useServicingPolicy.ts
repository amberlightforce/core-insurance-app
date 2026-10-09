import { athensToday } from '../../quote/time';
import { usePolicy } from '../api';

/**
 * The policy a servicing screen works on. As of today a Scheduled policy shows no risk (nothing is valid yet), so
 * for one the screen reads the policy as of the start of its upcoming term, the way the policy file does.
 */
export function useServicingPolicy(policyId: string) {
  const today = athensToday();
  const current = usePolicy(policyId, today);
  const upcomingStart =
    current.data?.policy.status === 'SCHEDULED' && current.data.term
      ? athensToday(new Date(current.data.term.period.from))
      : null;
  const upcoming = usePolicy(policyId, upcomingStart ?? today);
  return upcomingStart !== null ? upcoming : current;
}
