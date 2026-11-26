/** The signed-in staff user as the shell shows it (dev sign-in today, the PLT profile later). */
export interface ShellUser {
  name: string;
  roles: readonly string[];
}

/** The legal entity until the PLT user profile and the MKT registry provide it (synthetic). */
export const sampleEntityName = 'Sample Insurance S.A.';

/** Synthetic dev users carry a «Dev » prefix and «(synthetic)» marker; the shell shows the persona only. */
export function displayName(name: string): string {
  return (
    name
      .replace(/\s*\([^)]*\)\s*$/u, '')
      .replace(/^Dev\s+/u, '')
      .trim() || name
  );
}

/** A role without a translation reads as words: `Staff.ReinsuranceManager` → `Reinsurance Manager`. */
export function humanizeRole(role: string): string {
  const name = role.split('.').at(-1) ?? role;
  return name.replace(/([a-z])([A-Z])/gu, '$1 $2');
}

/** First word of the display name, for the home greeting. */
export function firstName(name: string): string {
  return displayName(name).split(/\s+/u)[0] ?? name;
}

/** Role → `shell:roles.*` key (i18next keys cannot contain the role's dots). */
export const roleKeys = {
  'Staff.Underwriter': 'roles.underwriter',
  'Staff.Billing': 'roles.billing',
  'Staff.BillingManager': 'roles.billingManager',
  'Staff.Finance': 'roles.finance',
  'Staff.ClaimsHandler': 'roles.claimsHandler',
  'Staff.ClaimsManager': 'roles.claimsManager',
  'Platform.Admin': 'roles.admin',
} as const;

export type KnownRole = keyof typeof roleKeys;

export function isKnownRole(role: string): role is KnownRole {
  return role in roleKeys;
}
