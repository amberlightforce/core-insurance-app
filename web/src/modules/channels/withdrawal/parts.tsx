import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../../design-system';
import type { WithdrawalStatus } from './api';

const semantics: Record<WithdrawalStatus, 'info' | 'warning' | 'success' | 'error'> = {
  DECLARED: 'warning',
  CONFIRMED: 'info',
  SUBMITTED: 'info',
  PROCESSED: 'success',
  REJECTED: 'error',
  DECLINED: 'error',
};

export function WithdrawalStatusPill({ status }: { status: WithdrawalStatus }) {
  const { t } = useTranslation('channels');
  return (
    <StatusPill
      semantic={semantics[status]}
      text={t(`withdrawal.status.${status}`)}
      announceChanges={false}
    />
  );
}
