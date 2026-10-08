import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Banner,
  Button,
  DataTable,
  EmptyState,
  LoadingState,
  moneyColumn,
  queueColumn,
  textColumn,
  useRegionFormat,
  type DataColumn,
} from '../../../design-system';
import { formatRelative } from '../../../format';
import { problemOf } from '../../staff/problem';
import { type ReferralListItem, type ReferralQueue, type useReferralList } from '../api';
import { useIssueTypeLabel } from './helpers';
import styles from './Workbench.module.css';

export interface QueuePaneProps {
  queue: ReferralQueue;
  onOpen: (jobRef: string) => void;
  /** The list query of the queue (owned by the page: the rail shows its counts). */
  list: ReturnType<typeof useReferralList>;
}

/**
 * The queue sheet (mockup `.queue`): the heading of the view, the referral rows (customer over «number · product»,
 * reason, waiting since, premium) and the entry count. A row opens with one click or Enter (D-SLC-21 (c)); ↑/↓ move
 * the cursor. The checkbox column of the mockup is not built (no bulk actions in this slice).
 */
export function QueuePane({ queue, onOpen, list }: QueuePaneProps) {
  const { t } = useTranslation('underwriting');
  const region = useRegionFormat();
  const typeLabel = useIssueTypeLabel();
  const { query, items, counts } = list;

  const columns = useMemo<DataColumn<ReferralListItem>[]>(
    () => [
      queueColumn<ReferralListItem>(
        'customer',
        t('queue.columns.customer'),
        (row) => ({
          primary: row.customer?.displayName ?? row.customer?.partyNumber ?? t('queue.noName'),
          ...(row.jobNumber ? { id: row.jobNumber } : {}),
          ...(row.productCode ? { fact: row.productCode } : {}),
        }),
        { size: 180, enableSorting: false },
      ),
      textColumn<ReferralListItem>(
        'reason',
        t('queue.columns.reason'),
        (row) => {
          const first = row.reasons[0];
          if (!first) return null;
          const label = typeLabel(first.issueType);
          return row.reasons.length > 1
            ? `${label} ${t('queue.extraReasons', { count: row.reasons.length - 1 })}`
            : label;
        },
        { size: 120, enableSorting: false },
      ),
      textColumn<ReferralListItem>(
        'waiting',
        t('queue.columns.waiting'),
        (row) => formatRelative(row.raisedAt, { region }),
        { size: 110, enableSorting: false },
      ),
      moneyColumn<ReferralListItem>(
        'premium',
        t('queue.columns.premium'),
        (row) => row.premiumTotal?.amount ?? null,
        { currency: 'EUR', size: 90, enableSorting: false },
      ),
    ],
    [t, region, typeLabel],
  );

  const getRowId = useMemo(() => (row: ReferralListItem) => row.jobRef, []);
  const getRowLabel = useMemo(() => (row: ReferralListItem) => row.jobNumber ?? row.jobRef, []);
  const openRow = useMemo(
    () => (row: ReferralListItem) => {
      onOpen(row.jobRef);
    },
    [onOpen],
  );

  const done = counts?.decidedByMeToday ?? 0;
  const mineUnknown = queue === 'MINE' && counts?.mine === null;

  let body;
  if (query.isPending) {
    body = (
      <div className={styles.centre}>
        <LoadingState immediate>{t('queue.label')}</LoadingState>
      </div>
    );
  } else if (query.isError && items.length === 0) {
    const problem = problemOf(query.error);
    body = (
      <div className={styles.notice}>
        <Banner
          variant={problem.status === 403 ? 'warning' : 'danger'}
          title={problem.status === 403 ? t('noPermission.title') : t('list.error')}
          live="none"
          actions={
            problem.status === 403 ? undefined : (
              <Button
                variant="secondary"
                size="sm"
                onPress={() => {
                  void query.refetch();
                }}
              >
                {t('errors.retry')}
              </Button>
            )
          }
        >
          {problem.status === 403 ? t('noPermission.body') : (problem.detail ?? problem.title)}
        </Banner>
      </div>
    );
  } else {
    body = (
      <div className={styles.tableWrap}>
        {mineUnknown ? (
          <div className={styles.notice}>
            <Banner variant="info" live="none" title={t('queue.mineUnknown')} />
          </div>
        ) : null}
        <DataTable<ReferralListItem>
          aria-label={t('queue.table')}
          columns={columns}
          data={items}
          getRowId={getRowId}
          getRowLabel={getRowLabel}
          rowVariant="queue"
          onOpen={openRow}
          openOnClick
          cardBreakpoint={320}
          searchable={false}
          showDensityToggle={false}
          showColumnSettings={false}
          isRefetching={query.isRefetching && !query.isFetchingNextPage}
          emptyState={
            <EmptyState
              kind="done"
              headingLevel={3}
              headline={t(`queue.emptyTitle.${queue}`)}
              description={t(`queue.emptyBody.${queue}`, { done })}
            />
          }
        />
      </div>
    );
  }

  return (
    <div className={styles.sheet}>
      <div className={styles.qhead}>
        <h2 className={styles.qtitle}>{t(`views.${queue}`)}</h2>
      </div>
      {body}
      {query.hasNextPage ? (
        <div className={styles.qfoot}>
          <span className={styles.caption}>{t('queue.footer', { count: items.length })}</span>
          <Button
            variant="ghost"
            size="sm"
            isLoading={query.isFetchingNextPage}
            onPress={() => {
              void query.fetchNextPage();
            }}
          >
            {t('queue.loadMore')}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
