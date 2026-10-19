import { readSession } from '../../../dev-auth/devAuth';

/** Billing staff work delinquency cases; the server enforces bil.Delinquency.* permissions. */
export const delinquencyRoles: readonly string[] = ['Staff.Billing', 'Staff.BillingManager'];

export function canActOnDelinquency(): boolean {
  const roles = readSession()?.user.roles ?? [];
  return roles.some((r) => delinquencyRoles.includes(r));
}

/** Whole days until the clock is due (negative once overdue); null when no clock runs. */
export function daysRemaining(
  clockDueAt: string | null | undefined,
  now = new Date(),
): number | null {
  if (!clockDueAt) return null;
  return Math.ceil((new Date(clockDueAt).getTime() - now.getTime()) / 86_400_000);
}
