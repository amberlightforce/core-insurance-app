import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { durations } from '../../tokens';
import { cx } from '../../utils/cx';
import styles from './Spinner.module.css';

export type SpinnerSize = 12 | 14 | 16 | 20 | 24;

export interface SpinnerProps {
  size?: SpinnerSize;
  /** Accessible label; defaults to «Φόρτωση…». Pass `null` when a parent already announces the busy state. */
  label?: string | null;
  /**
   * Spinners appear only after 400 ms so fast work never flashes (Part 2 §4.24). Set `false` when the
   * caller already waited (for example a button that is loading).
   */
  delayed?: boolean;
}

/** A 2 px 270° arc in currentColor, 800 ms per turn (1,200 ms under reduced motion). */
export function Spinner({ size = 16, label, delayed = true }: SpinnerProps) {
  const { t } = useTranslation('ds');
  const [visible, setVisible] = useState(!delayed);

  useEffect(() => {
    if (!delayed) return;
    const id = setTimeout(() => {
      setVisible(true);
    }, durations.delaySpinner);
    return () => {
      clearTimeout(id);
    };
  }, [delayed]);

  if (!visible) return null;

  const name = label === null ? undefined : (label ?? t('spinner.label'));
  return (
    <svg
      className={cx(styles.spinner, 'ds-spin')}
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      {...(name ? { role: 'img', 'aria-label': name } : { 'aria-hidden': true })}
    >
      <circle className={styles.track} cx="12" cy="12" r="10" strokeWidth="2.5" />
      <path
        className={styles.arc}
        d="M12 2a10 10 0 1 1-10 10"
        strokeWidth="2.5"
        strokeLinecap="round"
      />
    </svg>
  );
}
