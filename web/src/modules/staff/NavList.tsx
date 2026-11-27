import { ChevronRight } from 'lucide-react';
import { Link } from 'react-aria-components';

import { Icon } from '../../design-system';
import { cx } from '../../design-system/utils/cx';
import styles from './staff.module.css';

export interface NavListItem {
  id: string;
  label: string;
  to: string;
  /** Small secondary line under the label. */
  hint?: string;
}

/** A card list of navigation rows: label, optional hint and a trailing chevron; the whole row is one link. */
export function NavList({
  items,
  'aria-label': ariaLabel,
}: {
  items: NavListItem[];
  'aria-label': string;
}) {
  return (
    <ul className={styles.navList} aria-label={ariaLabel}>
      {items.map((item) => (
        <li key={item.id}>
          <Link href={item.to} className={cx(styles.navLink)}>
            <span className={styles.navLabel}>
              <span>{item.label}</span>
              {item.hint ? <span className={styles.navHint}>{item.hint}</span> : null}
            </span>
            <Icon icon={ChevronRight} size={16} />
          </Link>
        </li>
      ))}
    </ul>
  );
}
