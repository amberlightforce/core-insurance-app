import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  Button,
  EmptyState,
  TextField,
  dateColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useWithdrawals, type WithdrawalListItem } from './api';
import { WithdrawalStatusPill } from './parts';

/** Staff list of withdrawal requests (chn.Withdrawal.list). */
export function WithdrawalListPage() {
  const { t } = useTranslation('channels');
  const navigate = useNavigate();
  const [draft, setDraft] = useState('');
  const [requestNumber, setRequestNumber] = useState('');
  const query = useWithdrawals(requestNumber);

  const columns: DataColumn<WithdrawalListItem>[] = [
    textColumn<WithdrawalListItem>(
      'number',
      t('withdrawal.fields.requestNumber'),
      (r) => r.requestNumber,
    ),
    statusColumn<WithdrawalListItem>(
      'status',
      t('withdrawal.fields.status'),
      (r) => r.status,
      (r) => <WithdrawalStatusPill status={r.status} />,
    ),
    textColumn<WithdrawalListItem>(
      'refund',
      t('withdrawal.fields.refundStatus'),
      (r) => r.refundStatus ?? null,
    ),
    dateColumn<WithdrawalListItem>(
      'declared',
      t('withdrawal.fields.declaredAt'),
      (r) => r.declaredAt ?? null,
    ),
    dateColumn<WithdrawalListItem>(
      'confirmed',
      t('withdrawal.fields.confirmedAt'),
      (r) => r.confirmedAt ?? null,
    ),
  ];

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('withdrawal.list.title')}
        description={t('withdrawal.list.subtitle')}
      />
      <Section
        title={t('withdrawal.list.section')}
        {...(query.data ? { count: query.data.items.length } : {})}
      >
        <form
          className={styles.actions}
          aria-label={t('withdrawal.list.search')}
          onSubmit={(event) => {
            event.preventDefault();
            setRequestNumber(draft.trim());
          }}
        >
          <TextField label={t('withdrawal.list.search')} value={draft} onChange={setDraft} />
          <Button
            variant="secondary"
            onPress={() => {
              setRequestNumber(draft.trim());
            }}
          >
            {t('withdrawal.list.searchAction')}
          </Button>
        </form>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<WithdrawalListItem>
              aria-label={t('withdrawal.list.section')}
              columns={columns}
              data={page.items}
              getRowId={(r) => r.withdrawalRequestId}
              onOpen={(r) => {
                void navigate(`/channels/withdrawals/${r.withdrawalRequestId}`);
              }}
              emptyState={
                <EmptyState
                  kind="done"
                  headingLevel={3}
                  headline={t('withdrawal.list.emptyTitle')}
                  description={t('withdrawal.list.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
        {query.hasNextPage ? (
          <Button
            variant="secondary"
            isLoading={query.isFetchingNextPage}
            onPress={() => {
              void query.fetchNextPage();
            }}
          >
            {t('withdrawal.list.more')}
          </Button>
        ) : null}
      </Section>
    </div>
  );
}
