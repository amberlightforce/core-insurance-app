import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, EmptyState } from '../../design-system';
import { PortalPage } from '../ui/PortalUi';
import { usePortalTitle } from '../ui/hooks';

export function NotFoundPage() {
  const { t } = useTranslation('portal');
  const navigate = useNavigate();
  usePortalTitle(t('notFound.title'));
  return (
    <PortalPage>
      <EmptyState
        kind="first-use"
        headingLevel={1}
        headline={t('notFound.title')}
        description={t('notFound.body')}
        action={
          <Button
            variant="primary"
            onPress={() => {
              void navigate('/portal');
            }}
          >
            {t('notFound.back')}
          </Button>
        }
      />
    </PortalPage>
  );
}
