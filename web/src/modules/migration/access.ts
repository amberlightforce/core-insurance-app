import { useDevSession } from '../../dev-auth/devAuth';

/** Roles that work the migration screens (permissions mig.Batch.*, mig.Routing.*); the API enforces each call. */
export const migrationRoles = ['Staff.MigrationManager', 'Platform.Admin'] as const;

export function useMigrationCaller() {
  const roles = useDevSession()?.user.roles ?? [];
  return { canWork: roles.some((r) => migrationRoles.some((m) => m === r)) };
}
