import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../design-system';
import styles from '../staff/staff.module.css';
import { useMigrationCaller } from './access';

/** Route guard of the migration screens: migration managers see them, everyone else the no-permission state. */
export function RequireMigrationRole() {
  const { t } = useTranslation('staff');
  if (useMigrationCaller().canWork) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
