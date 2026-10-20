import { packRoles } from '../../../auth/roles';
import { useDevSession } from '../../../dev-auth/devAuth';

export interface Caller {
  id: string;
  actorKey: string;
  name: string;
  /** May request an activation or rollback (maker: Platform.ReleaseManager). */
  canRequest: boolean;
  /** May decide a request (checker: Platform.DesignAuthority). */
  canDecide: boolean;
  canView: boolean;
}

/** The signed-in user's pack capabilities. They only decide what is offered; the server enforces each call. */
export function useCaller(): Caller {
  const user = useDevSession()?.user;
  const roles = user?.roles ?? [];
  return {
    id: user?.id ?? '',
    actorKey: user?.actorKey ?? '',
    name: user?.name ?? '',
    canRequest: roles.includes('Platform.ReleaseManager'),
    canDecide: roles.includes('Platform.DesignAuthority'),
    canView: roles.some((r) => packRoles.some((p) => p === r)),
  };
}
