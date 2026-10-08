import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Outlet, useLocation, useNavigate } from 'react-router';

import { AppShell } from './app-shell/AppShell';
import {
  defaultNavItems,
  isActivePath,
  visibleNavItems,
  type NavItem,
} from './app-shell/navigation';
import { Avatar } from './design-system/components/Avatar';
import { Button } from './design-system/components/Button';
import { CommandPalette, type PaletteItem } from './design-system/components/CommandPalette';
import { NotificationBell, NotificationCenter } from './design-system/components/Notifications';
import { ShortcutOverlay, type ShortcutGroup } from './design-system/components/ShortcutOverlay';
import { EmptyState } from './design-system/components/States';
import { Toaster } from './design-system/components/Toast';
import { moduleIcons } from './design-system/icons';
import { readSession } from './dev-auth/devAuth';

/**
 * Staff portal root: the Aegean shell around the routed module. Modules are placeholders until their work
 * packages land; the palette, help overlay, notifications and language switch are live.
 * Identity (MSAL React, D-FE-03) and the PLT user profile replace the sample user and entity.
 */
const sampleUser = { name: 'Κώστας Νικολάου' };
const sampleEntity = 'Παράδειγμα Ασφαλιστική Α.Ε.';

function useCurrentNavItem(): NavItem {
  const { pathname } = useLocation();
  return (
    [...defaultNavItems].reverse().find((item) => isActivePath(pathname, item.to)) ??
    defaultNavItems[0] ?? { id: 'home', to: '/' }
  );
}

export function AppLayout() {
  const { t } = useTranslation(['shell', 'ds']);
  const navigate = useNavigate();
  const current = useCurrentNavItem();
  const { pathname } = useLocation();
  // Role-gated entries (claims) follow the signed-in user; the session is re-read on each navigation.
  const navItems = useMemo(
    () => visibleNavItems(defaultNavItems, readSession()?.user.roles ?? []),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- pathname re-reads the session after a sign-in
    [pathname],
  );
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);

  const openPalette = useCallback(() => {
    setPaletteOpen((open) => !open);
  }, []);
  const openHelp = useCallback(() => {
    setHelpOpen(true);
  }, []);

  const paletteItems = useMemo<PaletteItem[]>(
    () =>
      navItems.map((item) => ({
        id: `nav-${item.id}`,
        group: 'navigation',
        title: t(`shell:nav.${item.id}`),
        icon: moduleIcons[item.id],
        onAction: () => {
          void navigate(item.to);
        },
      })),
    [navItems, navigate, t],
  );

  const shortcutGroups = useMemo<ShortcutGroup[]>(
    () => [
      {
        title: t('shell:help.general'),
        shortcuts: [
          { keys: 'Mod+K', description: t('shell:help.palette') },
          { keys: '?', description: t('shell:help.shortcuts') },
          { keys: 'F6', description: t('shell:help.regions') },
          { keys: '[', description: t('shell:help.rail') },
          { keys: 'F8', description: t('shell:help.toasts') },
        ],
      },
    ],
    [t],
  );

  return (
    <>
      <AppShell
        pageTitle={t(`shell:nav.${current.id}`)}
        navItems={navItems}
        entityName={sampleEntity}
        onOpenPalette={openPalette}
        onOpenHelp={openHelp}
        notifications={
          <NotificationBell
            count={0}
            isExpanded={notificationsOpen}
            onPress={() => {
              setNotificationsOpen((open) => !open);
            }}
          />
        }
        userMenu={<Avatar name={sampleUser.name} size="md" />}
      >
        <Outlet />
      </AppShell>
      <CommandPalette isOpen={paletteOpen} onOpenChange={setPaletteOpen} items={paletteItems} />
      <ShortcutOverlay groups={shortcutGroups} isOpen={helpOpen} onOpenChange={setHelpOpen} />
      <NotificationCenter
        isOpen={notificationsOpen}
        onOpenChange={setNotificationsOpen}
        notifications={[]}
      />
      <Toaster />
    </>
  );
}

/** A module whose work package has not landed yet. */
export function ModulePlaceholder() {
  const { t } = useTranslation('shell');
  const navigate = useNavigate();
  const current = useCurrentNavItem();
  const module = t(`nav.${current.id}`);
  return (
    <EmptyState
      kind="first-use"
      headingLevel={1}
      headline={t('home.placeholderTitle', { module })}
      description={t('home.placeholderBody')}
      {...(current.id === 'home'
        ? {}
        : {
            action: (
              <Button
                variant="secondary"
                onPress={() => {
                  void navigate('/');
                }}
              >
                {t('home.goHome')}
              </Button>
            ),
          })}
    />
  );
}
