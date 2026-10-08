import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import {
  StatusPill,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import type { RefundBreakdownLine, RefundNettingLine, RefundState } from './api';

type Semantic = 'info' | 'success' | 'error' | 'warning' | 'pending-approval';

const stateSemantic: Record<RefundState, Semantic> = {
  PROPOSED: 'info',
  PENDING_APPROVAL: 'pending-approval',
  APPROVED: 'success',
  HELD: 'warning',
  DISBURSING: 'info',
  AWAITING_PROOF: 'info',
  PAID: 'success',
  REJECTED: 'error',
  RETURNED: 'warning',
};

/** Refund lifecycle state through the single status map (semantic family plus the localised label). */
export function RefundStatePill({ state }: { state: RefundState }) {
  const { t } = useTranslation('billing');
  return (
    <StatusPill
      semantic={stateSemantic[state]}
      subLabel={t(`refunds.state.${state}`, { defaultValue: state })}
      announceChanges={false}
    />
  );
}

/** Per charge type breakdown of a refund (REQ-BIL-184): tax and levy lines carry the rule that decided them. */
export function BreakdownTable({
  lines,
  currency,
}: {
  lines: readonly RefundBreakdownLine[];
  currency: string;
}) {
  const { t } = useTranslation('billing');
  const columns = useMemo<DataColumn<RefundBreakdownLine>[]>(
    () => [
      identifierColumn<RefundBreakdownLine>(
        'chargeType',
        t('refunds.breakdown.columns.chargeType'),
        (l) => l.chargeType,
        { size: 160 },
      ),
      textColumn<RefundBreakdownLine>(
        'category',
        t('refunds.breakdown.columns.category'),
        (l) => l.chargeCategory,
      ),
      textColumn<RefundBreakdownLine>(
        'kind',
        t('refunds.breakdown.columns.transactionKind'),
        (l) => l.transactionKind ?? null,
      ),
      textColumn<RefundBreakdownLine>(
        'rule',
        t('refunds.breakdown.columns.rule'),
        (l) => l.treatmentRuleId ?? null,
      ),
      statusColumn<RefundBreakdownLine>(
        'legal',
        t('refunds.breakdown.columns.legal'),
        (l) => l.legalStatus ?? null,
        (l) =>
          l.provisional === true ? (
            <StatusPill
              semantic="warning"
              subLabel={t('refunds.breakdown.provisional')}
              announceChanges={false}
            />
          ) : l.legalStatus ? (
            <span>{l.legalStatus}</span>
          ) : null,
        { size: 200 },
      ),
      moneyColumn<RefundBreakdownLine>(
        'amount',
        t('refunds.breakdown.columns.amount'),
        (l) => l.amount.amount,
        { currency },
      ),
    ],
    [t, currency],
  );
  if (lines.length === 0) return <p className={styles.muted}>{t('refunds.breakdown.empty')}</p>;
  return (
    <SimpleTable<RefundBreakdownLine>
      aria-label={t('refunds.breakdown.title')}
      columns={columns}
      data={lines}
      getRowId={(l) =>
        `${l.transactionId}|${l.chargeType}|${l.chargeCategory}|${l.treatmentRuleId ?? ''}|${l.amount.amount}`
      }
    />
  );
}

/** Netting against open invoices and other credits; empty is stated, not hidden. */
export function NettingTable({
  lines,
  currency,
}: {
  lines: readonly RefundNettingLine[];
  currency: string;
}) {
  const { t } = useTranslation('billing');
  const columns = useMemo<DataColumn<RefundNettingLine>[]>(
    () => [
      textColumn<RefundNettingLine>('kind', t('refunds.netting.columns.kind'), (l) =>
        t(`refunds.netting.kind.${l.kind}`),
      ),
      identifierColumn<RefundNettingLine>(
        'invoice',
        t('refunds.netting.columns.invoice'),
        (l) => l.invoiceId ?? null,
        { size: 200 },
      ),
      moneyColumn<RefundNettingLine>(
        'amount',
        t('refunds.netting.columns.amount'),
        (l) => l.amount.amount,
        { currency },
      ),
    ],
    [t, currency],
  );
  if (lines.length === 0) return <p className={styles.muted}>{t('refunds.netting.none')}</p>;
  return (
    <SimpleTable<RefundNettingLine>
      aria-label={t('refunds.netting.title')}
      columns={columns}
      data={lines}
      getRowId={(l) => `${l.kind}|${l.invoiceId ?? ''}|${l.amount.amount}`}
    />
  );
}
