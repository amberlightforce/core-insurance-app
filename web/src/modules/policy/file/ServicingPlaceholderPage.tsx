import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { Banner } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader } from '../../staff/PageHeader';
import styles from '../../staff/staff.module.css';

type Action = 'change' | 'cancel' | 'renew';

/**
 * Entry point of a servicing screen until SL3-UI-POL-JOBS builds it: the policy file's action bar links here
 * (`/policies/:policyId/change|cancel|renew`), and the JOBS work package swaps only these three route elements.
 */
function Placeholder({ action }: { action: Action }) {
  const { t } = useTranslation('policy');
  const { policyId = '' } = useParams();
  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={t(`file.servicing.${action}.title`)}
        actions={
          <LinkButton variant="secondary" to={`/policies/${encodeURIComponent(policyId)}`}>
            {t('file.servicing.back')}
          </LinkButton>
        }
      />
      <Banner variant="info" live="none" title={t('file.servicing.soonTitle')}>
        {t('file.servicing.soonBody')}
      </Banner>
    </div>
  );
}

export function PolicyChangePlaceholder() {
  return <Placeholder action="change" />;
}

export function PolicyCancelPlaceholder() {
  return <Placeholder action="cancel" />;
}

export function PolicyRenewPlaceholder() {
  return <Placeholder action="renew" />;
}
