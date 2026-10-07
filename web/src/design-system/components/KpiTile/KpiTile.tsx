import { ArrowRight, CircleAlert, CircleHelp, Clock, RotateCcw } from 'lucide-react';
import { useEffect, useRef } from 'react';
import {
  Button as AriaButton,
  Dialog,
  DialogTrigger,
  Focusable,
  Heading,
  Link,
  Popover,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { Button } from '../Button';
import { SkeletonBlock } from '../States';
import { Tooltip } from '../Tooltip';
import { formatDelta, formatKpiValue, formatTime, type DeltaUnit, type KpiFormat } from './format';
import styles from './KpiTile.module.css';
import { Sparkline } from './Sparkline';
import { trendTone } from './sparklinePath';

export interface KpiDelta {
  /** Change in `unit`s (4.2 = +4,2 % when `unit` is `percent`). */
  value: number;
  unit?: DeltaUnit;
  /** Comparison basis in words: «vs προηγ. μήνα», «vs πλάνο». */
  basis: string;
}

export interface KpiDefinition {
  formula: string;
  /** Source mart or read model. */
  source: string;
  /** Freshness of the definition's data, e.g. «Δεδομένα έως 06/10/2026». */
  asOf?: string;
}

export interface KpiTileProps {
  label: string;
  value: number | null;
  format?: KpiFormat;
  currency?: string;
  /** Unit after the value for `number` KPIs, e.g. «ημ.». */
  unit?: string;
  /** Preformatted abbreviated value (overrides the built-in el-GR/en-GB formatter). */
  displayValue?: string;
  /** Preformatted full value for the tooltip and accessible name. */
  fullValue?: string;
  delta?: KpiDelta;
  /** Per-KPI favourability: colour comes from this, never from the sign alone. */
  higherIsBetter?: boolean;
  trend?: number[];
  definition?: KpiDefinition;
  /** «Ενημέρωση 14:32»: a Date (formatted as 24-hour time) or preformatted text. */
  asOf?: Date | string;
  /** Past the freshness SLA: the as-of caption turns warning with a clock. */
  isStale?: boolean;
  /** Period in words for the accessible name, e.g. «Οκτώβριος 2026». */
  period?: string;
  state?: 'ready' | 'loading' | 'error';
  onRetry?: () => void;
  /** Linked tile: the whole tile opens the report. */
  href?: string;
  onPress?: () => void;
  /**
   * Live value changes are announced politely, at most once per 10 s per tile (E.2). Off by default.
   * There is no count-up or ticker: D-FE-05 forbids animating money, and count-up of counts is not allowed
   * on first render, so values always appear final from frame 0.
   */
  announceUpdates?: boolean;
}

const ANNOUNCE_INTERVAL_MS = 10_000;

/** KPI tile (Part 2 §4.27, Part 3 §6.4–6.5). */
export function KpiTile({
  label,
  value,
  format = 'number',
  currency = 'EUR',
  unit,
  displayValue,
  fullValue,
  delta,
  higherIsBetter = true,
  trend,
  definition,
  asOf,
  isStale = false,
  period,
  state = 'ready',
  onRetry,
  href,
  onPress,
  announceUpdates = false,
}: KpiTileProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();

  const formatted = value === null ? null : formatKpiValue(value, format, region, currency);
  const withUnit = (text: string) => (unit ? `${text}\u00A0${unit}` : text);
  const shortText = displayValue ?? (formatted ? withUnit(formatted.short) : null);
  const fullText = fullValue ?? (formatted ? withUnit(formatted.full) : null);

  const deltaTone =
    delta === undefined || delta.value === 0
      ? 'neutral'
      : delta.value > 0 === higherIsBetter
        ? 'success'
        : 'danger';
  const deltaText = delta
    ? t('kpiTile.delta', {
        value: formatDelta(delta.value, delta.unit ?? 'percent', region),
        basis: delta.basis,
      })
    : null;
  const deltaNote =
    deltaTone === 'success'
      ? t('kpiTile.favourableNote')
      : deltaTone === 'danger'
        ? t('kpiTile.adverseNote')
        : '';

  const ready = state === 'ready' && fullText !== null;
  const name = ready
    ? t('kpiTile.name', {
        label,
        value: fullText,
        delta: deltaText ?? '',
        note: deltaNote,
        hasDelta: deltaText ? 'yes' : 'no',
        period: period ?? '',
        hasPeriod: period ? 'yes' : 'no',
      })
    : label;

  // Polite, throttled announcement of live changes (never of the first render).
  const lastAnnounced = useRef(0);
  const previousValue = useRef(value);
  useEffect(() => {
    if (!announceUpdates || previousValue.current === value) return;
    previousValue.current = value;
    const now = Date.now();
    if (now - lastAnnounced.current < ANNOUNCE_INTERVAL_MS) return;
    lastAnnounced.current = now;
    announce(name);
  }, [announceUpdates, value, name]);

  const asOfText =
    asOf === undefined ? null : asOf instanceof Date ? formatTime(asOf, region) : asOf;
  const linked = href !== undefined || onPress !== undefined;
  const tone = trend ? trendTone(trend, higherIsBetter) : 'neutral';

  const trendLabel =
    trend && trend.length > 1
      ? t('kpiTile.trend', {
          from: formatKpiValue(trend[0] ?? 0, format, region, currency).short,
          to: formatKpiValue(trend[trend.length - 1] ?? 0, format, region, currency).short,
        })
      : '';

  return (
    <div
      className={styles.tile}
      role="group"
      aria-label={name}
      data-linked={linked || undefined}
      data-state={state}
      aria-busy={state === 'loading' || undefined}
    >
      <div className={styles.labelRow}>
        <span className={styles.label}>{label}</span>
        {definition ? (
          <DialogTrigger>
            <AriaButton
              className={cx(styles.help)}
              aria-label={t('kpiTile.definitionFor', { label })}
            >
              <Icon icon={CircleHelp} size={14} />
            </AriaButton>
            <Popover
              className={cx(styles.popover)}
              placement="bottom start"
              data-material="popover"
            >
              <Dialog
                className={cx(styles.dialog)}
                aria-label={t('kpiTile.definitionFor', { label })}
              >
                <Heading slot="title" className={cx(styles.popoverTitle)}>
                  {label}
                </Heading>
                <dl className={styles.definition}>
                  <dt>{t('kpiTile.formula')}</dt>
                  <dd>{definition.formula}</dd>
                  <dt>{t('kpiTile.source')}</dt>
                  <dd className={styles.mono}>{definition.source}</dd>
                  {definition.asOf ? (
                    <>
                      <dt>{t('kpiTile.dataAsOf')}</dt>
                      <dd>{definition.asOf}</dd>
                    </>
                  ) : null}
                </dl>
              </Dialog>
            </Popover>
          </DialogTrigger>
        ) : null}
      </div>

      {state === 'loading' ? (
        <div className={styles.loading}>
          <span className="ds-visually-hidden">{t('kpiTile.loading')}</span>
          <SkeletonBlock width="96px" height="28px" shape="block" />
          <SkeletonBlock width="64%" />
        </div>
      ) : state === 'error' || shortText === null ? (
        <div className={styles.error}>
          <span className={styles.value} aria-hidden="true">
            —
          </span>
          <span className={styles.errorText}>
            <Icon icon={CircleAlert} size={14} />
            {t('kpiTile.unavailable')}
          </span>
          {onRetry ? (
            <span className={styles.raised}>
              <Button variant="ghost" size="sm" icon={RotateCcw} onPress={onRetry}>
                {t('kpiTile.retry')}
              </Button>
            </span>
          ) : null}
        </div>
      ) : (
        <>
          <div className={styles.valueRow}>
            {shortText !== fullText ? (
              <Tooltip content={fullText}>
                <Focusable>
                  <span className={cx(styles.value, styles.raised)} tabIndex={0}>
                    <span aria-hidden="true">{shortText}</span>
                    <span className="ds-visually-hidden">{fullText}</span>
                  </span>
                </Focusable>
              </Tooltip>
            ) : (
              <span className={styles.value}>{shortText}</span>
            )}
            {delta && deltaText ? (
              <span className={styles.delta} data-tone={deltaTone}>
                {delta.value === 0 ? null : (
                  <span aria-hidden="true">{delta.value > 0 ? '▲' : '▼'}</span>
                )}
                {deltaText}
                {deltaNote ? <span className="ds-visually-hidden"> {deltaNote}</span> : null}
              </span>
            ) : null}
          </div>
          {trend && trend.length > 1 ? (
            <Sparkline
              values={trend}
              height={32}
              tone={tone}
              label={trendLabel}
              draw={format !== 'money'}
            />
          ) : null}
        </>
      )}

      <div className={styles.footerRow}>
        {asOfText ? (
          <span className={styles.asOf} data-stale={isStale || undefined}>
            {isStale ? <Icon icon={Clock} size={12} /> : null}
            {t('kpiTile.asOf', { time: asOfText })}
            {isStale ? <span className="ds-visually-hidden">{t('kpiTile.staleNote')}</span> : null}
          </span>
        ) : null}
        {linked ? (
          <Link
            className={cx(styles.reportLink)}
            aria-label={t('kpiTile.reportFor', { name })}
            {...defined({ href, onPress })}
          >
            {t('kpiTile.viewReport')}
            <Icon icon={ArrowRight} size={12} />
          </Link>
        ) : null}
      </div>
    </div>
  );
}
