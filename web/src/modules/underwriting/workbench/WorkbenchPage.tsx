import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';

import { PermissionDenied, SplitView } from '../../../design-system';
import { useDevSession } from '../../../dev-auth/devAuth';
import {
  isReferralQueue,
  useReferralList,
  type ReferralQueue,
  type ReferralQueueCounts,
} from '../api';
import { DetailPane } from './DetailPane';
import { QueuePane } from './QueuePane';
import { ViewsRail } from './ViewsRail';
import { workbenchRoles } from './helpers';
import styles from './Workbench.module.css';

const uuidShape = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function Workbench() {
  const { t } = useTranslation('underwriting');
  const [params, setParams] = useSearchParams();
  const rawQueue = params.get('queue');
  const queue: ReferralQueue = isReferralQueue(rawQueue) ? rawQueue : 'MINE';
  // Only an opaque job id travels in the URL (PITFALLS 18-19); anything else is ignored.
  const rawJob = params.get('job');
  const job = rawJob !== null && uuidShape.test(rawJob) ? rawJob : null;

  const list = useReferralList(queue);
  // The rail keeps the last counts while another view loads (derived state, set during render).
  const [counts, setCounts] = useState<ReferralQueueCounts | undefined>(undefined);
  if (list.counts && list.counts !== counts) setCounts(list.counts);

  const selectQueue = useCallback(
    (next: ReferralQueue) => {
      setParams(next === 'MINE' ? {} : { queue: next });
    },
    [setParams],
  );
  const openJob = useCallback(
    (jobRef: string) => {
      setParams(queue === 'MINE' ? { job: jobRef } : { queue, job: jobRef });
    },
    [queue, setParams],
  );
  const closeJob = useCallback(() => {
    setParams(queue === 'MINE' ? {} : { queue });
  }, [queue, setParams]);

  return (
    <div className={styles.root}>
      <div className={styles.screen}>
        <ViewsRail active={queue} counts={counts} onSelect={selectQueue} />
        <div className={styles.main}>
          <SplitView
            listLabel={t('queue.label')}
            detailLabel={t('detail.label')}
            isDetailOpen={job !== null}
            onCloseDetail={closeJob}
            defaultListWidth={520}
            storageKey="coreins.uw.listWidth"
            list={<QueuePane queue={queue} onOpen={openJob} list={list} />}
            detail={<DetailPane jobRef={job} />}
          />
        </div>
      </div>
    </div>
  );
}

/**
 * «Ανάληψη κινδύνου» (SCR-UW-01 / SCR-UW-03 subset): the referral workbench. Holders of an underwriting role see
 * it, everyone else the no-permission state; the API enforces `uw.Referral.list` on every call regardless.
 */
export function WorkbenchPage() {
  const { t } = useTranslation('underwriting');
  const session = useDevSession();
  const roles = session?.user.roles ?? [];
  if (!roles.some((role) => (workbenchRoles as readonly string[]).includes(role))) {
    return <PermissionDenied restriction={t('noPermission.body')} />;
  }
  return <Workbench />;
}
