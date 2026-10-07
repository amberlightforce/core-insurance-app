import { CircleAlert, RotateCcw } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { Button } from '../Button';
import { SkeletonBlock } from '../States';
import { formatTime } from '../../../format/dates';
import { formatKpiValue, type KpiFormat } from './format';
import styles from './KpiTile.module.css';
import { Sparkline } from './Sparkline';
import { trendTone } from './sparklinePath';

export interface HeroMetricProps {
  /** «Ασφάλιστρα νέας παραγωγής Οκτ.». */
  label: string;
  value: number | null;
  format?: KpiFormat;
  currency?: string;
  /** Preformatted full value (the hero always shows the full value, never abbreviated). */
  fullValue?: string;
  /** Comparison line, e.g. «104 % του πλάνου». */
  comparison?: string;
  trend?: number[];
  higherIsBetter?: boolean;
  /** Accessible summary of the trend; defaults to «Τάση: από … σε …». */
  trendLabel?: string;
  asOf?: Date | string;
  isStale?: boolean;
  state?: 'ready' | 'loading' | 'error';
  onRetry?: () => void;
}

/**
 * Hero metric (Part 2 §4.27): one per dashboard, display.xl value, comparison line and a 64 px trend with the
 * horizon fill. Sits on the canopy card (the caller's). Money never animates (D-FE-05); no count-up.
 */
export function HeroMetric({
  label,
  value,
  format = 'money',
  currency = 'EUR',
  fullValue,
  comparison,
  trend,
  higherIsBetter = true,
  trendLabel,
  asOf,
  isStale = false,
  state = 'ready',
  onRetry,
}: HeroMetricProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const full =
    fullValue ?? (value === null ? null : formatKpiValue(value, format, region, currency).full);
  const asOfText =
    asOf === undefined ? null : asOf instanceof Date ? formatTime(asOf, region) : asOf;

  const summary =
    trendLabel ??
    (trend && trend.length > 1
      ? t('kpiTile.trend', {
          from: formatKpiValue(trend[0] ?? 0, format, region, currency).short,
          to: formatKpiValue(trend[trend.length - 1] ?? 0, format, region, currency).short,
        })
      : '');

  return (
    <div
      className={styles.hero}
      role="group"
      aria-label={
        state === 'ready' && full !== null
          ? t('heroMetric.name', {
              label,
              value: full,
              comparison: comparison ?? '',
              hasComparison: comparison ? 'yes' : 'no',
            })
          : label
      }
      aria-busy={state === 'loading' || undefined}
    >
      <span className={styles.heroLabel}>{label}</span>
      {state === 'loading' ? (
        <div className={styles.loading}>
          <span className="ds-visually-hidden">{t('kpiTile.loading')}</span>
          <SkeletonBlock width="240px" height="40px" shape="block" />
          <SkeletonBlock width="160px" />
        </div>
      ) : state === 'error' || full === null ? (
        <div className={styles.error}>
          <span className={styles.heroValue} aria-hidden="true">
            —
          </span>
          <span className={styles.errorText}>
            <Icon icon={CircleAlert} size={14} />
            {t('kpiTile.unavailable')}
          </span>
          {onRetry ? (
            <Button variant="ghost" size="sm" icon={RotateCcw} onPress={onRetry}>
              {t('kpiTile.retry')}
            </Button>
          ) : null}
        </div>
      ) : (
        <>
          <span className={styles.heroValue}>{full}</span>
          {comparison ? <span className={styles.comparison}>{comparison}</span> : null}
          {trend && trend.length > 1 ? (
            <Sparkline
              values={trend}
              height={64}
              tone={trendTone(trend, higherIsBetter)}
              label={summary}
              draw={format !== 'money'}
            />
          ) : null}
        </>
      )}
      {asOfText ? (
        <span className={styles.asOf} data-stale={isStale || undefined}>
          {t('kpiTile.asOf', { time: asOfText })}
          {isStale ? <span className="ds-visually-hidden">{t('kpiTile.staleNote')}</span> : null}
        </span>
      ) : null}
    </div>
  );
}
