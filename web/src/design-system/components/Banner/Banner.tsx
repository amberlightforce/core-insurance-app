import {
  CircleAlert,
  CircleCheck,
  Info,
  Megaphone,
  Sparkles,
  TriangleAlert,
  X,
  type LucideIcon,
} from 'lucide-react';
import { useEffect, useId, useState, type ReactNode, type Ref } from 'react';
import { useObjectRef } from 'react-aria';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useReducedMotion } from '../../preferences/context';
import { durations } from '../../tokens';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import styles from './Banner.module.css';

export type BannerVariant = 'info' | 'success' | 'warning' | 'danger' | 'ai' | 'system';

const icons: Record<BannerVariant, LucideIcon> = {
  info: Info,
  success: CircleCheck,
  warning: TriangleAlert,
  danger: CircleAlert,
  ai: Sparkles,
  system: Megaphone,
};

export interface BannerProps {
  variant?: BannerVariant;
  title: ReactNode;
  /** Body text (`body.compact`). */
  children?: ReactNode;
  /** Link-style actions (RAC `Link` / `Button variant="link"`), e.g. «Ανανέωση». */
  actions?: ReactNode;
  /** `inline` (radius.lg, inside a region) or `page` (radius.none, under the record header). */
  layout?: 'inline' | 'page';
  /**
   * Live-region role. Defaults to `alert` for danger (an error in response to an action) and `status`
   * otherwise. Use `none` for banners that are present on load and should not be announced.
   */
  live?: 'status' | 'alert' | 'none';
  /** Shows ×. Ignored for danger banners, which cannot be dismissed (conflicts, blocking errors). */
  onDismiss?: () => void;
  /** Moves focus to the banner when it appears (error summary on submit failure, WCAG 3.3.1). */
  autoFocus?: boolean;
  ref?: Ref<HTMLDivElement>;
  className?: string;
}

/**
 * Banner (Part 2 §4.22): 3 px family accent bar, 16 px icon, title, body, link actions and an optional ×.
 * Appears with MI-27 (grid rows 0fr→1fr + opacity); dismissing fades for 150 ms before `onDismiss`.
 */
export function Banner({
  variant = 'info',
  title,
  children,
  actions,
  layout = 'inline',
  live,
  onDismiss,
  autoFocus = false,
  ref,
  className,
}: BannerProps) {
  const { t } = useTranslation('ds');
  const reducedMotion = useReducedMotion();
  const titleId = useId();
  const localRef = useObjectRef(ref);
  const [closing, setClosing] = useState(false);
  const role = live ?? (variant === 'danger' ? 'alert' : 'status');
  const dismissible = onDismiss !== undefined && variant !== 'danger';

  useEffect(() => {
    if (autoFocus) localRef.current?.focus();
  }, [autoFocus, localRef]);

  useEffect(() => {
    if (!closing || !onDismiss) return;
    const id = setTimeout(onDismiss, reducedMotion ? 0 : durations.feedbackLg);
    return () => {
      clearTimeout(id);
    };
  }, [closing, onDismiss, reducedMotion]);

  return (
    <div
      ref={localRef}
      className={cx(styles.banner, className)}
      data-variant={variant}
      data-layout={layout}
      data-closing={closing || undefined}
      {...(role === 'none' ? {} : { role })}
      aria-labelledby={titleId}
      tabIndex={autoFocus ? -1 : undefined}
    >
      <div className={styles.clip}>
        <div className={styles.inner}>
          <span className={styles.icon}>
            <Icon icon={icons[variant]} size={16} />
          </span>
          <div className={styles.text}>
            <p id={titleId} className={styles.title}>
              {title}
            </p>
            {children ? <div className={styles.body}>{children}</div> : null}
            {actions ? <div className={styles.actions}>{actions}</div> : null}
          </div>
          {dismissible ? (
            <Button
              variant="ghost"
              size="sm"
              icon={X}
              label={t('banner.dismiss')}
              onPress={() => {
                setClosing(true);
              }}
            />
          ) : null}
        </div>
      </div>
    </div>
  );
}
