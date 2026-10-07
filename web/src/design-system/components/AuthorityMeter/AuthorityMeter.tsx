import { CircleAlert, TriangleAlert } from 'lucide-react';
import type { CSSProperties } from 'react';
import { useMeter } from 'react-aria';
import { useTranslation } from 'react-i18next';

import { formatMoney, formatNumber, formatPercent } from '../../../format/numbers';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { authorityStatus, toNumber } from './authority';
import styles from './AuthorityMeter.module.css';

export type AuthorityMeterFormat = 'money' | 'percent' | 'number';

export interface AuthorityMeterProps {
  /** Visible label, e.g. «Όριο εξουσιοδότησης» or «Όριο σας ±10 %». */
  label: string;
  /** Current amount (exact decimal string for money) or percentage. Use the absolute value for deviations. */
  value: number | string;
  /** The user's limit (PLT `authority.check`). */
  limit: number | string;
  /** How value and limit are written: money «6.480,00 €», percent «7,50 %», or a plain number. */
  format?: AuthorityMeterFormat;
  /** End of the scale; defaults to 125 % of the limit so overruns stay visible. */
  max?: number | string;
  /** 4 px inline (fields) or 6 px decision bar (approval workbench). */
  size?: 'inline' | 'decision';
  /** Hide the value line (the label and accessible value stay). */
  hideValue?: boolean;
  className?: string;
}

function formatFigure(
  value: number | string,
  format: AuthorityMeterFormat,
  region: 'el-GR' | 'en-GB',
) {
  switch (format) {
    case 'money':
      return formatMoney(value, { region });
    case 'percent':
      return formatPercent(value, { region });
    default:
      return formatNumber(value, { region });
  }
}

/**
 * Authority meter (Part 2 §4.6 deviation, §4.24, v3 decision bar): React Aria `useMeter` (the hook behind RAC
 * `Meter`, used directly so the element carries the plain `meter` role rather than RAC's `meter progressbar`
 * fallback list, which axe rejects); 4 px (inline) or 6 px
 * (decision bar) track, fill success within the limit, warning at 80–100 %, danger beyond, with a 2 px limit
 * tick. The accessible value is «6.480,00 € από 60.000,00 € όριο». Figures never animate; only the fill may
 * move (transform scaleX).
 */
export function AuthorityMeter({
  label,
  value,
  limit,
  format = 'money',
  max,
  size = 'inline',
  hideValue = false,
  className,
}: AuthorityMeterProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const numericValue = Math.abs(toNumber(value));
  const numericLimit = toNumber(limit);
  const scaleMax = max === undefined ? numericLimit * 1.25 : toNumber(max);
  const status = authorityStatus(numericValue, numericLimit);
  const magnitude = typeof value === 'string' ? value.replace(/^[-\u2212]/, '') : Math.abs(value);
  const valueText = t('authorityMeter.valueText', {
    value: formatFigure(magnitude, format, region),
    limit: formatFigure(limit, format, region),
  });
  const fill = scaleMax > 0 ? Math.min(numericValue / scaleMax, 1) : 0;
  const tick = scaleMax > 0 ? Math.min(numericLimit / scaleMax, 1) : 1;
  const style = { '--_fill': String(fill), '--_tick': `${String(tick * 100)}%` } as CSSProperties;

  const { meterProps, labelProps } = useMeter({
    label,
    value: numericValue,
    minValue: 0,
    maxValue: scaleMax > 0 ? scaleMax : 1,
    valueLabel:
      status === 'danger'
        ? t('authorityMeter.valueTextOver', {
            value: formatFigure(magnitude, format, region),
            limit: formatFigure(limit, format, region),
          })
        : valueText,
  });

  return (
    <div
      {...meterProps}
      role="meter"
      className={cx(styles.meter, className)}
      data-status={status}
      data-size={size}
      style={style}
    >
      <div className={styles.header}>
        <span {...labelProps} className={styles.label}>
          {label}
        </span>
        {hideValue ? null : (
          <span className={styles.value} aria-hidden="true">
            {status === 'danger' ? <Icon icon={CircleAlert} size={14} /> : null}
            {status === 'warning' ? <Icon icon={TriangleAlert} size={14} /> : null}
            <span>{valueText}</span>
          </span>
        )}
      </div>
      <div className={styles.track} aria-hidden="true">
        <div className={styles.fill} />
        <div className={styles.tick} />
      </div>
      {status === 'danger' ? (
        <span className={styles.over} aria-hidden="true">
          {t('authorityMeter.overLimit')}
        </span>
      ) : null}
    </div>
  );
}
