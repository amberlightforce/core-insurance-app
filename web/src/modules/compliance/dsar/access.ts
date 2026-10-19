import { useDevSession } from '../../../dev-auth/devAuth';

/** Roles that work the DSAR console (permissions cmp.Dsar.*, cmp.DisclosureLog.*); the API enforces each call. */
export const dsarRoles = ['Staff.ComplianceOfficer', 'Platform.Admin'] as const;

export function useDsarCaller() {
  const session = useDevSession();
  const roles = session?.user.roles ?? [];
  return {
    canWork: roles.some((r) => dsarRoles.some((d) => d === r)),
    actorKey: session?.user.actorKey ?? '',
  };
}
