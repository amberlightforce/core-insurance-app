import { useTranslation } from 'react-i18next';

import { StatusPill, type EntityState } from '../../../design-system';
import { useParty } from '../../party/api';
import { LinkButton } from '../../staff/LinkButton';
import { useRecentLabels } from '../../staff/useRecentLabels';
import type { ReceivableStatus } from './api';

type InvoiceState = EntityState<'invoice'>;

/** A receivable reads through the invoice entry of the status map (open is billed, cancelled is reversed). */
const stateMap: Record<ReceivableStatus, InvoiceState> = {
  OPEN: 'billed',
  PARTIALLY_PAID: 'partiallyPaid',
  PAID: 'paid',
  CANCELLED: 'reversed',
};

export function ReceivableStatusPill({ status }: { status: ReceivableStatus }) {
  return <StatusPill entity="invoice" state={stateMap[status]} announceChanges={false} />;
}

/** Counterparty display name from the party read (personal values come masked from pty.Party.get). */
export function CounterpartyName({ partyId }: { partyId: string }) {
  const party = useParty(partyId);
  if (!party.data) return <span>{partyId.slice(0, 8)}</span>;
  const p = party.data.party;
  const name = p.names.find((n) => n.form === 'NATIVE') ?? p.names[0];
  const text = name
    ? (name.organisationName ??
      ([name.givenNames, name.familyName].filter(Boolean).join(' ') || p.partyNumber))
    : p.partyNumber;
  return <span>{text}</span>;
}

/** Link to the claim file labelled with the claim number (short id until it is loaded). */
export function ClaimLink({ claimId }: { claimId: string | null | undefined }) {
  const { t } = useTranslation('billing');
  const labels = useRecentLabels('claim', claimId ? [claimId] : []);
  if (!claimId) return <span>{t('receivables.none')}</span>;
  return (
    <LinkButton to={`/claims/${claimId}`}>{labels[0]?.label ?? claimId.slice(0, 8)}</LinkButton>
  );
}
