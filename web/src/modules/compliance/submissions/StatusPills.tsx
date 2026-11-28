import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../../design-system';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const statusSemantic: Record<string, Semantic> = {
  PLANNED: 'disabled',
  IN_PREPARATION: 'info',
  READY: 'pending-approval',
  SUBMITTED: 'info',
  ACKNOWLEDGED: 'success',
  REJECTED: 'error',
  RESUBMITTED: 'disabled',
};

const approvalSemantic: Record<string, Semantic> = {
  NONE: 'disabled',
  PENDING: 'pending-approval',
  APPROVED: 'success',
};

export function SubmissionStatusPill({ status }: { status: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill
      semantic={statusSemantic[status] ?? 'disabled'}
      text={t(`submissions.status.${status}`, { defaultValue: status })}
    />
  );
}

export function ApprovalPill({ status }: { status: string }) {
  const { t } = useTranslation('compliance');
  return (
    <StatusPill
      semantic={approvalSemantic[status] ?? 'disabled'}
      text={t(`submissions.approval.${status}`, { defaultValue: status })}
    />
  );
}

export function OverduePill() {
  const { t } = useTranslation('compliance');
  return <StatusPill semantic="error" text={t('submissions.overdue')} />;
}
