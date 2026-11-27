import { useTranslation } from 'react-i18next';
import { Navigate, Outlet, useNavigate } from 'react-router';

import { hasStaffAccess, portalRoles } from '../auth/roles';
import { clearSession, useDevSession } from '../dev-auth/devAuth';
import { Button, EmptyState } from '../design-system';
import { PortalHero, PortalPage } from './ui/PortalUi';
import { usePortalTitle } from './ui/hooks';

/** The page a signed-in account without portal access sees (no staff role to send it to, no portal role). */
export function PortalForbidden() {
  const { t } = useTranslation('portal');
  const navigate = useNavigate();
  usePortalTitle(t('forbidden.title'));
  return (
    <PortalPage>
      <PortalHero title={t('forbidden.title')} overline={t('brand')} />
      <EmptyState
        kind="first-use"
        headingLevel={2}
        headline={t('forbidden.title')}
        description={t('forbidden.body')}
        action={
          <Button
            variant="primary"
            onPress={() => {
              clearSession();
              void navigate('/dev/sign-in');
            }}
          >
            {t('forbidden.switch')}
          </Button>
        }
      />
    </PortalPage>
  );
}

/**
 * Route guard of the signed-in portal screens (BO-CHN-12): role `Customer.Portal` sees the nested screens; a
 * signed-out visitor goes to sign-in; a staff user is redirected to the staff home; any other account is refused.
 * The public withdrawal declaration sits outside this guard (open decision #2). The API enforces its own
 * permissions on every call; this only avoids screens that cannot work.
 */
export function PortalGuard() {
  const session = useDevSession();
  if (!session) return <Navigate to="/dev/sign-in" replace />;
  const roles = session.user.roles;
  if (roles.some((role) => (portalRoles as readonly string[]).includes(role))) return <Outlet />;
  if (hasStaffAccess(roles)) return <Navigate to="/" replace />;
  return <PortalForbidden />;
}
