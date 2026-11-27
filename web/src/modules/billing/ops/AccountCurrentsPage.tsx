import { useTranslation } from 'react-i18next';

import { EmptyState } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import styles from '../../staff/staff.module.css';

/**
 * Account-currents: intermediary statements. The BIL slice has no account-current data or endpoint yet
 * (bil.AccountCurrent.* is CONTRACT_NOT_SERVED), so the screen says so instead of showing invented rows.
 * TODO(hardening): list/detail/dispute once the backend serves bil.AccountCurrent.
 */
export function AccountCurrentsPage() {
  const { t } = useTranslation('billing');
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('accountCurrents.title')}
        description={t('accountCurrents.subtitle')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <Section title={t('accountCurrents.listTitle')} family="info">
        <EmptyState
          kind="first-use"
          headingLevel={3}
          headline={t('accountCurrents.unavailableTitle')}
          description={t('accountCurrents.unavailableBody')}
        />
      </Section>
    </div>
  );
}
