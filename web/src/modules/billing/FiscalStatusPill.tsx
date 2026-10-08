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
 * Fiscal-document status of an invoice. In the slice the document comes from the CMP stub, not from the tax
 * authority, so the pill always carries the «stub» marker (D-SLC-10b).
 */
export function FiscalStatusPill({
  status,
  compact = false,
}: {
  status: string | null | undefined;
  /** Short text and stub marker, for narrow cards (e.g. the policy file's invoice list). */
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
      subLabel={compact ? t('fiscal.stubShort') : t('fiscal.stub')}
      announceChanges={false}
    />
  );
}
