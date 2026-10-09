import { Copy } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import type { PackRollbackExceptionView } from '../../../api/types';
import { Button, StatusPill, announce } from '../../../design-system';
import styles from './PackRollback.module.css';

/** A configuration hash in mono with a copy button: `short` for list rows, full (wrapping) for the record. */
export function HashText({ value, short = false }: { value: string; short?: boolean }) {
  const { t } = useTranslation('policy');
  const shown = short ? `${value.slice(0, 12)}…` : value;
  return (
    <span className={styles.hash}>
      <span className="ds-mono" {...(short ? { title: value } : {})} data-testid="hash">
        {shown}
      </span>
      <Button
        variant="ghost"
        size="sm"
        icon={Copy}
        label={t('packRollback.copyHash')}
        onPress={() => {
          void navigator.clipboard
            ?.writeText(value)
            .then(() => {
              announce(t('packRollback.copied'));
            })
            .catch(() => {
              // Clipboard blocked: the hash stays visible and selectable.
            });
        }}
      />
    </span>
  );
}

export function ExceptionStatusPill({ status }: { status: PackRollbackExceptionView['status'] }) {
  const { t } = useTranslation('policy');
  return status === 'OPEN' ? (
    <StatusPill semantic="warning" text={t('packRollback.status.OPEN')} announceChanges={false} />
  ) : (
    <StatusPill
      semantic="success"
      text={t('packRollback.status.REVIEWED')}
      announceChanges={false}
    />
  );
}
