import { readSession } from '../../dev-auth/devAuth';

/** Staff.ReinsuranceAccountant enters treaties (dev user `riacct`), Staff.ReinsuranceManager approves (`rimgr`). */
export const accountantRole = 'Staff.ReinsuranceAccountant';
export const managerRole = 'Staff.ReinsuranceManager';
/** Everyone who works the reinsurance screens (D-SL4-16); `Platform.Admin` for the superuser. */
export const reinsuranceRoles = [
  accountantRole,
  managerRole,
  'Staff.RecoverySpecialist',
  'Platform.Admin',
] as const;

export function currentUser() {
  const session = readSession();
  return { id: session?.user.id ?? '', roles: session?.user.roles ?? [] };
}

export const hasRole = (roles: readonly string[], role: string) => roles.includes(role);
