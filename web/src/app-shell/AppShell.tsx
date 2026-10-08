import { useCallback, useEffect, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { RouterProvider } from 'react-aria-components';
import { useHref, useLocation, useNavigate, type NavigateOptions } from 'react-router';

import { announce } from '../design-system/a11y/announce';
import { matchesShortcut } from '../design-system/components/Kbd';
import { usePreferences } from '../design-system/preferences';
import { cx } from '../design-system/utils/cx';
import styles from './AppShell.module.css';
import { EnvRibbon } from './EnvRibbon';
import { cycleRegion, isTypingTarget } from './keyboard';
import { NavRail } from './NavRail';
import { defaultNavItems, type Environment, type NavItem } from './navigation';
import { StatusBar } from './StatusBar';
import { TopBar } from './TopBar';
import type { ShellUser } from './user';
import { UserMenu } from './UserMenu';

declare module 'react-aria-components' {
  interface RouterConfig {
    routerOptions: NavigateOptions;
  }
}

export interface AppShellProps {
  children: ReactNode;
  /** Page title for `<title>` («{page} · {product}») and the polite route announcement (WCAG 2.4.2). */
  pageTitle: string;
  navItems?: NavItem[];
  entityName: string;
  entityCount?: number;
  environment?: Environment;
  aiEnabled?: boolean;
  backgroundJobs?: number;
  breadcrumb?: ReactNode;
  onOpenPalette: () => void;
  onOpenHelp: () => void;
  notifications?: ReactNode;
  /** Signed-in user shown in the avatar menu; `null` when signed out. */
  user?: ShellUser | null;
  /** Avatar menu account action: sign in, or switch user / sign out. */
  onAccount?: () => void;
  /** Work-views sidebar (IB-03), workbenches only. */
  workViews?: ReactNode;
  /** Context panel (assistant, activity, notes, documents). */
  contextPanel?: ReactNode;
  /** Injected clock for the status bar (tests, stories). */
  now?: () => Date;
}

/**
 * Staff app shell from the approved v3 mockup (Part 1 §3.4): skip link → env ribbon → glass rail + top bar
 * over the ambient canvas → main region (the only content scroll container) → optional work views and
 * context panel → glass status bar. The main region is transparent: pages put their own sheets and cards on
 * the canvas (home canopy, record sheet), so the ambient sea shows between them. Landmarks in order;
 * F6 / Shift+F6 cycle regions; Ctrl/⌘+K opens the palette from anywhere; `?` opens help and `[` toggles the
 * rail (single-key shortcuts honour the user setting).
 */
export function AppShell({
  children,
  pageTitle,
  navItems = defaultNavItems,
  entityName,
  entityCount = 1,
  environment = null,
  aiEnabled = true,
  backgroundJobs = 0,
  breadcrumb,
  onOpenPalette,
  onOpenHelp,
  notifications,
  user = null,
  onAccount,
  workViews,
  contextPanel,
  now,
}: AppShellProps) {
  const { t } = useTranslation(['shell', 'common']);
  const navigate = useNavigate();
  const location = useLocation();
  const { preferences, setPreference } = usePreferences();
  const shellRef = useRef<HTMLDivElement>(null);
  const firstRender = useRef(true);

  const toggleRail = useCallback(() => {
    setPreference('railExpanded', !preferences.railExpanded);
  }, [preferences.railExpanded, setPreference]);

  // <title> and the polite route-change announcement (G-03).
  useEffect(() => {
    const prefix = environment ? `[${environment.name}] ` : '';
    const title = `${prefix}${t('shell:title', { page: pageTitle, product: t('common:app.product') })}`;
    document.title = title;
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    announce(pageTitle);
  }, [pageTitle, location.pathname, environment, t]);

  // Global keyboard layer.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (matchesShortcut(event, 'Mod+K')) {
        event.preventDefault();
        onOpenPalette();
        return;
      }
      if (event.key === 'F6') {
        if (shellRef.current && cycleRegion(shellRef.current, event.shiftKey))
          event.preventDefault();
        return;
      }
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (!preferences.singleKeyShortcuts || isTypingTarget(event.target)) return;
      if (event.key === '?') {
        event.preventDefault();
        onOpenHelp();
      } else if (event.key === '[') {
        event.preventDefault();
        toggleRail();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [onOpenPalette, onOpenHelp, toggleRail, preferences.singleKeyShortcuts]);

  return (
    <RouterProvider navigate={(to, options) => void navigate(to, options)} useHref={useHref}>
      <div
        ref={shellRef}
        className={styles.shell}
        data-rail-expanded={preferences.railExpanded || undefined}
        data-has-ribbon={environment ? true : undefined}
      >
        <a className={styles.skipLink} href="#main-content">
          {t('shell:skipLink')}
        </a>
        <div className={styles.ambient} aria-hidden="true">
          <i />
          <i />
          <i />
          <i />
        </div>
        {environment ? <EnvRibbon environment={environment} /> : null}
        <TopBar
          entityName={entityName}
          entityCount={entityCount}
          breadcrumb={breadcrumb}
          pageTitle={pageTitle}
          onOpenPalette={onOpenPalette}
          notifications={notifications}
          userMenu={
            <UserMenu
              user={user}
              onOpenHelp={onOpenHelp}
              onAccount={
                onAccount ??
                (() => {
                  void navigate('/dev/sign-in');
                })
              }
            />
          }
        />
        <NavRail
          items={navItems}
          expanded={preferences.railExpanded}
          onToggleExpanded={toggleRail}
        />
        <div
          className={cx(
            styles.body,
            workViews ? styles.withWorkViews : undefined,
            contextPanel ? styles.withContext : undefined,
          )}
        >
          {workViews ? (
            <nav
              className={styles.workViews}
              aria-label={t('shell:landmarks.workViews')}
              data-shell-region="workviews"
            >
              {workViews}
            </nav>
          ) : null}
          <main
            id="main-content"
            className={styles.main}
            tabIndex={-1}
            data-shell-region="main"
          >
            {children}
          </main>
          {contextPanel ? (
            <aside
              className={styles.contextPanel}
              aria-label={t('shell:landmarks.context')}
              data-shell-region="context"
            >
              {contextPanel}
            </aside>
          ) : null}
        </div>
        <StatusBar
          entityName={entityName}
          environmentName={environment?.name ?? 'PROD'}
          aiEnabled={aiEnabled}
          backgroundJobs={backgroundJobs}
          clockShifted={Boolean(environment?.shiftedDate)}
          {...(now ? { now } : {})}
        />
      </div>
    </RouterProvider>
  );
}
