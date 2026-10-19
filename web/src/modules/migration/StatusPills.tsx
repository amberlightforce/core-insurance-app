import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';
import type { BatchStatus, OutcomeCode } from './api';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const batchSemantic: Record<BatchStatus, Semantic> = {
  DRAFT: 'disabled',
  PENDING_APPROVAL: 'pending-approval',
  REJECTED: 'error',
  RUNNING: 'info',
  COMPLETED: 'success',
  COMPLETED_WITH_ERRORS: 'warning',
  PAUSED: 'warning',
  REVERSED: 'disabled',
  REVERSAL_BLOCKED: 'error',
  NOT_REVERSIBLE: 'warning',
};

const outcomeSemantic: Record<OutcomeCode, Semantic> = {
  PENDING: 'disabled',
  IMPORTED: 'success',
  SKIPPED_EXISTING: 'info',
  FAILED: 'error',
  REVERSED: 'disabled',
  REVERSAL_BLOCKED: 'error',
};

export function BatchStatusPill({ status }: { status: BatchStatus }) {
  const { t } = useTranslation('migration');
  return <StatusPill semantic={batchSemantic[status]} text={t(`status.${status}`)} />;
}

export function OutcomePill({ outcome }: { outcome: OutcomeCode }) {
  const { t } = useTranslation('migration');
  return (
    <StatusPill
      semantic={outcomeSemantic[outcome]}
      text={t(`outcome.${outcome}`)}
      announceChanges={false}
    />
  );
}
