import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';

import { useRegionFormat } from '../../../design-system';
import { formatInteger } from '../../../format';
import { countOf, referralQueues, type ReferralQueue, type ReferralQueueCounts } from '../api';
import styles from './Workbench.module.css';

export interface ViewsRailProps {
  active: ReferralQueue;
  /** Counts of every queue (they come with each page of the list); undefined until the first page arrived. */
  counts: ReferralQueueCounts | undefined;
  onSelect: (queue: ReferralQueue) => void;
}

/**
 * The work views (IB-03, mockup `nav.views`): My referrals / Team / Completed today / Rejected with their counts and
 * the day's progress card. «Λήγουν σήμερα» and «Με όρους σε εκκρεμότητα» are not built: there is no SLA and no
 * conditions yet (D-SL5-03).
 */
export function ViewsRail({ active, counts, onSelect }: ViewsRailProps) {
  const { t } = useTranslation('underwriting');
  const region = useRegionFormat();
  const done = counts?.decidedByMeToday;
  const mine = counts?.mine;
  // Progress = decided by me today ÷ (that + mine); not shown when the mine count is not computed.
  const total = done !== undefined && mine !== null && mine !== undefined ? done + mine : null;
  const percent = total && done !== undefined ? Math.round((done / total) * 100) : 0;

  return (
    <nav className={styles.rail} aria-label={t('views.label')}>
      <p className={styles.overline}>{t('views.heading')}</p>
      {referralQueues.map((queue) => {
        const count = countOf(counts, queue);
        return (
          <button
            key={queue}
            type="button"
            className={styles.view}
            aria-current={queue === active ? 'true' : undefined}
            onClick={() => {
              onSelect(queue);
            }}
          >
            <span>{t(`views.${queue}`)}</span>
            <span
              className={styles.count}
              {...(count === null ? { title: t('views.countUnknown') } : {})}
            >
              {count === undefined || count === null ? '–' : formatInteger(count, region)}
            </span>
          </button>
        );
      })}
      <div className={styles.spacer} />
      {total !== null && done !== undefined ? (
        <div className={styles.progress}>
          <p className={styles.caption}>{t('views.progress')}</p>
          <p className={styles.progressValue}>{t('views.progressValue', { done, total })}</p>
          <div
            className={styles.track}
            role="progressbar"
            aria-label={t('views.progress')}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percent}
          >
            <div className={styles.fill} style={{ '--_fill': percent / 100 } as CSSProperties} />
          </div>
        </div>
      ) : null}
    </nav>
  );
}
