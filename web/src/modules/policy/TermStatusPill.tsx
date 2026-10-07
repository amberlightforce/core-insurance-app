import { StatusPill, type EntityState } from '../../design-system';

type TermState = EntityState<'policyTerm'>;

const states: Record<string, TermState | undefined> = {
  SCHEDULED: 'scheduled',
  IN_FORCE: 'inForce',
  PENDING_CANCELLATION: 'pendingCancellation',
  CANCELLED: 'cancelled',
  EXPIRED: 'expired',
};

/** Policy/term status through the single status map: Scheduled, In force, Expired (and the cancellation states). */
export function TermStatusPill({ state }: { state: string }) {
  const mapped = states[state];
  return mapped ? (
    <StatusPill entity="policyTerm" state={mapped} announceChanges={false} />
  ) : (
    <StatusPill semantic="info" subLabel={state} announceChanges={false} />
  );
}
