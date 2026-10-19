import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  EmptyState,
  Select,
  dateColumn,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import {
  useReceivables,
  type ReceivableFilter,
  type ReceivableSourceType,
  type ReceivableStatus,
  type ReceivableView,
} from './api';
import { ClaimLink, CounterpartyName, ReceivableStatusPill } from './ReceivableParts';
import { canApproveReleases } from './roles';

const ALL = 'ALL';
const statuses: ReceivableStatus[] = ['OPEN', 'PARTIALLY_PAID', 'PAID', 'CANCELLED'];
const sources: ReceivableSourceType[] = ['CLM_CLAIM_PAYMENT', 'FS_CLEARING'];

/** Receivables «Απαιτήσεις»: money owed to the insurer by recoveries and Friendly Settlement clearing (REQ-BIL-346). */
export function ReceivablesListPage() {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const [status, setStatus] = useState<string>(ALL);
  const [source, setSource] = useState<string>(ALL);
  const filter: ReceivableFilter = {
    ...(status !== ALL ? { status: status as ReceivableStatus } : {}),
    ...(source !== ALL ? { sourceType: source as ReceivableSourceType } : {}),
  };
  const query = useReceivables(filter);

  const columns = useMemo<DataColumn<ReceivableView>[]>(
    () => [
      textColumn<ReceivableView>('source', t('receivables.columns.source'), (r) =>
        t(`receivables.source.${r.sourceType}`),
      ),
      textColumn<ReceivableView>('purpose', t('receivables.columns.purpose'), (r) =>
        t(`receivables.purpose.${r.purpose}`),
      ),
      statusColumn<ReceivableView>(
        'counterparty',
        t('receivables.columns.counterparty'),
        (r) => r.counterpartyPartyId,
        (r) => <CounterpartyName partyId={r.counterpartyPartyId} />,
      ),
      statusColumn<ReceivableView>(
        'claim',
        t('receivables.columns.claim'),
        (r) => r.claimId ?? '',
        (r) => <ClaimLink claimId={r.claimId} />,
      ),
      identifierColumn<ReceivableView>(
        'reference',
        t('receivables.columns.reference'),
        (r) => r.paymentReference,
      ),
      moneyColumn<ReceivableView>(
        'amount',
        t('receivables.columns.amount'),
        (r) => r.amount.amount,
        {
          currency: 'EUR',
        },
      ),
      moneyColumn<ReceivableView>(
        'open',
        t('receivables.columns.open'),
        (r) => r.openAmount.amount,
        { currency: 'EUR' },
      ),
      dateColumn<ReceivableView>('due', t('receivables.columns.due'), (r) => r.dueDate),
      statusColumn<ReceivableView>(
        'status',
        t('receivables.columns.status'),
        (r) => r.status,
        (r) => <ReceivableStatusPill status={r.status} />,
        { size: 180 },
      ),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('receivables.title')}
        description={t('receivables.subtitle')}
        actions={
          <>
            {canApproveReleases() ? (
              <LinkButton variant="secondary" to="/billing/releases">
                {t('releases.open')}
              </LinkButton>
            ) : null}
            <LinkButton variant="secondary" to="/billing">
              {t('refunds.backToBilling')}
            </LinkButton>
          </>
        }
      />
      <Section title={t('receivables.listTitle')} family="teal" count={query.data?.items.length}>
        <div className={styles.grid}>
          <Select
            label={t('receivables.filters.status')}
            id="receivables-status"
            options={[
              { id: ALL, label: t('receivables.filters.all') },
              ...statuses.map((s) => ({ id: s, label: t(`receivables.status.${s}`) })),
            ]}
            value={status}
            onChange={(v) => {
              setStatus(v ?? ALL);
            }}
          />
          <Select
            label={t('receivables.filters.source')}
            id="receivables-source"
            options={[
              { id: ALL, label: t('receivables.filters.all') },
              ...sources.map((s) => ({ id: s, label: t(`receivables.source.${s}`) })),
            ]}
            value={source}
            onChange={(v) => {
              setSource(v ?? ALL);
            }}
          />
        </div>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<ReceivableView>
              aria-label={t('receivables.listTitle')}
              columns={columns}
              data={page.items}
              getRowId={(r) => r.receivableId}
              onOpen={(r) => {
                void navigate(`/billing/receivables/${r.receivableId}`);
              }}
              emptyState={
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('receivables.emptyTitle')}
                  description={t('receivables.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
