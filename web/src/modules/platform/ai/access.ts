import { aiAdminRoles } from '../../../auth/roles';
import { useDevSession } from '../../../dev-auth/devAuth';

export function useCanAdministerAi(): boolean {
  const roles = useDevSession()?.user.roles ?? [];
  return roles.some((r) => aiAdminRoles.some((a) => a === r));
}
