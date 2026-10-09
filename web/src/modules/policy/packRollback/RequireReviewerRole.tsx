import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router';

import { Banner } from '../../../design-system';
import styles from '../../staff/staff.module.css';
import { useCanReview } from './api';

/** Route guard of the exception queue: reviewers (and Platform.Admin) see it, everyone else the no-permission state. */
export function RequireReviewerRole() {
  const { t } = useTranslation('staff');
  if (useCanReview()) return <Outlet />;
  return (
    <div className={styles.page}>
      <Banner variant="warning" live="none" title={t('noPermission.title')}>
        {t('noPermission.body')}
      </Banner>
    </div>
  );
}
