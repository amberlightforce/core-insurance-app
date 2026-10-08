import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';

import type {
  FinancialTransactionView,
  SetApprovalView,
  TransactionSetView,
} from '../../api/types';
import {
  Banner,
  StatusPill,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useRefreshMoney, useTransactionSet } from './api';
import { ChecklistPill, SetStatusPill } from './MoneyPills';

/** The approvals a referred set needs (D-SL2-13: one request per authority type and cost type), as a checklist. */
export function ApprovalChecklist({ approvals }: { approvals: readonly SetApprovalView[] }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  if (approvals.length === 0) return null;
  const done = approvals.filter((a) => a.status === 'APPROVED').length;
  return (
    <div className={styles.stack}>
      <p className="ds-label">{t('set.checklist.title', { done, total: approvals.length })}</p>
      <ul className={styles.stack} aria-label={t('set.checklist.label')}>
        {approvals.map((a) => (
          <li key={a.approvalRequestId} className={styles.tags}>
            <ChecklistPill status={a.status} />
            <span>
              {t('set.checklist.item', {
                type: t(`set.authority.${a.authorityType.replaceAll('.', '_')}`, {
                  defaultValue: a.authorityType,
                }),
                costType: t(`codes.costType.${a.costType}`, { defaultValue: a.costType }),
                amount: fmt.money(a.amount),
              })}
            </span>
            {a.decidedAt ? (
              <span className="ds-caption">{fmt.dateTime(a.decidedAt)}</span>
            ) : (
              <LinkButton to={`/claims/approvals/${a.approvalRequestId}`}>
                {t('set.checklist.open')}
              </LinkButton>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The transactions of a set; lines added by the system (reserve top-up, final release) are flagged. */
export function SetTransactions({
  transactions,
  label,
}: {
  transactions: readonly FinancialTransactionView[];
  label: string;
}) {
  const { t } = useTranslation('claims');
  const columns = useMemo<DataColumn<FinancialTransactionView>[]>(
    () => [
      textColumn<FinancialTransactionView>('number', t('set.columns.number'), (r) => r.txnNumber, {
        size: 160,
      }),
      textColumn<FinancialTransactionView>('kind', t('set.columns.kind'), (r) =>
        t(`set.kind.${r.kind}`),
      ),
      textColumn<FinancialTransactionView>(
        'line',
        t('set.columns.line'),
        (r) =>
          `${t(`codes.costType.${r.costType}`, { defaultValue: r.costType })} · ${t(`codes.costCategory.${r.costCategory}`, { defaultValue: r.costCategory })}`,
      ),
      moneyColumn<FinancialTransactionView>(
        'amount',
        t('set.columns.amount'),
        (r) => r.amount.amount,
        {
          currency: 'EUR',
        },
      ),
      statusColumn<FinancialTransactionView>(
        'origin',
        t('set.columns.origin'),
        (r) => (r.proposed ? 'proposed' : 'manual'),
        (r) =>
          r.proposed ? (
            <StatusPill
              semantic="info"
              subLabel={t(`set.proposed.${r.reasonCode ?? 'OTHER'}`, {
                defaultValue: t('set.proposed.OTHER'),
              })}
              announceChanges={false}
            />
          ) : (
            <span>{t('set.manual')}</span>
          ),
      ),
    ],
    [t],
  );
  return (
    <SimpleTable<FinancialTransactionView>
      aria-label={label}
      columns={columns}
      data={transactions}
      getRowId={(r) => r.txnId}
    />
  );
}

/** One set with status, its transactions and the approval checklist; polls while it waits for approval. */
export function SetCard({ claimId, setId }: { claimId: string; setId: string }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const query = useTransactionSet(setId);
  const refresh = useRefreshMoney(claimId);
  const set = query.data?.set;
  const lastStatus = useRef<string | undefined>(undefined);

  // A decision applied by the worker changes balances and payments: reload them when the set status moves.
  useEffect(() => {
    if (set && lastStatus.current !== undefined && lastStatus.current !== set.status)
      void refresh();
    lastStatus.current = set?.status;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- refresh is stable enough; only status changes matter
  }, [set?.status]);

  if (query.isPending) return <p className={styles.muted}>{t('set.loading')}</p>;
  if (query.isError || !set) return <Banner variant="warning" title={t('set.loadFailed')} />;
  return <SetBody set={set} when={fmt.dateTime(set.createdAt)} />;
}

export function SetBody({ set, when }: { set: TransactionSetView; when: string }) {
  const { t } = useTranslation('claims');
  const waiting = set.status === 'PENDING_APPROVAL';
  return (
    <article className={styles.stack} aria-label={t('set.label', { id: set.setId.slice(0, 8) })}>
      <div className={styles.tags}>
        <SetStatusPill status={set.status} />
        <span className="ds-mono">{set.setId.slice(0, 8)}</span>
        <span className="ds-caption">{when}</span>
        {set.maker ? (
          <span className="ds-caption">{t('set.maker', { maker: set.maker })}</span>
        ) : null}
      </div>
      <SetTransactions
        transactions={set.transactions}
        label={t('set.transactions', { id: set.setId.slice(0, 8) })}
      />
      {set.approvals && set.approvals.length > 0 ? (
        <ApprovalChecklist approvals={set.approvals} />
      ) : null}
      {waiting ? <p className={styles.muted}>{t('set.waiting')}</p> : null}
      {set.status === 'REJECTED' ? (
        <Banner variant="danger" live="none" title={t('set.rejected')}>
          {set.rejectionReason
            ? t(`set.rejectionReason.${set.rejectionReason}`, { defaultValue: set.rejectionReason })
            : null}
        </Banner>
      ) : null}
    </article>
  );
}
