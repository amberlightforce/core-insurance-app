import { readSession } from '../../../dev-auth/devAuth';

/** Roles that see the refund screens (clerks propose, the billing manager decides). */
export const refundViewRoles: readonly string[] = ['Staff.Billing', 'Staff.BillingManager'];
export const refundDecideRole = 'Staff.BillingManager';

export function currentRoles(): string[] {
  return readSession()?.user.roles ?? [];
}

/**
 * Whether to offer approve/reject. The requester is the maker and never decides their own refund (D-SL3-14); this
 * only hides the control, the server enforces authority and separation of duties on every decision.
 */
export function canOfferDecision(requestedBy: string): boolean {
  const session = readSession();
  if (!session) return false;
  return session.user.roles.includes(refundDecideRole) && session.user.id !== requestedBy;
}
