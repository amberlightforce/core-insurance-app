import { StatusPill } from '../../design-system';

const setStates: Record<
  string,
  'draft' | 'submitted' | 'pendingApproval' | 'approved' | 'rejected' | 'posted' | undefined
> = {
  DRAFT: 'draft',
  SUBMITTED: 'submitted',
  PENDING_APPROVAL: 'pendingApproval',
  APPROVED: 'approved',
  REJECTED: 'rejected',
  POSTED: 'posted',
};

/** Transaction-set status through the single status map. */
export function SetStatusPill({ status }: { status: string }) {
  const state = setStates[status];
  return state ? (
    <StatusPill entity="transactionSet" state={state} announceChanges={false} />
  ) : (
    <StatusPill semantic="info" subLabel={status} announceChanges={false} />
  );
}

const paymentStates: Record<
  string,
  | 'pendingApproval'
  | 'approved'
  | 'stopped'
  | 'released'
  | 'issued'
  | 'cleared'
  | 'rejected'
  | undefined
> = {
  PENDING: 'pendingApproval',
  APPROVED: 'approved',
  ON_HOLD: 'stopped',
  SUBMITTED: 'released',
  ISSUED: 'issued',
  CLEARED: 'cleared',
  REJECTED: 'rejected',
};

/** Claim payment status (the disbursement states of the status map). */
export function PaymentStatusPill({ status }: { status: string }) {
  const state = paymentStates[status];
  return state ? (
    <StatusPill entity="disbursement" state={state} announceChanges={false} />
  ) : (
    <StatusPill semantic="info" subLabel={status} announceChanges={false} />
  );
}

/** Approval request status of one checklist item. */
export function ChecklistPill({ status }: { status: 'PENDING' | 'APPROVED' | 'REJECTED' }) {
  return status === 'PENDING' ? (
    <StatusPill entity="transactionSet" state="pendingApproval" announceChanges={false} />
  ) : status === 'APPROVED' ? (
    <StatusPill entity="transactionSet" state="approved" announceChanges={false} />
  ) : (
    <StatusPill entity="transactionSet" state="rejected" announceChanges={false} />
  );
}
