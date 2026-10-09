import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button } from '../../../design-system';
import { readSession } from '../../../dev-auth/devAuth';
import { actionAvailability, hasServicingRole, type ServicingAction } from './model';
import styles from './PolicyFile.module.css';

const actions: {
  id: ServicingAction;
  path: string;
  variant: 'secondary' | 'danger' | 'primary';
}[] = [
  { id: 'change', path: 'change', variant: 'secondary' },
  { id: 'cancel', path: 'cancel', variant: 'secondary' },
  { id: 'renew', path: 'renew', variant: 'primary' },
];

/**
 * Change / Cancel / Renew now. Hidden without the servicing permission; otherwise a button that opens the
 * servicing screen when the term state allows it, or stays focusable with the reason when it does not. The
 * policy id is an opaque id; no personal data goes into the route (PITFALLS 18).
 */
export function ActionBar({
  policyId,
  termState,
  renewalExists,
}: {
  policyId: string;
  termState: string | undefined;
  renewalExists: boolean;
}) {
  const { t } = useTranslation('policy');
  const navigate = useNavigate();
  const permitted = hasServicingRole(readSession()?.user.roles ?? []);
  const visible = actions
    .map((a) => ({
      ...a,
      availability: actionAvailability(a.id, { permitted, termState, renewalExists }),
    }))
    .filter((a) => a.availability.status !== 'hidden');
  if (visible.length === 0) return null;
  return (
    <div className={styles.actionBar} role="group" aria-label={t('file.actions.label')}>
      {visible.map((a) => (
        <Button
          key={a.id}
          variant={a.variant}
          {...(a.availability.status === 'disabled'
            ? { disabledReason: t(`file.actions.reason.${a.availability.reason}`) }
            : {})}
          onPress={() => {
            void navigate(`/policies/${encodeURIComponent(policyId)}/${a.path}`);
          }}
        >
          {t(`file.actions.${a.id}`)}
        </Button>
      ))}
    </div>
  );
}
