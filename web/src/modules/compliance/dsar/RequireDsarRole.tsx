import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../../design-system';
import styles from '../../staff/staff.module.css';
import { useDsarCaller } from './access';

/** Route guard of the DSAR console: compliance officers see it, everyone else the no-permission state. */
export function RequireDsarRole() {
  const { t } = useTranslation('staff');
  if (useDsarCaller().canWork) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
