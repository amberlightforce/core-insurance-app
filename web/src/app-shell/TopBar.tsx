import { Building2, ChevronDown, Search } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button as AriaButton } from 'react-aria-components';

import { Kbd } from '../design-system/components/Kbd';
import { Icon } from '../design-system/icons';
import { cx } from '../design-system/utils/cx';
import styles from './AppShell.module.css';

export interface TopBarProps {
  /** Legal entity chip (v3 mockup `.crumb .entity`); a switcher only when the user works for several. */
  entityName?: string;
  entityCount?: number;
  /** Where the user is: a breadcrumb, or the page title. */
  breadcrumb?: ReactNode;
  pageTitle?: string;
  onOpenPalette: () => void;
  /** Notification bell (design-system NotificationBell). */
  notifications?: ReactNode;
  /** Avatar menu: user, role, appearance settings, help. */
  userMenu?: ReactNode;
}

/**
 * Top bar (v3 mockup `.top`): glass chrome, entity chip and the current page, centred palette trigger
 * («Αναζήτηση ή εντολή…» + Ctrl K), bell and the avatar menu — nothing else, so the right edge stays quiet.
 */
export function TopBar({
  entityName,
  entityCount = 1,
  breadcrumb,
  pageTitle,
  onOpenPalette,
  notifications,
  userMenu,
}: TopBarProps) {
  const { t } = useTranslation('shell');
  return (
    <header
      className={cx(styles.topBar)}
      data-material="chrome"
      data-shell-region="banner"
      data-print="hide"
    >
      <div className={cx(styles.crumbs)}>
        {entityName ? (
          <span className={cx(styles.entity)} title={t('topBar.entity', { name: entityName })}>
            <Icon icon={Building2} size={14} />
            <span className={cx(styles.entityName)}>{entityName}</span>
            {entityCount > 1 ? <Icon icon={ChevronDown} size={12} /> : null}
          </span>
        ) : null}
        {breadcrumb ??
          (pageTitle ? <span className={cx(styles.pageCrumb)}>{pageTitle}</span> : null)}
      </div>
      <AriaButton
        className={cx(styles.paletteTrigger)}
        onPress={onOpenPalette}
        aria-label={t('topBar.searchLabel')}
      >
        <Icon icon={Search} size={14} />
        <span className={cx(styles.paletteText)}>{t('topBar.search')}</span>
        <span className={cx(styles.paletteKbd)}>
          <Kbd shortcut="Mod+K" />
        </span>
      </AriaButton>
      <div className={cx(styles.topActions)}>
        {notifications}
        {userMenu}
      </div>
    </header>
  );
}
