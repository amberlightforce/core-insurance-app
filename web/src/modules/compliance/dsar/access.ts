import { dsarRoles } from '../../../auth/roles';
import { useDevSession } from '../../../dev-auth/devAuth';

export { dsarRoles };

export function useDsarCaller() {
  const session = useDevSession();
  const roles = session?.user.roles ?? [];
  return {
    canWork: roles.some((r) => dsarRoles.some((d) => d === r)),
    actorKey: session?.user.actorKey ?? '',
  };
}
