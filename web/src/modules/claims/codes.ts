/**
 * Code lists of the claims screens. All are ILLUSTRATIVE (PRD-07 §16.5 open): the loss-cause list is a free code
 * list until the reference data work package, the same for duplicate reasons. Labels live in the `claims`
 * namespace under `codes.<list>.<CODE>`.
 */
export const lossCauses = ['COLLISION', 'THEFT', 'FIRE', 'GLASS', 'VANDALISM', 'WEATHER'] as const;

/** Receipt media of SCR-CLM-01 (a contract list, REQ-CLM-036). */
export const receiptMedia = [
  'TELEPHONE',
  'EMAIL',
  'LETTER',
  'FAX',
  'SMS',
  'IN_PERSON',
  'ELECTRONIC',
] as const;

/** Reason codes for the duplicate decision (illustrative). */
export const duplicateReasons = [
  'SAME_LOSS',
  'DIFFERENT_LOSS',
  'ADDITIONAL_PARTY_SAME_LOSS',
  'CUSTOMER_CONFIRMED',
] as const;

/** Reason codes for a second open exposure on the same coverage and claimant (illustrative, REQ-CLM-063). */
export const exposureDuplicateReasons = ['ADDITIONAL_DAMAGE', 'SEPARATE_INCIDENT'] as const;

/** Closure outcomes a handler can choose (Denied needs a coverage decision, a later work package). */
export const closeOutcomes = ['COMPLETED', 'WITHDRAWN', 'DUPLICATE', 'NO_PAYMENT'] as const;

/** Reasons the close guard reports per exposure (REQ-CLM-072). */
export const closeGuardReasons = ['OPEN_RESERVE', 'PAYMENT_PENDING'] as const;
