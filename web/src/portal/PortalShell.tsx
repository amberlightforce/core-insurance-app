import { LogIn, LogOut, Undo2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ToggleButton, ToggleButtonGroup, type Key } from 'react-aria-components';
import { Link, NavLink, Outlet, useNavigate } from 'react-router';

import { portalRoles } from '../auth/roles';
import { clearSession, useDevSession } from '../dev-auth/devAuth';
import { Avatar, Button, Icon, NotificationBell, cx } from '../design-system';
import { changeLanguage, isLanguage, supportedLanguages } from '../i18n';
import styles from './PortalShell.module.css';
import { usePortalDashboard } from './pages/portalApi';
import { portalNav, withdrawalPath } from './ui/nav';

const shortNames = { el: 'ΕΛ', en: 'EN' } as const;

/** Synthetic dev users carry a «Dev » prefix and «(synthetic)» marker; the portal shows the persona only. */
function personaName(name: string): string {
  return (
    name
      .replace(/\s*\([^)]*\)\s*$/u, '')
      .replace(/^Dev\s+/u, '')
      .trim() || name
  );
}

function LanguageSwitch() {
  const { t, i18n } = useTranslation('portal');
  const current = isLanguage(i18n.language) ? i18n.language : 'el';
  const onChange = (keys: Set<Key>) => {
    const [next] = [...keys];
    if (typeof next === 'string' && isLanguage(next) && next !== current) void changeLanguage(next);
  };
  return (
    <ToggleButtonGroup
      className={cx(styles.language)}
      aria-label={t('language.label')}
      selectionMode="single"
      disallowEmptySelection
      selectedKeys={[current]}
      onSelectionChange={onChange}
    >
      {supportedLanguages.map((lng) => (
        <ToggleButton
          key={lng}
          id={lng}
          className={cx(styles.languageOption)}
          aria-label={t(`language.${lng}`)}
          lang={lng}
        >
          {shortNames[lng]}
        </ToggleButton>
      ))}
    </ToggleButtonGroup>
  );
}

/**
 * The customer portal shell (BO-CHN-12), mounted at `/portal` outside the staff AppShell. Same Aegean language
 * (ambient canvas, glass header) with a simpler frame for customers: brand, five-area nav, language switch,
 * notification bell and an account button, then the page, then a footer carrying the withdrawal link.
 * Signed-out visitors (the public withdrawal declaration) get the brand, language switch and a sign-in button.
 * Portal code never imports staff modules (ESLint `no-restricted-imports` on `src/portal/**`).
 */
export function PortalShell() {
  const { t } = useTranslation('portal');
  const navigate = useNavigate();
  const session = useDevSession();
  const isCustomer =
    session?.user.roles.some((r) => (portalRoles as readonly string[]).includes(r)) ?? false;
  const name = session ? personaName(session.user.name) : '';
  // The bell shows the unread count of the portal dashboard read; a failed read shows no badge rather than a wrong one.
  const unread = usePortalDashboard(isCustomer).data?.unreadNotificationCount ?? 0;

  return (
    <div className={styles.shell}>
      <a className={styles.skipLink} href="#portal-main">
        {t('skipLink')}
      </a>
      <div className={styles.ambient} aria-hidden="true">
        <i />
        <i />
        <i />
        <i />
      </div>

      <header className={styles.header} data-material="chrome">
        <Link to="/portal" className={styles.brand} aria-label={t('brand')}>
          <span className={styles.logo} aria-hidden="true">
            Α
          </span>
          <span className={styles.brandName}>{t('brand')}</span>
        </Link>

        {isCustomer ? (
          <nav className={styles.nav} aria-label={t('nav.label')}>
            <ul className={styles.navList}>
              {portalNav.map((item) => (
                <li key={item.id}>
                  <NavLink to={item.to} end={item.to === '/portal'} className={cx(styles.navLink)}>
                    <Icon icon={item.icon} size={16} />
                    <span>{t(`nav.${item.id}`)}</span>
                  </NavLink>
                </li>
              ))}
            </ul>
          </nav>
        ) : null}

        <div className={styles.actions}>
          <LanguageSwitch />
          {isCustomer ? (
            <>
              <NotificationBell
                count={unread}
                attention={unread > 0}
                onPress={() => {
                  void navigate('/portal/notifications');
                }}
              />
              <Link
                to="/portal/profile"
                className={styles.account}
                title={t('account.signedInAs', { name })}
                aria-label={t('account.profile', { name })}
              >
                <Avatar name={name} size="sm" decorative />
                <span className={styles.accountName}>{name}</span>
              </Link>
              <Button
                variant="ghost"
                size="sm"
                aria-label={t('account.signOut')}
                onPress={() => {
                  clearSession();
                  void navigate('/dev/sign-in');
                }}
              >
                <span className={cx(styles.signOut)}>
                  <Icon icon={LogOut} size={16} />
                  <span className={cx(styles.signOutText)}>{t('account.signOut')}</span>
                </span>
              </Button>
            </>
          ) : (
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                void navigate('/dev/sign-in');
              }}
            >
              <span className={cx(styles.signOut)}>
                <Icon icon={LogIn} size={16} />
                {t('account.signIn')}
              </span>
            </Button>
          )}
        </div>
      </header>

      <main id="portal-main" className={styles.main} tabIndex={-1}>
        <Outlet />
      </main>

      <footer className={styles.footer} aria-label={t('footer.label')}>
        <Link to={withdrawalPath} className={styles.footerLink}>
          <Icon icon={Undo2} size={14} />
          {t('footer.withdrawal')}
        </Link>
        <span className={styles.footerNote}>
          {t('footer.company')} · {t('footer.note')}
        </span>
      </footer>
    </div>
  );
}
