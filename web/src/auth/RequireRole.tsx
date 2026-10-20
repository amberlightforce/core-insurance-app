import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../design-system';
import { useDevSession } from '../dev-auth/devAuth';
import styles from '../modules/staff/staff.module.css';
import { hasAnyRole } from './roles';

/**
 * Route guard: holders of one of `roles` see the nested screens, everyone else (including a signed-out visitor) the
 * no-permission state. The API enforces its own permissions on every call; this only avoids screens that cannot work.
 */
export function RequireRole({ roles }: { roles: readonly string[] }) {
  const { t } = useTranslation('staff');
  const userRoles = useDevSession()?.user.roles ?? [];
  if (hasAnyRole(userRoles, roles)) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
