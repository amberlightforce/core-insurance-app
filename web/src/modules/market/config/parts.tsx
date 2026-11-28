import { useTranslation } from 'react-i18next';

import type { ConfigChangeStatus } from '../../../api/types';
import { StatusPill } from '../../../design-system';

/** The request status, always with the record's own translated text. */
export function ConfigStatusPill({ status }: { status: ConfigChangeStatus }) {
  const { t } = useTranslation('market');
  const text = t(`config.status.${status}`);
  switch (status) {
    case 'PENDING_APPROVAL':
      return <StatusPill semantic="pending-approval" text={text} announceChanges={false} />;
    case 'APPLIED':
      return <StatusPill semantic="success" text={text} announceChanges={false} />;
    case 'REJECTED':
      return <StatusPill semantic="error" text={text} announceChanges={false} />;
    case 'SCHEDULED':
      return <StatusPill semantic="info" text={text} announceChanges={false} />;
    default:
      return <StatusPill semantic="read-only" text={text} announceChanges={false} />;
  }
}

/** Legal status of a value (D-REG-01): anything but Settled / NotRegulatory is shown as a warning. */
export function LegalStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('market');
  const text = t(`config.legal.${status}`, { defaultValue: status });
  const ok = status === 'Settled' || status === 'NotRegulatory';
  return (
    <StatusPill
      semantic={ok ? 'success' : 'warning'}
      text={text}
      size="sm"
      announceChanges={false}
    />
  );
}
