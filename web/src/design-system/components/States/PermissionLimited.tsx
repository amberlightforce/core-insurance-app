import { Lock } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import styles from './States.module.css';

export interface PermissionLimitedProps {
  /** The masked form, e.g. «•••• 4471». Restricted values are masked or omitted, never blank. */
  maskedValue?: string;
  /** Why it is masked; defaults to «Απαιτείται δικαίωμα προβολής ευαίσθητων δεδομένων». */
  reason?: string;
}

/**
 * Permission-limited caption (DESIGN-B A.11): «•••• 4471 · Απαιτείται δικαίωμα προβολής ευαίσθητων δεδομένων».
 * The masked value is announced as «κρυφό» (E.2).
 */
export function PermissionLimited({ maskedValue, reason }: PermissionLimitedProps) {
  const { t } = useTranslation('ds');
  return (
    <span className={styles.limited}>
      {maskedValue ? (
        <>
          <span className={styles.masked} aria-hidden="true">
            {maskedValue}
          </span>
          <span className="ds-visually-hidden">{t('permissionLimited.hidden')}</span>
        </>
      ) : null}
      <span className={styles.limitedCaption}>
        <Icon icon={Lock} size={12} />
        {reason ?? t('permissionLimited.caption')}
      </span>
    </span>
  );
}
