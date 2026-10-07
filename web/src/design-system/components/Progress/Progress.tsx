import { useId, type CSSProperties } from 'react';
import { Label, ProgressBar as AriaProgressBar } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { formatPercent } from './formatPercent';
import styles from './Progress.module.css';

export type ProgressSize = 'sm' | 'md' | 'lg';

interface ProgressBaseProps {
  /** Visible label above the bar. Without it pass `aria-label`. */
  label?: string;
  'aria-label'?: string;
  /** Overrides `aria-valuetext`, e.g. «31 από 50 εργασίες». */
  valueText?: string;
  className?: string;
}

export interface ProgressBarProps extends ProgressBaseProps {
  value?: number;
  /** Total; with `showCount` the bar shows «31 από 50». Default 100. */
  maxValue?: number;
  /** Track 4 / 6 / 8 px (`lg` is the IB-12 day progress). */
  size?: ProgressSize;
  /** Shows «62 %» next to the label. Default true. */
  showValue?: boolean;
  /** Shows «{value} από {max}» under the bar. */
  showCount?: boolean;
  /** 2 px bar with a sliding 30 % segment; omits `aria-valuenow`. */
  isIndeterminate?: boolean;
}

function fractionOf(value: number, max: number): number {
  if (max <= 0) return 0;
  return Math.min(1, Math.max(0, value / max));
}

/**
 * Linear progress (Part 2 §4.24) on RAC `ProgressBar`: `gradient.tide` fill scaled with `transform`,
 * success fill and an end-cap check at 100 % (MI-43); indeterminate is a 2 px sliding segment that
 * pulses in place under reduced motion.
 */
export function ProgressBar({
  label,
  'aria-label': ariaLabel,
  value = 0,
  maxValue = 100,
  size = 'md',
  showValue = true,
  showCount = false,
  isIndeterminate = false,
  valueText,
  className,
}: ProgressBarProps) {
  const { t } = useTranslation('ds');
  const locale = useRegionFormat();
  const fraction = fractionOf(value, maxValue);
  const complete = !isIndeterminate && fraction >= 1;
  const count = t('progress.count', { value, max: maxValue });
  const text = valueText ?? (showCount ? count : formatPercent(fraction, locale));
  const fillStyle = { '--_fraction': String(fraction) } as CSSProperties;

  return (
    <AriaProgressBar
      className={cx(styles.linear, className)}
      value={value}
      maxValue={maxValue}
      isIndeterminate={isIndeterminate}
      valueLabel={text}
      data-size={isIndeterminate ? 'indeterminate' : size}
      data-complete={complete || undefined}
      {...(ariaLabel ? { 'aria-label': ariaLabel } : {})}
    >
      {label || (showValue && !isIndeterminate) ? (
        <span className={styles.header}>
          {label ? <Label className={styles.label}>{label}</Label> : <span />}
          {showValue && !isIndeterminate ? (
            <span className={cx(styles.value, 'ds-num')} aria-hidden="true">
              {formatPercent(fraction, locale)}
            </span>
          ) : null}
        </span>
      ) : null}
      <span className={styles.track} aria-hidden="true">
        {isIndeterminate ? (
          <span className={styles.segment} />
        ) : (
          <span className={styles.fill} style={fillStyle} />
        )}
        {complete ? (
          <svg className={styles.check} viewBox="0 0 12 12" width={12} height={12}>
            <circle cx="6" cy="6" r="6" className={styles.checkDisc} />
            <path d="M3.5 6.2 5.2 7.8 8.5 4.4" className={styles.checkMark} pathLength={1} />
          </svg>
        ) : null}
      </span>
      {showCount && !isIndeterminate ? (
        <span className={cx(styles.count, 'ds-num')} aria-hidden="true">
          {count}
        </span>
      ) : null}
    </AriaProgressBar>
  );
}

export type ProgressRingSize = 16 | 24 | 40 | 64;

export interface ProgressRingProps extends ProgressBaseProps {
  value: number;
  maxValue?: number;
  size?: ProgressRingSize;
}

const ringStroke: Record<ProgressRingSize, number> = { 16: 2, 24: 2.5, 40: 3.5, 64: 5 };

/**
 * Circular / step ring (Part 2 §4.24): 16 / 24 / 40 / 64 px, stroke 2 / 2.5 / 3.5 / 5, an SVG
 * `gradient.tide` arc; the value shows in the centre at 40 and 64 px.
 */
export function ProgressRing({
  label,
  'aria-label': ariaLabel,
  value,
  maxValue = 100,
  size = 24,
  valueText,
  className,
}: ProgressRingProps) {
  const locale = useRegionFormat();
  const gradientId = useId();
  const fraction = fractionOf(value, maxValue);
  const stroke = ringStroke[size];
  const radius = (size - stroke) / 2;
  const circumference = 2 * Math.PI * radius;
  const percent = formatPercent(fraction, locale);
  const showCentre = size >= 40;

  return (
    <AriaProgressBar
      className={cx(styles.ring, className)}
      value={value}
      maxValue={maxValue}
      valueLabel={valueText ?? percent}
      data-ring-size={size}
      data-complete={fraction >= 1 || undefined}
      {...(ariaLabel ? { 'aria-label': ariaLabel } : {})}
      {...(label && !ariaLabel ? { 'aria-label': label } : {})}
    >
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${String(size)} ${String(size)}`}
        aria-hidden="true"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="0">
            <stop offset="0%" className={styles.stopStart} />
            <stop offset="100%" className={styles.stopEnd} />
          </linearGradient>
        </defs>
        <circle
          className={styles.ringTrack}
          cx={size / 2}
          cy={size / 2}
          r={radius}
          strokeWidth={stroke}
          fill="none"
        />
        <circle
          className={styles.ringArc}
          cx={size / 2}
          cy={size / 2}
          r={radius}
          strokeWidth={stroke}
          fill="none"
          stroke={`url(#${gradientId})`}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={circumference * (1 - fraction)}
          transform={`rotate(-90 ${String(size / 2)} ${String(size / 2)})`}
        />
      </svg>
      {showCentre ? (
        <span className={cx(styles.ringValue, 'ds-num')} aria-hidden="true">
          {percent}
        </span>
      ) : null}
    </AriaProgressBar>
  );
}
