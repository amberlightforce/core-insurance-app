import { reinsuranceRoles } from '../../auth/roles';
import { readSession } from '../../dev-auth/devAuth';

export { reinsuranceRoles };

/** Staff.ReinsuranceAccountant enters treaties (dev user `riacct`), Staff.ReinsuranceManager approves (`rimgr`). */
export const accountantRole = 'Staff.ReinsuranceAccountant';
export const managerRole = 'Staff.ReinsuranceManager';

export function currentUser() {
  const session = readSession();
  return {
    id: session?.user.id ?? '',
    actorKey: session?.user.actorKey ?? '',
    roles: session?.user.roles ?? [],
  };
}

export const hasRole = (roles: readonly string[], role: string) => roles.includes(role);
