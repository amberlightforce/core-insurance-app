import { Ellipsis, PanelLeftClose, PanelLeftOpen } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  Link,
  Menu,
  MenuItem,
  MenuTrigger,
  Popover,
  Button as AriaButton,
} from 'react-aria-components';
import { useLocation } from 'react-router';

import { Button } from '../design-system/components/Button';
import { Tooltip } from '../design-system/components/Tooltip';
import { Icon, moduleIcons } from '../design-system/icons';
import { cx } from '../design-system/utils/cx';
import styles from './AppShell.module.css';
import { adminNavItem, bottomBarSlots, isActivePath, type NavItem } from './navigation';

export interface NavRailProps {
  items: NavItem[];
  expanded: boolean;
  onToggleExpanded: () => void;
}

/**
 * Navigation rail (Part 1 §3.4, v3 mockup): glass chrome, 20 px icons in 40×40 hit areas, tooltips to the right
 * when collapsed, labels when expanded (`[` toggles). Below 905 px the same items render as a bottom tab bar
 * with at most 5 destinations plus «Περισσότερα».
 */
export function NavRail({ items, expanded, onToggleExpanded }: NavRailProps) {
  const { t } = useTranslation('shell');
  const { pathname } = useLocation();

  const renderLink = (item: NavItem, extraClass?: string) => {
    const label = t(`nav.${item.label ?? item.id}`);
    const current = isActivePath(pathname, item.to);
    const link = (
      <Link
        href={item.to}
        className={cx(styles.railLink)}
        {...(current ? { 'aria-current': 'page' as const } : {})}
        {...(expanded ? {} : { 'aria-label': label })}
      >
        <Icon icon={moduleIcons[item.icon ?? item.id]} size={20} />
        <span className={cx(styles.railLabel)}>{label}</span>
      </Link>
    );
    return (
      <li key={item.to} className={cx(styles.railItem, extraClass)}>
        {expanded ? (
          link
        ) : (
          <Tooltip content={label} placement="end">
            {link}
          </Tooltip>
        )}
      </li>
    );
  };

  const overflow = items.slice(bottomBarSlots);

  return (
    <nav
      className={cx(styles.rail)}
      data-material="chrome"
      data-expanded={expanded || undefined}
      data-shell-region="nav"
      aria-label={t('landmarks.primaryNav')}
      data-print="hide"
    >
      <span className={cx(styles.logo)} aria-hidden="true">
        Α
      </span>
      <ul className={cx(styles.railList)}>
        {items.map((item, index) =>
          renderLink(item, index >= bottomBarSlots ? styles.railOverflowItem : undefined),
        )}
        <li className={cx(styles.railMore)}>
          <MenuTrigger>
            <AriaButton className={cx(styles.railLink)} aria-label={t('nav.more')}>
              <Icon icon={Ellipsis} size={20} />
              <span className={cx(styles.railLabel)}>{t('nav.more')}</span>
            </AriaButton>
            <Popover className={cx(styles.menuPopover)} data-material="popover" placement="top end">
              <Menu className={cx(styles.menu)} aria-label={t('nav.more')}>
                {overflow.map((item) => (
                  <MenuItem
                    key={item.to}
                    id={item.to}
                    href={item.to}
                    className={cx(styles.menuItem)}
                  >
                    <Icon icon={moduleIcons[item.icon ?? item.id]} size={16} />
                    {t(`nav.${item.label ?? item.id}`)}
                  </MenuItem>
                ))}
              </Menu>
            </Popover>
          </MenuTrigger>
        </li>
      </ul>
      <span className={cx(styles.railGrow)} />
      <ul className={cx(styles.railList, styles.railBottom)}>
        {renderLink(adminNavItem, styles.railOverflowItem)}
      </ul>
      <div className={cx(styles.railToggle)}>
        <Button
          variant="ghost"
          icon={expanded ? PanelLeftClose : PanelLeftOpen}
          label={expanded ? t('nav.collapse') : t('nav.expand')}
          shortcut="["
          onPress={onToggleExpanded}
        />
      </div>
    </nav>
  );
}
