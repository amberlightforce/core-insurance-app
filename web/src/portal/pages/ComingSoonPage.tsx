import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, EmptyState } from '../../design-system';
import { PortalPage } from '../ui/PortalUi';
import { usePortalTitle } from '../ui/hooks';

/**
 * Stub for a portal area whose pages have not landed.
 * TODO(BO-CHN-13): replace each stub route in `portalRoutes.tsx` with the real page.
 */
export function ComingSoonPage({
  area,
}: {
  area: 'policies' | 'billing' | 'documents' | 'messages' | 'notifications';
}) {
  const { t } = useTranslation('portal');
  const navigate = useNavigate();
  const name = t(`nav.${area}`);
  usePortalTitle(name);
  return (
    <PortalPage>
      <EmptyState
        kind="first-use"
        headingLevel={1}
        headline={t('soon.title', { area: name })}
        description={t('soon.body')}
        action={
          <Button
            variant="primary"
            onPress={() => {
              void navigate('/portal');
            }}
          >
            {t('soon.back')}
          </Button>
        }
      />
    </PortalPage>
  );
}

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
