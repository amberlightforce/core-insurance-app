/**
 * The configuration keys the maker can pick from (mirror of the server key registry, `ConfigKeys.All`; the server
 * validates every key, so a key typed by hand is still accepted when it is registered).
 * TODO(hardening): read the catalogue from an API once the key registry is exposed.
 */
export const configKeys: readonly string[] = [
  'tax.ipt.rate.general',
  'tax.ipt.rate.fire',
  'tax.ipt.motor_class',
  'tax.ipt.liability_point',
  'tax.levy.auxfund.ceiling_rate',
  'tax.levy.auxfund.split_basis',
  'tax.levy.auxfund.split.insurer_share',
  'tax.levy.auxfund.split.policyholder_share',
  'tax.levy.auxfund.components',
  'tax.levy.auxfund.establishment_component',
  'tax.levy.auxfund.ph_stamp_duty_rate',
  'tax.levy.auxfund.base',
  'tax.levy.auxfund.midterm_base',
  'tax.levy.auxfund.cancellation_treatment',
  'cur.transaction',
  'cur.functional',
  'cur.rounding.default',
  'cur.order_of_operations',
  'clm.history.lookback_years',
  'bil.writeoff.tolerance',
];

export const legalStatuses = [
  'NotRegulatory',
  'Settled',
  'PendingOpinion',
  'Verify',
  'Unverified',
  'Uncertain',
  'MarketPractice',
  'Draft',
] as const;

export const changeStatuses = [
  'DRAFT',
  'PENDING_APPROVAL',
  'SCHEDULED',
  'APPLIED',
  'REJECTED',
  'WITHDRAWN',
] as const;
