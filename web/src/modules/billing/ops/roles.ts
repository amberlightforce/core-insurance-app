import { readSession } from '../../../dev-auth/devAuth';

/** Billing staff work the suspense workbench; the server enforces bil.Allocation.allocate (Staff.Billing). */
export function canWorkBillingOps(): boolean {
  const roles = readSession()?.user.roles ?? [];
  return roles.includes('Staff.Billing');
}
