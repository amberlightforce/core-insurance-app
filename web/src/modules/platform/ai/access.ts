import { useDevSession } from '../../../dev-auth/devAuth';

/** Illustrative until PLT role grants land; the API enforces plt.AiToggle.set / plt.Ai.killSwitch. */
export const aiAdminRoles = ['Platform.Admin', 'Platform.AiGovernor'] as const;

export function useCanAdministerAi(): boolean {
  const roles = useDevSession()?.user.roles ?? [];
  return roles.some((r) => aiAdminRoles.some((a) => a === r));
}
