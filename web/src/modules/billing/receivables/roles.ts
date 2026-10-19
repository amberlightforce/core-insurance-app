import { readSession } from '../../../dev-auth/devAuth';

/** Staff.Billing reads receivables and records receipts; the manager sees the same and approves releases. */
export const receivableViewRoles: readonly string[] = ['Staff.Billing', 'Staff.BillingManager'];
export const releaseApproveRole = 'Staff.BillingManager';

export function currentRoles(): string[] {
  return readSession()?.user.roles ?? [];
}

/** Whether the user may approve releases at all (the server still enforces role and separation of duties). */
export function canApproveReleases(): boolean {
  return currentRoles().includes(releaseApproveRole);
}

/** The requester is the maker and never approves their own release (D-SL4-12); this only disables the control. */
export function isRequester(requestedBy: string | undefined): boolean {
  const id = readSession()?.user.id;
  return id !== undefined && requestedBy !== undefined && id === requestedBy;
}
