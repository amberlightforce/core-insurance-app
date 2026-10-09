import { Copy } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import type { PackActivationStatus } from '../../../api/types';
import { Button, StatusPill, announce } from '../../../design-system';
import styles from './Packs.module.css';

/** A content hash in mono, shown in full (wrapping, never clipped) with a copy button. */
export function HashText({ value, label }: { value: string; label: string }) {
  const { t } = useTranslation('market');
  return (
    <span className={styles.hash}>
      <span className="ds-mono" data-testid="hash">
        {value}
      </span>
      <Button
        variant="ghost"
        size="sm"
        icon={Copy}
        label={t('packs.copyHash', { label })}
        onPress={() => {
          void navigator.clipboard
            ?.writeText(value)
            .then(() => {
              announce(t('packs.copied'));
            })
            .catch(() => {
              // Clipboard blocked (permissions, insecure context): the hash stays selectable on screen.
            });
        }}
      />
    </span>
  );
}

/** The activation request status, always with the record's own translated text (no semantic prefix). */
export function ActivationStatusPill({ status }: { status: PackActivationStatus }) {
  const { t } = useTranslation('market');
  const text = t(`packs.activationStatus.${status}`);
  switch (status) {
    case 'PENDING_APPROVAL':
    case 'REQUESTED':
      return <StatusPill semantic="pending-approval" text={text} announceChanges={false} />;
    case 'ACTIVE':
      return <StatusPill semantic="success" text={text} announceChanges={false} />;
    case 'REJECTED':
      return <StatusPill semantic="error" text={text} announceChanges={false} />;
    case 'SCHEDULED':
      return <StatusPill semantic="info" text={text} announceChanges={false} />;
    default:
      return <StatusPill semantic="read-only" text={text} announceChanges={false} />;
  }
}
