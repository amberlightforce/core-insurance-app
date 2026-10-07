import { useId, type ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import type { StatusFamily } from '../../tokens';
import { cx, defined } from '../../utils/cx';
import { ErrorState, SkeletonBlock } from '../States';
import { Heading, type HeadingLevel } from '../States/Heading';
import styles from './Card.module.css';

export type CardVariant = 'static' | 'interactive' | 'selectable' | 'work-left';
export type CardState = 'ready' | 'loading' | 'error' | 'empty';

export interface CardProps {
  /**
   * - `static`: a plain container.
   * - `interactive`: the whole card is one stretched link (the title). Only explicit secondary buttons in
   *   `actions` may sit above it; never put other interactive content in the body.
   * - `selectable`: choice-card styling via `isSelected` (selection semantics come from the caller's
   *   `Radio`/`Checkbox`).
   * - `work-left` (IB-11): use `WorkLeftCard`.
   */
  variant?: CardVariant;
  /** On the canvas (not on a sheet): raised surface + elevation.1 instead of the 1 px border. */
  onCanvas?: boolean;
  title?: ReactNode;
  headingLevel?: HeadingLevel;
  meta?: ReactNode;
  /** Secondary actions in the header (stay clickable above an interactive card's stretched link). */
  actions?: ReactNode;
  footer?: ReactNode;
  children?: ReactNode;
  /** Interactive: destination of the stretched link. */
  href?: string;
  /** Interactive: press handler when the card opens something in place. */
  onPress?: () => void;
  /** Interactive: accessible name of the link when the title alone is not enough. */
  linkLabel?: string;
  isSelected?: boolean;
  state?: CardState;
  /** Error state message (what happened, where, how to fix it). */
  errorMessage?: string;
  onRetry?: () => void;
  correlationId?: string;
  /** Empty state text; defaults to «Δεν υπάρχουν δεδομένα». */
  emptyMessage?: string;
  emptyAction?: ReactNode;
  /** Work-left family dot. */
  family?: StatusFamily;
  'aria-label'?: string;
}

function CardSkeleton() {
  return (
    <>
      <SkeletonBlock width="64%" />
      <SkeletonBlock width="88%" />
      <SkeletonBlock width="40%" />
    </>
  );
}

/** Card (Part 2 §4.26): header (heading.3 + meta + actions), body, footer; loading, error and empty states. */
export function Card({
  variant = 'static',
  onCanvas = false,
  title,
  headingLevel = 3,
  meta,
  actions,
  footer,
  children,
  href,
  onPress,
  linkLabel,
  isSelected = false,
  state = 'ready',
  errorMessage,
  onRetry,
  correlationId,
  emptyMessage,
  emptyAction,
  family,
  'aria-label': ariaLabel,
}: CardProps) {
  const { t } = useTranslation('ds');
  const titleId = useId();
  const stretched = variant === 'interactive' && (href !== undefined || onPress !== undefined);

  const titleContent =
    stretched && title !== undefined ? (
      <Link
        className={cx(styles.link)}
        {...defined({ href, onPress })}
        {...(linkLabel ? { 'aria-label': linkLabel } : {})}
      >
        {title}
      </Link>
    ) : (
      title
    );

  let body: ReactNode;
  if (state === 'loading') {
    body = (
      <div className={styles.body} aria-busy="true">
        <span className="ds-visually-hidden">{t('card.loading')}</span>
        <CardSkeleton />
      </div>
    );
  } else if (state === 'error') {
    body = <ErrorState {...defined({ message: errorMessage, onRetry, correlationId })} />;
  } else if (state === 'empty') {
    body = (
      <div className={styles.empty}>
        <span>{emptyMessage ?? t('card.empty')}</span>
        {emptyAction}
      </div>
    );
  } else {
    body = children === undefined ? null : <div className={styles.body}>{children}</div>;
  }

  const named = ariaLabel
    ? { role: 'group', 'aria-label': ariaLabel }
    : title !== undefined
      ? { role: 'group', 'aria-labelledby': titleId }
      : {};
  const hasHeader = title !== undefined || meta !== undefined || actions !== undefined;

  return (
    <div
      className={styles.card}
      data-variant={variant}
      data-on-canvas={onCanvas || undefined}
      data-selected={(variant === 'selectable' && isSelected) || undefined}
      data-busy={state === 'loading' || undefined}
      {...named}
    >
      {variant === 'work-left' ? (
        <span className={styles.dot} data-family={family ?? 'neutral'} aria-hidden="true" />
      ) : null}
      {hasHeader ? (
        <div className={styles.header}>
          <div className={styles.titleBlock}>
            {title !== undefined ? (
              <Heading level={headingLevel} className={styles.title} id={titleId}>
                {titleContent}
              </Heading>
            ) : null}
            {meta !== undefined ? <div className={styles.meta}>{meta}</div> : null}
          </div>
          {actions !== undefined ? <div className={styles.actions}>{actions}</div> : null}
        </div>
      ) : null}
      {body}
      {footer !== undefined && state === 'ready' ? (
        <div className={styles.footer}>{footer}</div>
      ) : null}
    </div>
  );
}
