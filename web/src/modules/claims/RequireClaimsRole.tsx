import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../design-system';
import { readSession } from '../../dev-auth/devAuth';
import styles from '../staff/staff.module.css';

const claimsRoles = ['Staff.ClaimsHandler', 'Staff.ClaimsManager'];

/**
 * Route guard of the claims screens: holders of a claims role see the screen, everyone else the no-permission state
 * (the API enforces its own permissions on every call; this only avoids showing forms that cannot work).
 */
export function RequireClaimsRole() {
  const { t } = useTranslation('staff');
  const roles = readSession()?.user.roles ?? [];
  if (roles.some((role) => claimsRoles.includes(role))) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
