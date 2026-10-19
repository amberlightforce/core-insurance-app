import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../../design-system';
import styles from '../../staff/staff.module.css';
import { useCaller } from './access';

/** Route guard of the pack screens: holders of a pack role see them, everyone else the no-permission state. */
export function RequirePackRole() {
  const { t } = useTranslation('staff');
  const caller = useCaller();
  if (caller.canView) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
