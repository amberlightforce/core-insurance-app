// Dev users of claims slice 4 (SL4-PLT): ids for API sign-in and EXACT display names for the dev sign-in page (PITFALLS 30).
export const SL4_USERS = {
  recovery: { id: 'recovery', displayName: 'Dev Recovery Specialist (synthetic)' },
  riacct: { id: 'riacct', displayName: 'Dev Reinsurance Accountant (synthetic)' },
  rimgr: { id: 'rimgr', displayName: 'Dev Reinsurance Manager (synthetic)' },
  billingmgr: { id: 'billingmgr', displayName: 'Billing Manager' },
  claims: { id: 'claims', displayName: 'Dev Claims Handler (synthetic)' },
  claimsmgr: { id: 'claimsmgr', displayName: 'Dev Claims Manager (synthetic)' },
  billing: { id: 'billing', displayName: 'Dev Billing Clerk (synthetic)' },
  finance: { id: 'finance', displayName: 'Dev Finance Accountant (synthetic)' },
} as const;

export type Sl4User = keyof typeof SL4_USERS;
