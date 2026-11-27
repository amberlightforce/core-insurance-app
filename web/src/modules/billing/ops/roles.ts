import { readSession } from '../../../dev-auth/devAuth';

/** Billing staff work the suspense workbench; the server enforces bil.Allocation.allocate (Staff.Billing). */
export function canWorkBillingOps(): boolean {
  const roles = readSession()?.user.roles ?? [];
  return roles.includes('Staff.Billing');
}

/** suggest / list: Staff.Billing, Staff.BillingManager, Platform.Admin (server-enforced). */
export function canViewSuspense(): boolean {
  const roles = readSession()?.user.roles ?? [];
  return ['Staff.Billing', 'Staff.BillingManager', 'Platform.Admin'].some((r) => roles.includes(r));
}

/** transfer and refund-to-sender: Staff.Billing and Staff.BillingManager (server-enforced). */
export function canMoveSuspense(): boolean {
  const roles = readSession()?.user.roles ?? [];
  return roles.includes('Staff.Billing') || roles.includes('Staff.BillingManager');
}
