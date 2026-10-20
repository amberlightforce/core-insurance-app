import { migrationRoles } from '../../auth/roles';
import { useDevSession } from '../../dev-auth/devAuth';

export function useMigrationCaller() {
  const roles = useDevSession()?.user.roles ?? [];
  return { canWork: roles.some((r) => migrationRoles.some((m) => m === r)) };
}
