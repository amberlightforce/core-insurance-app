import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';

const states: Record<string, 'draft' | 'open' | 'closed' | undefined> = {
  DRAFT: 'draft',
  OPEN: 'open',
  CLOSED: 'closed',
};

/** Claim (and exposure) status through the single status map, with the open sub-status as its sub-label. */
export function ClaimStatusPill({
  status,
  subStatus,
  entity = 'claim',
}: {
  status: string;
  subStatus?: string | null | undefined;
  entity?: 'claim' | 'exposure';
}) {
  const { t } = useTranslation('claims');
  const state = states[status];
  const subLabel = subStatus ? t(`subStatus.${subStatus}`) : undefined;
  return state ? (
    <StatusPill
      entity={entity}
      state={state}
      announceChanges={false}
      {...(subLabel ? { subLabel } : {})}
    />
  ) : (
    <StatusPill semantic="info" subLabel={status} announceChanges={false} />
  );
}
