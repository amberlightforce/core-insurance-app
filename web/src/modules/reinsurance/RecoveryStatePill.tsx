import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';

/** Recovery record state (CALCULATED → POSTED → REVERSED) through the status map, in the module's own words. */
export function RecoveryStatePill({
  state,
}: {
  state: 'CALCULATED' | 'POSTED' | 'REVERSED' | null;
}) {
  const { t } = useTranslation('reinsurance');
  if (state === null) return <span>—</span>;
  const text = t(`recoverables.state.${state}`);
  switch (state) {
    case 'CALCULATED':
      return <StatusPill semantic="info" text={text} announceChanges={false} />;
    case 'POSTED':
      return <StatusPill semantic="success" text={text} announceChanges={false} />;
    case 'REVERSED':
      return <StatusPill semantic="warning" text={text} announceChanges={false} />;
  }
}
