import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../../design-system';
import styles from '../../staff/staff.module.css';
import { useCanAdministerAi } from './access';

/** Route guard of the AI control screens: AI administrators see them, everyone else the no-permission state. */
export function RequireAiRole() {
  const { t } = useTranslation('staff');
  if (useCanAdministerAi()) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
