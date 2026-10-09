import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  EmptyState,
  dateColumn,
  moneyColumn,
  queueColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { refundsOf, useRefunds, type RefundView } from './api';
import { RefundStatePill } from './RefundParts';

/** Short, non-personal label of a refund id for the queue row. */
function shortId(id: string): string {
  return id.slice(0, 8);
}

function RefundTable({
  rows,
  label,
  emptyTitle,
  emptyBody,
  emptyKind,
}: {
  rows: readonly RefundView[];
  label: string;
  emptyTitle: string;
  emptyBody: string;
  emptyKind: 'done' | 'first-use';
}) {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const columns = useMemo<DataColumn<RefundView>[]>(
    () => [
      // v3 queue row: what is asked over the object in mono and who asked.
      queueColumn<RefundView>(
        'refund',
        t('refunds.columns.refund'),
        (r) => ({
          primary: t('refunds.queuePrimary'),
          id: `${shortId(r.refundId)} · ${r.payee.maskedIban}`,
          fact: r.requestedBy,
        }),
        { size: 320 },
      ),
      moneyColumn<RefundView>('amount', t('refunds.columns.amount'), (r) => r.amount.amount, {
        currency: 'EUR',
      }),
      statusColumn<RefundView>(
        'state',
        t('refunds.columns.state'),
        (r) => r.state,
        (r) => <RefundStatePill state={r.state} />,
        { size: 200 },
      ),
      textColumn<RefundView>('approval', t('refunds.columns.approval'), (r) =>
        t(`refunds.approval.${r.approvalState}`),
      ),
      dateColumn<RefundView>('proposed', t('refunds.columns.requestedAt'), (r) => r.proposedAt),
      statusColumn<RefundView>(
        'account',
        t('refunds.columns.account'),
        (r) => r.billingAccountId,
        (r) => (
          <LinkButton to={`/billing/accounts/${r.billingAccountId}`}>
            {t('refunds.detail.openAccount')}
          </LinkButton>
        ),
      ),
    ],
    [t],
  );
  return (
    <SimpleTable<RefundView>
      aria-label={label}
      columns={columns}
      data={rows}
      getRowId={(r) => r.refundId}
      rowVariant="queue"
      onOpen={(r) => {
        void navigate(`/billing/refunds/${r.refundId}`);
      }}
      emptyState={
        <EmptyState
          kind={emptyKind}
          headingLevel={3}
          headline={emptyTitle}
          description={emptyBody}
        />
      }
    />
  );
}

/** Refund approvals inbox (follows the claims approvals inbox): refunds waiting for a decision, then all refunds. */
export function RefundsInboxPage() {
  const { t } = useTranslation('billing');
  const pending = useRefunds({ state: 'PENDING_APPROVAL' });
  const all = useRefunds({});

  const pendingRows = useMemo(() => refundsOf(pending.data), [pending.data]);
  const allRows = useMemo(() => refundsOf(all.data), [all.data]);

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        description={t('refunds.subtitle')}
        overline={t('overline')}
        title={t('refunds.title')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <Section title={t('refunds.pendingTitle')} family="plum" count={pendingRows.length}>
        <QueryView query={pending}>
          {() => (
            <RefundTable
              rows={pendingRows}
              label={t('refunds.pendingTitle')}
              emptyKind="done"
              emptyTitle={t('refunds.emptyPendingTitle')}
              emptyBody={t('refunds.emptyPendingBody')}
            />
          )}
        </QueryView>
      </Section>
      <Section title={t('refunds.allTitle')} count={allRows.length}>
        <QueryView query={all}>
          {() => (
            <RefundTable
              rows={allRows}
              label={t('refunds.allTitle')}
              emptyKind="first-use"
              emptyTitle={t('refunds.emptyAllTitle')}
              emptyBody={t('refunds.emptyAllBody')}
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
