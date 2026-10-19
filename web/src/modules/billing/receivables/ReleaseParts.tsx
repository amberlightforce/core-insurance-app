import { StatusPill, type EntityState } from '../../../design-system';
import type { DisbursementView } from './api';

type DisbursementState = EntityState<'disbursement'>;

const stateMap: Record<DisbursementView['status'], DisbursementState> = {
  Requested: 'requested',
  PendingApproval: 'pendingApproval',
  Approved: 'approved',
  Released: 'released',
  Issued: 'issued',
  Cleared: 'cleared',
  Rejected: 'rejected',
  Stopped: 'stopped',
  Voided: 'voided',
  Returned: 'returned',
};

export function DisbursementStatusPill({ status }: { status: DisbursementView['status'] }) {
  return <StatusPill entity="disbursement" state={stateMap[status]} announceChanges={false} />;
}
