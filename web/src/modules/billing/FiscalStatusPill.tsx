import { useTranslation } from 'react-i18next';

import { StatusPill, type EntityState } from '../../design-system';

type FiscalState = EntityState<'fiscalDocument'>;

const states: Record<string, FiscalState> = {
  PENDING: 'pending',
  REGISTERED: 'registered',
  REJECTED: 'rejected',
  REQUEST_FAILED: 'rejected',
};

/**
 * Fiscal-document status of an invoice, as reported by the billing contract. The contract carries no stub
 * indicator, so none is shown; the channel is never bound as a stub in production.
 */
export function FiscalStatusPill({
  status,
  compact = false,
}: {
  status: string | null | undefined;
  /** Short text, for narrow cards (e.g. the policy file's invoice list). */
  compact?: boolean;
}) {
  const { t } = useTranslation('billing');
  // A missing status shows a dash, never a raw translation key.
  if (!status) return <span>—</span>;
  const mapped = states[status];
  if (!mapped) return <span>{t(`fiscal.status.${status}`, { defaultValue: status })}</span>;
  return (
    <StatusPill
      entity="fiscalDocument"
      state={mapped}
      {...(compact ? { text: t(`fiscal.status.${status}`, { defaultValue: status }) } : {})}
      announceChanges={false}
    />
  );
}
