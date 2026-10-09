import { useTranslation } from 'react-i18next';

const knownKinds = new Set([
  'NEW_BUSINESS',
  'ENDORSEMENT_DEBIT',
  'ENDORSEMENT_CREDIT',
  'CANCELLATION',
  'DISTANCE_WITHDRAWAL_VOID',
  'VOID',
  'RETURN_PREMIUM',
  'REINSTATEMENT',
  'FEE',
  'REFUND',
]);

/** The translated transaction kind; an unknown code from a newer contract is shown as received, never blank. */
export function useKindLabel() {
  const { t } = useTranslation('policy');
  return (kind: string) => (knownKinds.has(kind) ? t(`packRollback.kind.${kind}`) : kind);
}
