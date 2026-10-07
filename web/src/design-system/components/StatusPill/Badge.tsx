import { useTranslation } from 'react-i18next';

import { formatInteger } from '../../../format/numbers';
import { useRegionFormat } from '../../preferences/context';
import styles from './StatusPill.module.css';

export type BadgeTone = 'neutral' | 'attention';

export interface BadgeProps {
  count: number;
  /** `neutral` (counts in tabs and work views) or `attention` (danger fill, white; unread, errors). */
  tone?: BadgeTone;
  /** Values above `max` show «99+». */
  max?: number;
  /**
   * Accessible text that replaces the bare number, e.g. «3 μη αναγνωσμένες ειδοποιήσεις». Without it the
   * number is read as is (enough inside a labelled tab or button).
   */
  label?: string;
}

/** Count badge (Part 2 §4.13 `count`): min 18×18, radius.full, caption 600, tabular numerals. */
export function Badge({ count, tone = 'neutral', max = 99, label }: BadgeProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const capped = count > max;
  const text = capped
    ? t('badge.overflow', { max: formatInteger(max, region) })
    : formatInteger(count, region);
  return (
    <span className={styles.badge} data-tone={tone}>
      <span aria-hidden={label !== undefined || capped ? true : undefined}>{text}</span>
      {label !== undefined ? (
        <span className="ds-visually-hidden">{label}</span>
      ) : capped ? (
        <span className="ds-visually-hidden">
          {t('badge.overflowLong', { max: formatInteger(max, region) })}
        </span>
      ) : null}
    </span>
  );
}
