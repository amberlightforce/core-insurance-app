import { Undo2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';

import { Banner, Icon } from '../../design-system';
import { portalNav, withdrawalPath } from '../ui/nav';
import { PortalHero, PortalPage, PortalSection } from '../ui/PortalUi';
import { usePortalTitle } from '../ui/hooks';
import styles from '../ui/portal.module.css';

/**
 * Portal home (BO-CHN-12 placeholder). It shows the way into the portal's areas and the withdrawal declaration and
 * makes no claim about the customer's policies or balances.
 * TODO(BO-CHN-13): replace the pending banner with the dashboard and work-left cards once the portal read API
 * (BO-CHN-02: dashboard, policies, policy detail) is available; feature-detect nothing, call the real endpoints.
 */
export function DashboardPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.overview'));
  const shortcuts = portalNav.filter((item) => item.id !== 'overview');

  return (
    <PortalPage>
      <PortalHero
        title={t('dashboard.title')}
        overline={t('dashboard.overline')}
        lead={t('dashboard.lead')}
      />
      <Banner variant="info" title={t('dashboard.pendingTitle')}>
        {t('dashboard.pendingBody')}
      </Banner>
      <PortalSection title={t('dashboard.shortcuts')} family="brand">
        <ul className={styles.grid}>
          {shortcuts.map((item) => (
            <li key={item.id} className={styles.gridItem}>
              <Link to={item.to} className={styles.shortcut}>
                <span className={styles.shortcutIcon} aria-hidden="true">
                  <Icon icon={item.icon} size={20} />
                </span>
                <h3 className={styles.shortcutTitle}>{t(`nav.${item.id}`)}</h3>
                <p className={styles.shortcutBody}>{t(`dashboard.shortcut.${item.id}`)}</p>
              </Link>
            </li>
          ))}
        </ul>
      </PortalSection>
      <PortalSection title={t('dashboard.withdrawalTitle')} family="teal">
        <p className={styles.muted}>{t('dashboard.withdrawalBody')}</p>
        <div className={styles.actions}>
          <Link to={withdrawalPath} className={styles.textLink}>
            <Icon icon={Undo2} size={14} /> {t('dashboard.withdrawalAction')}
          </Link>
        </div>
      </PortalSection>
    </PortalPage>
  );
}
