import { useTranslation } from 'react-i18next';

import { StatusPill, type SemanticStatus } from '../../../design-system';
import type { ManualJournalStatus } from './types';

const semantic: Record<ManualJournalStatus, SemanticStatus> = {
  DRAFT: 'info',
  SUBMITTED: 'pending-approval',
  APPROVED: 'success',
  POSTED: 'success',
  REJECTED: 'error',
  WITHDRAWN: 'read-only',
};

/** Status of a manual journal; the label is the finance wording, the colour and icon come from the one status map. */
export function ManualJournalStatusPill({ status }: { status: ManualJournalStatus }) {
  const { t } = useTranslation('finance');
  return (
    <StatusPill
      semantic={semantic[status]}
      text={t(`ops.manual.status.${status}`)}
      announceChanges={false}
    />
  );
}
