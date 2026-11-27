import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';

/** Version status as a pill; unknown states fall back to an info pill with the raw code. */
export function VersionStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('products');
  const semantic =
    status === 'LOCKED' ? 'success' : status === 'DRAFT' ? 'pending-approval' : 'info';
  return (
    <StatusPill
      semantic={semantic}
      text={t(`status.${status}`, { defaultValue: status })}
      announceChanges={false}
    />
  );
}
