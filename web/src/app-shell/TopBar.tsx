import { CircleHelp, Search } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button as AriaButton } from 'react-aria-components';

import { Button } from '../design-system/components/Button';
import { Kbd } from '../design-system/components/Kbd';
import { Icon } from '../design-system/icons';
import { cx } from '../design-system/utils/cx';
import styles from './AppShell.module.css';
import { LanguageSwitch } from './LanguageSwitch';

export interface TopBarProps {
  /** Shown only when the user works for more than one legal entity (Part 1 §3.4). */
  entityName?: string;
  entityCount?: number;
  breadcrumb?: ReactNode;
  onOpenPalette: () => void;
  onOpenHelp: () => void;
  /** Notification bell (design-system NotificationBell). */
  notifications?: ReactNode;
  /** User menu trigger with presence (design-system Avatar inside a menu). */
  userMenu?: ReactNode;
}

/**
 * Top bar (v3 mockup `.top`): glass chrome, entity chip, breadcrumb/title, centred palette trigger
 * («Αναζήτηση ή εντολή…» + Ctrl K), bell, «?» help in the same place on every screen (WCAG 3.2.6),
 * language switch, user menu.
 */
export function TopBar({
  entityName,
  entityCount = 1,
  breadcrumb,
  onOpenPalette,
  onOpenHelp,
  notifications,
  userMenu,
}: TopBarProps) {
  const { t } = useTranslation('shell');
  return (
    <header className={cx(styles.topBar)} data-material="chrome" data-shell-region="banner" data-print="hide">
      <div className={cx(styles.crumbs)}>
        {entityName && entityCount > 1 ? (
          <span className={cx(styles.entity)} title={t('topBar.entity', { name: entityName })}>
            {entityName}
          </span>
        ) : null}
        {breadcrumb}
      </div>
      <AriaButton className={cx(styles.paletteTrigger)} onPress={onOpenPalette} aria-label={t('topBar.searchLabel')}>
        <Icon icon={Search} size={16} />
        <span className={cx(styles.paletteText)}>{t('topBar.search')}</span>
        <span className={cx(styles.paletteKbd)}>
          <Kbd shortcut="Mod+K" />
        </span>
      </AriaButton>
      <div className={cx(styles.topActions)}>
        {notifications}
        <Button variant="ghost" icon={CircleHelp} label={t('topBar.help')} shortcut="?" onPress={onOpenHelp} />
        <LanguageSwitch />
        {userMenu}
      </div>
    </header>
  );
}
