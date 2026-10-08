import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import { Banner, Button, textColumn, type DataColumn } from '../../design-system';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useIssueText, useJobIssues, type UwIssueItem } from './uwIssues';

/** Statuses that matter for the bind: still blocking, or decided and holding. History (Closed, Invalidated) is left out. */
const shown = new Set(['Open', 'Rejected', 'Approved', 'ApprovedWithConditions']);

/**
 * Why the bind stopped on UW_ISSUES_OPEN (REQ-UW-087 interstitial, slice form): every underwriting issue of the job
 * in plain words — what it is, why it was raised, its status and the next step (ask a senior underwriter to approve
 * it, then bind again; or change the answer).
 */
export function BindReferrals({ jobId }: { jobId: string }) {
  const { t } = useTranslation('uw');
  const query = useJobIssues(jobId, true);
  const text = useIssueText();
  const columns = useMemo<DataColumn<UwIssueItem>[]>(
    () => [
      textColumn<UwIssueItem>('issue', t('columns.issue'), (i) => text(i).type, { size: 180 }),
      textColumn<UwIssueItem>('reason', t('columns.reason'), (i) => text(i).why, { size: 280 }),
      textColumn<UwIssueItem>('status', t('columns.status'), (i) => text(i).status, { size: 160 }),
      textColumn<UwIssueItem>('next', t('columns.nextStep'), (i) => text(i).next, { size: 360 }),
    ],
    [t, text],
  );
  return (
    <div className={styles.stack}>
      <Banner variant="warning" title={t('bindBlocked.title')}>
        {t('bindBlocked.body')}
      </Banner>
      <QueryView query={query}>
        {(page) => (
          <SimpleTable<UwIssueItem>
            aria-label={t('bindBlocked.table')}
            columns={columns}
            data={page.items.filter((i) => shown.has(i.status))}
            getRowId={(i) => i.id}
          />
        )}
      </QueryView>
      <div className={styles.actions}>
        <Button
          variant="secondary"
          isLoading={query.isFetching}
          onPress={() => {
            void query.refetch();
          }}
        >
          {t('bindBlocked.refresh')}
        </Button>
      </div>
    </div>
  );
}
