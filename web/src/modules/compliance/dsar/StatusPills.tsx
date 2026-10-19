import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../../design-system';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const caseSemantic: Record<string, Semantic> = {
  RECEIVED: 'disabled',
  IDENTITY_VERIFIED: 'info',
  IN_REVIEW: 'pending-approval',
  COMPLETED: 'success',
  REFUSED: 'error',
};

const taskSemantic: Record<string, Semantic> = {
  PENDING: 'info',
  DONE: 'success',
  FAILED: 'error',
  NOT_BUILT: 'warning',
};

const kindSemantic: Record<string, Semantic> = {
  ERASED: 'success',
  ANONYMISED: 'success',
  RESTRICTED: 'info',
  NOT_HELD: 'disabled',
  NOT_BUILT: 'warning',
};

export function CaseStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill semantic={caseSemantic[status] ?? 'disabled'} text={t(`dsar.status.${status}`)} />
  );
}

export function TaskStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill semantic={taskSemantic[status] ?? 'disabled'} text={t(`dsar.task.${status}`)} />
  );
}

export function OutcomeKindPill({ kind }: { kind: string }) {
  const { t } = useTranslation('compliance');
  return <StatusPill semantic={kindSemantic[kind] ?? 'disabled'} text={t(`dsar.kind.${kind}`)} />;
}
