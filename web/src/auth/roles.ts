/**
 * The one role model of the staff web app: every route guard (`RequireRole`) and every rail entry
 * (`app-shell/navigation.ts`) takes its role list from here, so a screen is never shown in the nav to a user the route
 * refuses (or the other way round). The API still enforces permissions on every call.
 */

/** Roles that work claims (dev users `claims` and `claimsmgr`). */
export const claimsRoles = ['Staff.ClaimsHandler', 'Staff.ClaimsManager'] as const;

/** Roles that work underwriting referrals (permission uw.Referral.list; dev users `uwsenior`, `superuser`). */
export const underwritingRoles = ['Staff.UnderwritingManager', 'Platform.Admin'] as const;

/** Roles that review pack-rollback exceptions (the same reviewers as the referral workbench). */
export const packRollbackReviewerRoles = underwritingRoles;

/**
 * Roles that see the WRK task inbox (permissions wrk.Activity.list / get, queue visibility of the PSR and
 * pack-rollback queues; Work's WorkCatalog). The API still filters every list to the queues the caller may see.
 */
export const workInboxRoles = [
  'Staff.UnderwritingManager',
  'Staff.Underwriter',
  'Platform.Admin',
] as const;

/** Roles that work distance-withdrawal requests (permission chn.Withdrawal.list). */
export const withdrawalRoles = ['Staff.Underwriter', 'Staff.Billing', 'Platform.Admin'] as const;

/** Roles that see the billing back-office screens: refunds, receivables, releases, delinquency. */
export const billingRoles = ['Staff.Billing', 'Staff.BillingManager'] as const;

/** Roles that manage pack activation (illustrative; permission mkt.Pack.list). */
export const packRoles = [
  'Platform.ReleaseManager',
  'Platform.DesignAuthority',
  'Platform.Admin',
] as const;

/** Roles that work data migration (permissions mig.Batch.*, mig.Routing.*; dev role Staff.MigrationManager). */
export const migrationRoles = ['Staff.MigrationManager', 'Platform.Admin'] as const;

/** Roles that work the DSAR console (permissions cmp.Dsar.*, cmp.DisclosureLog.*). */
export const dsarRoles = ['Staff.ComplianceOfficer', 'Platform.Admin'] as const;

/** Roles that work the compliance landing: complaints and statutory clocks (cmp.Complaint.*, cmp.Clock.*). */
export const complianceRoles = ['Staff.ComplianceOfficer', 'Platform.Admin'] as const;

/** Roles that administer the AI control plane (illustrative; plt.AiToggle.set / plt.Ai.killSwitch). */
export const aiAdminRoles = ['Platform.Admin', 'Platform.AiGovernor'] as const;

/** Everyone who works the reinsurance screens (D-SL4-16); `Platform.Admin` for the superuser. */
export const reinsuranceRoles = [
  'Staff.ReinsuranceAccountant',
  'Staff.ReinsuranceManager',
  'Staff.RecoverySpecialist',
  'Platform.Admin',
] as const;

export function hasAnyRole(userRoles: readonly string[], allowed: readonly string[]): boolean {
  return userRoles.some((role) => allowed.includes(role));
}
