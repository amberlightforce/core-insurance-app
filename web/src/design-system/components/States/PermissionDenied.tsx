import { Check, Lock } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { Button } from '../Button';
import { Heading, type HeadingLevel } from './Heading';
import { IllLockedDoor } from './illustrations';
import styles from './States.module.css';

export interface PermissionDeniedProps {
  /** What is restricted, as a full sentence: «Δεν έχετε πρόσβαση στις αναφορές Solvency II.» */
  restriction: string;
  /** Who can grant access: «ο υπεύθυνος ρόλων της Οικονομικής Διεύθυνσης». */
  grantor?: string;
  /** Creates an access request to the role owner. Resolve to show the receipt; reject to allow a retry. */
  onRequestAccess?: () => Promise<void> | void;
  /** The request already exists (e.g. after a reload). */
  isRequested?: boolean;
  headingLevel?: HeadingLevel;
}

/** Permission denied for a whole screen (DESIGN-B A.11): lock illustration, what, who, «Αίτημα πρόσβασης». */
export function PermissionDenied({
  restriction,
  grantor,
  onRequestAccess,
  isRequested = false,
  headingLevel = 1,
}: PermissionDeniedProps) {
  const { t } = useTranslation('ds');
  const [status, setStatus] = useState<'idle' | 'sending' | 'sent'>(isRequested ? 'sent' : 'idle');
  const sent = status === 'sent' || isRequested;

  const request = async () => {
    if (!onRequestAccess) return;
    setStatus('sending');
    try {
      await onRequestAccess();
      setStatus('sent');
      announce(t('permissionDenied.requested'));
    } catch {
      setStatus('idle');
    }
  };

  return (
    <div className={styles.page}>
      <IllLockedDoor size="md" />
      <Heading level={headingLevel} className={styles.pageTitle}>
        {t('permissionDenied.title')}
      </Heading>
      <p className={styles.description}>{restriction}</p>
      {grantor ? (
        <p className={styles.description}>{t('permissionDenied.grantor', { grantor })}</p>
      ) : null}
      {onRequestAccess ? (
        sent ? (
          <span className={styles.receipt} role="status">
            <Icon icon={Check} size={16} />
            {t('permissionDenied.requested')}
          </span>
        ) : (
          <Button
            variant="primary"
            icon={Lock}
            isLoading={status === 'sending'}
            onPress={() => {
              void request();
            }}
          >
            {t('permissionDenied.request')}
          </Button>
        )
      ) : null}
    </div>
  );
}
