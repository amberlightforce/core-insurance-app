import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../../design-system';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const kindSemantic: Record<string, Semantic> = {
  REJECTED_BY_CHANNEL: 'error',
  PENDING_AT_CHANNEL: 'pending-approval',
  MARK_MISSING: 'warning',
  CORRELATION_BROKEN: 'warning',
};

export function BreakKindPill({ kind }: { kind: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill
      semantic={kindSemantic[kind] ?? 'info'}
      text={t(`fiscal.breakKind.${kind}`, { defaultValue: kind })}
    />
  );
}

export function BreakStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill
      semantic={status === 'RESOLVED' ? 'success' : 'warning'}
      text={t(`fiscal.breakStatus.${status}`, { defaultValue: status })}
    />
  );
}
