import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';
import type { ContractStatus } from './api';

/** The treaty status through the single status map; the word is the module's own (`text`), never a code. */
export function ContractStatusPill({ status }: { status: ContractStatus }) {
  const { t } = useTranslation('reinsurance');
  const text = t(`status.${status}`);
  switch (status) {
    case 'DRAFT':
      return <StatusPill entity="claim" state="draft" text={text} announceChanges={false} />;
    case 'PENDING_APPROVAL':
      return <StatusPill semantic="pending-approval" text={text} announceChanges={false} />;
    case 'APPROVED':
      return <StatusPill semantic="info" text={text} announceChanges={false} />;
    case 'ACTIVE':
      return <StatusPill semantic="success" text={text} announceChanges={false} />;
    case 'EXPIRED':
    case 'CLOSED':
      return <StatusPill entity="claim" state="closed" text={text} announceChanges={false} />;
  }
}
