import { Sparkles, Wifi, WifiOff } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useRegionFormat } from '../design-system/preferences';
import { Icon } from '../design-system/icons';
import { cx } from '../design-system/utils/cx';
import { athensZoneAbbreviation, formatDate, formatTime } from '../format/dates';
import styles from './AppShell.module.css';
import { useOnline } from './useOnline';

export interface StatusBarProps {
  entityName: string;
  environmentName: string;
  aiEnabled: boolean;
  backgroundJobs?: number;
  /** Shifted system clock in non-production (A-09): the clock turns warning. */
  clockShifted?: boolean;
  /** Injected clock for tests and stories. */
  now?: () => Date;
}

const systemNow = () => new Date();

const weekdayShort = (date: Date, region: string) =>
  new Intl.DateTimeFormat(region, { weekday: 'short', timeZone: 'Europe/Athens' }).format(date);

/**
 * Status bar (Part 1 §3.4): entity · environment · connectivity · AI state · Europe/Athens date and time with
 * EET/EEST · background jobs. The time updates once a minute without animation.
 */
export function StatusBar({
  entityName,
  environmentName,
  aiEnabled,
  backgroundJobs = 0,
  clockShifted = false,
  now = systemNow,
}: StatusBarProps) {
  const { t } = useTranslation('shell');
  const region = useRegionFormat();
  const online = useOnline();
  const [time, setTime] = useState(now);

  useEffect(() => {
    const id = setInterval(() => {
      setTime(now());
    }, 60_000);
    return () => {
      clearInterval(id);
    };
  }, [now]);

  return (
    <footer className={styles.statusBar} data-material="chrome" data-shell-region="contentinfo" data-print="hide">
      <span className={styles.statusItem} data-tone={online ? 'success' : 'warning'}>
        <Icon icon={online ? Wifi : WifiOff} size={12} />
        {online ? t('statusBar.online') : t('statusBar.offline')}
      </span>
      <span className={styles.statusItem}>
        {entityName} · {environmentName}
      </span>
      <span className={styles.statusItem} data-tone={aiEnabled ? 'ai' : 'neutral'}>
        <Icon icon={Sparkles} size={12} />
        {aiEnabled ? t('statusBar.aiOn') : t('statusBar.aiOff')}
      </span>
      {backgroundJobs > 0 ? (
        <span className={styles.statusItem}>{t('statusBar.jobs', { count: backgroundJobs })}</span>
      ) : null}
      <span className={styles.statusSpacer} />
      <time
        className={cx(styles.statusItem, 'ds-num')}
        data-tone={clockShifted ? 'warning' : undefined}
        dateTime={time.toISOString()}
        aria-label={`${t('statusBar.clockLabel')}: ${formatDate(time, region)} ${formatTime(time, region)}`}
      >
        {weekdayShort(time, region)} {formatDate(time, region)} · {formatTime(time, region)}{' '}
        {athensZoneAbbreviation(time)}
      </time>
    </footer>
  );
}
