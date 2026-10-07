import { ChevronRight, Ellipsis } from 'lucide-react';
import {
  Breadcrumb,
  Breadcrumbs as AriaBreadcrumbs,
  Button as AriaButton,
  Link,
  Menu,
  MenuItem,
  MenuTrigger,
  Popover,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { collapseBreadcrumbs, type BreadcrumbItem } from './collapse';
import styles from './Breadcrumbs.module.css';

export interface BreadcrumbsProps {
  items: readonly BreadcrumbItem[];
  /** Called for items without `href` (client-side navigation by id). */
  onAction?: (id: string) => void;
  /** More items than this collapse the middle into a ⋯ menu (default 4). */
  maxItems?: number;
  /** Landmark name (default «Διαδρομή πλοήγησης»). */
  'aria-label'?: string;
}

function Separator() {
  return (
    <span className={styles.separator} aria-hidden="true">
      <Icon icon={ChevronRight} size={14} />
    </span>
  );
}

/**
 * Breadcrumbs on RAC Breadcrumbs: `nav` landmark, the last item is the current page
 * (`aria-current="page"`), and more than four items collapse the middle into a ⋯ menu.
 */
export function Breadcrumbs({ items, onAction, maxItems = 4, ...rest }: BreadcrumbsProps) {
  const { t } = useTranslation('ds');
  const { head, collapsed, tail } = collapseBreadcrumbs(items, maxItems);
  const act = (id: string) => {
    const item = items.find((candidate) => candidate.id === id);
    if (item && !item.href) onAction?.(id);
  };

  const crumb = (item: BreadcrumbItem, isLast: boolean) => (
    <Breadcrumb key={item.id} id={item.id} className={cx(styles.item)}>
      <Link
        className={cx(styles.link)}
        {...(item.href ? { href: item.href } : {})}
        {...(!isLast && !item.href
          ? {
              onPress: () => {
                act(item.id);
              },
            }
          : {})}
      >
        {item.label}
      </Link>
      {isLast ? null : <Separator />}
    </Breadcrumb>
  );

  return (
    <nav aria-label={rest['aria-label'] ?? t('breadcrumbs.label')} className={styles.nav}>
      <AriaBreadcrumbs className={cx(styles.list)}>
        {head.map((item) => crumb(item, false))}
        {collapsed.length > 0 ? (
          <Breadcrumb id="__collapsed" className={cx(styles.item)}>
            <MenuTrigger>
              <AriaButton
                className={cx(styles.collapsed)}
                aria-label={t('breadcrumbs.collapsed', { count: collapsed.length })}
              >
                <Icon icon={Ellipsis} size={16} />
              </AriaButton>
              <Popover
                className={cx(styles.popover)}
                placement="bottom start"
                data-material="popover"
              >
                <Menu
                  className={cx(styles.menu)}
                  items={collapsed}
                  onAction={(key) => {
                    act(String(key));
                  }}
                >
                  {(item) => (
                    <MenuItem
                      id={item.id}
                      className={cx(styles.menuItem)}
                      {...(item.href ? { href: item.href } : {})}
                    >
                      {item.label}
                    </MenuItem>
                  )}
                </Menu>
              </Popover>
            </MenuTrigger>
            <Separator />
          </Breadcrumb>
        ) : null}
        {tail.map((item, index) => crumb(item, index === tail.length - 1))}
      </AriaBreadcrumbs>
    </nav>
  );
}
