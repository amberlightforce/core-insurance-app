import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../../design-system';
import styles from '../../staff/staff.module.css';
import { useCanWorkWithdrawals } from './api';

/** Guard of the staff withdrawal screens (the API enforces chn.Withdrawal.list / .close on every call). */
export function RequireWithdrawalRole() {
  const { t } = useTranslation('staff');
  if (useCanWorkWithdrawals()) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
