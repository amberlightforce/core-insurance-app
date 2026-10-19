// Dev users of claims slice 4 (SL4-PLT): ids for API sign-in and EXACT display names for the dev sign-in page (PITFALLS 30).
// Names follow orchestration/briefs/sl4/SL4-PLT.md; if SL4-PLT lands with other names, change them here only.
export const SL4_USERS = {
  recovery: { id: 'recovery', displayName: 'Dev Recovery Specialist (synthetic)' },
  riacct: { id: 'riacct', displayName: 'Dev Reinsurance Accountant (synthetic)' },
  rimgr: { id: 'rimgr', displayName: 'Dev Reinsurance Manager (synthetic)' },
} as const;

export type Sl4User = keyof typeof SL4_USERS;
