import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../design-system';
import styles from '../staff/staff.module.css';
import { currentUser, reinsuranceRoles } from './roles';

/**
 * Route guard of the reinsurance screens: holders of a reinsurance role see them, everyone else the no-permission
 * state (the API enforces its own permissions on every call; this only avoids screens that cannot work).
 */
export function RequireReinsuranceRole() {
  const { t } = useTranslation('staff');
  const { roles } = currentUser();
  if (roles.some((role) => reinsuranceRoles.some((allowed) => allowed === role))) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
