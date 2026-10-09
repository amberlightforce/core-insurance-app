import { useDevSession } from '../../../dev-auth/devAuth';

/** Roles that see the registry (illustrative until the PLT role grants land; the API enforces mkt.Pack.list). */
const viewerRoles = ['Platform.ReleaseManager', 'Platform.DesignAuthority', 'Platform.Admin'];

export interface Caller {
  id: string;
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
    name: user?.name ?? '',
    canRequest: roles.includes('Platform.ReleaseManager'),
    canDecide: roles.includes('Platform.DesignAuthority'),
    canView: roles.some((r) => viewerRoles.includes(r)),
  };
}
