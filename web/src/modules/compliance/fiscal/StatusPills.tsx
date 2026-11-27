import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../../design-system';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const semantic: Record<string, Semantic> = {
  PENDING: 'pending-approval',
  REGISTERED: 'success',
  REJECTED: 'error',
};

export function FiscalStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill
      semantic={semantic[status] ?? 'disabled'}
      text={t(`fiscal.status.${status}`, { defaultValue: status })}
    />
  );
}
