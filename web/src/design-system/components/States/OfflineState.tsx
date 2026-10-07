import { WifiOff } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { Heading, type HeadingLevel } from './Heading';
import { IllOfflineCloud } from './illustrations';
import styles from './States.module.css';

export interface OfflineStateProps {
  /**
   * `banner`: the top banner over read-only cached data (the shell status bar adds MI-63).
   * `page`: nothing cached for this view; ILL-06 + the explanation.
   */
  variant?: 'banner' | 'page';
  /** Already-formatted time of the last successful sync, e.g. «14:32». */
  lastSynced?: string;
  headingLevel?: HeadingLevel;
}

/** Offline / degraded (DESIGN-B A.11). Actions elsewhere are disabled with the reason «Εκτός σύνδεσης». */
export function OfflineState({
  variant = 'banner',
  lastSynced,
  headingLevel = 2,
}: OfflineStateProps) {
  const { t } = useTranslation('ds');
  const synced = lastSynced ? t('offlineState.lastSynced', { time: lastSynced }) : null;

  if (variant === 'page') {
    return (
      <div className={styles.page} role="status">
        <IllOfflineCloud size="md" />
        <Heading level={headingLevel} className={styles.pageTitle}>
          {t('offlineState.title')}
        </Heading>
        <p className={styles.description}>{t('offlineState.banner')}</p>
        {synced ? <p className={styles.estimate}>{synced}</p> : null}
      </div>
    );
  }

  return (
    <div className={styles.banner} data-tone="warning" role="status">
      <span className={styles.bannerIcon}>
        <Icon icon={WifiOff} size={16} />
      </span>
      <div className={styles.bannerBody}>
        <span>{t('offlineState.banner')}</span>
        {synced ? <span className={styles.estimate}>{synced}</span> : null}
      </div>
    </div>
  );
}
