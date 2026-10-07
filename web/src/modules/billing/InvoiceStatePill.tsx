import { StatusPill, type EntityState } from '../../design-system';

type InvoiceState = EntityState<'invoice'>;

const states: Record<string, InvoiceState> = {
  PLANNED: 'planned',
  BILLED: 'billed',
  DUE: 'due',
  PAID: 'paid',
  PARTIALLY_PAID: 'partiallyPaid',
  OVERDUE: 'overdue',
  WRITTEN_OFF: 'writtenOff',
  REVERSED: 'reversed',
};

/** The contract's invoice state through the single status map. */
export function InvoiceStatePill({ state }: { state: string }) {
  const mapped = states[state];
  return mapped ? (
    <StatusPill entity="invoice" state={mapped} announceChanges={false} />
  ) : (
    <StatusPill semantic="info" subLabel={state} announceChanges={false} />
  );
}
