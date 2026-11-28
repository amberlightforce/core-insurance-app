import { configRoles } from '../../../auth/roles';
import { useDevSession } from '../../../dev-auth/devAuth';

export interface ConfigCaller {
  actorKey: string;
  /** Maker: Platform.ReleaseManager. */
  canMake: boolean;
  /** Checker: Platform.DesignAuthority. */
  canDecide: boolean;
  canView: boolean;
}

/** The signed-in user's configuration capabilities. They only decide what is offered; the server enforces each call. */
export function useConfigCaller(): ConfigCaller {
  const user = useDevSession()?.user;
  const roles = user?.roles ?? [];
  return {
    actorKey: user?.actorKey ?? '',
    canMake: roles.includes('Platform.ReleaseManager'),
    canDecide: roles.includes('Platform.DesignAuthority'),
    canView: roles.some((r) => configRoles.some((c) => c === r)),
  };
}
