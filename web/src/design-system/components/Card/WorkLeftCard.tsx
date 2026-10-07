import { ArrowRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import type { StatusFamily } from '../../tokens';
import { cx, defined } from '../../utils/cx';
import type { HeadingLevel } from '../States/Heading';
import { Card, type CardState } from './Card';
import styles from './Card.module.css';

export interface WorkLeftItem {
  id: string;
  label: string;
  /** A pill or a 24 px ghost action button; it sits above the card's stretched link. */
  meta?: ReactNode;
}

export interface WorkLeftCardProps {
  title: string;
  /** Status family of the 8 px dot (e.g. `danger` for breached, `warning` for due soon). */
  family: StatusFamily;
  count: number;
  /** At most three items are shown. */
  items?: WorkLeftItem[];
  /** «Συνέχεια»: the card's stretched link. */
  continueHref?: string;
  onContinue?: () => void;
  headingLevel?: HeadingLevel;
  onCanvas?: boolean;
  state?: CardState;
  onRetry?: () => void;
}

/**
 * Work-left card (IB-11): family dot with halo, title, count, up to 3 items and «Συνέχεια». The whole card
 * is the «Συνέχεια» link; hover lifts it to elevation.2. MI-40 (leaving the list) is the parent list's job.
 */
export function WorkLeftCard({
  title,
  family,
  count,
  items = [],
  continueHref,
  onContinue,
  headingLevel = 3,
  onCanvas = false,
  state = 'ready',
  onRetry,
}: WorkLeftCardProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const formattedCount = new Intl.NumberFormat(region, {
    useGrouping: 'always',
  }).format(count);
  const canContinue = continueHref !== undefined || onContinue !== undefined;

  return (
    <Card
      variant="work-left"
      family={family}
      title={title}
      headingLevel={headingLevel}
      onCanvas={onCanvas}
      state={state}
      meta={
        <>
          <span className={styles.count} aria-hidden="true">
            {formattedCount}
          </span>
          <span className="ds-visually-hidden">{t('card.count', { count })}</span>
        </>
      }
      {...defined({ onRetry })}
    >
      {items.length > 0 ? (
        <ul className={styles.items}>
          {items.slice(0, 3).map((item) => (
            <li key={item.id} className={styles.item}>
              <span className={styles.itemLabel}>{item.label}</span>
              {item.meta !== undefined ? (
                <span className={styles.itemMeta}>{item.meta}</span>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
      {canContinue ? (
        <Link
          className={cx(styles.link, styles.continue)}
          aria-label={t('card.continueLabel', { title })}
          {...defined({ href: continueHref, onPress: onContinue })}
        >
          {t('card.continue')}
          <Icon icon={ArrowRight} size={14} />
        </Link>
      ) : null}
    </Card>
  );
}
