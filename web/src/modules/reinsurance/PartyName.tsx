import { Link } from 'react-aria-components';

import { cx } from '../../design-system/utils/cx';
import { useParty } from '../party/api';
import styles from './Reinsurance.module.css';

/**
 * A reinsurer or broker as its organisation name, linked to the party file. The name is read from the party service
 * for display only and is never written to browser storage (PITFALLS 19); until it arrives, or when the read is
 * refused, the party number or the id stands in so a row never shows nothing.
 */
export function PartyName({ partyId }: { partyId: string }) {
  const party = useParty(partyId);
  const view = party.data?.party;
  const native = view?.names.find((n) => n.form === 'NATIVE') ?? view?.names[0];
  const name = native
    ? (native.organisationName ?? [native.familyName, native.givenNames].filter(Boolean).join(' '))
    : null;
  return (
    <Link href={`/parties/${partyId}`} className={cx(styles.wrapId)}>
      {name || view?.partyNumber || partyId}
    </Link>
  );
}
