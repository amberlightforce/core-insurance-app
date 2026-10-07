import { X } from 'lucide-react';
import {
  cloneElement,
  useEffect,
  useRef,
  type ReactElement,
  type ReactNode,
  type RefObject,
} from 'react';
import {
  Dialog as AriaDialog,
  DialogTrigger,
  Heading,
  OverlayArrow,
  Popover as AriaPopover,
  type PopoverProps as AriaPopoverProps,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { cx, defined } from '../../utils/cx';
import { Button } from '../Button';
import { useModalDepth } from '../Dialog/modalContext';
import { SkeletonText, useDelayedLoading } from '../Skeleton';
import { firstFieldIn, isTabPastEdge, tabbablesIn } from './focusables';
import styles from './Popover.module.css';

/** Width 240 / 320 / 400 px; `filters` 480 px (Part 2 §4.16). */
export type PopoverSize = 'sm' | 'md' | 'lg' | 'filters';
export type PopoverStatus = 'ready' | 'loading' | 'error';

export interface PopoverContentProps {
  /** Header title (also the dialog's accessible name). Without it pass `aria-label`. */
  title?: string;
  'aria-label'?: string;
  children?: ReactNode;
  footer?: ReactNode;
  size?: PopoverSize;
  /** `loading` shows a skeleton after 150 ms; `error` shows «Δεν ήταν δυνατή η φόρτωση» + Retry. */
  status?: PopoverStatus;
  /** Skeleton matching the final layout (defaults to three text lines). */
  loadingFallback?: ReactNode;
  errorMessage?: string;
  onRetry?: () => void;
  /** Hides the header × (the popover still closes with Esc and outside clicks). */
  hideClose?: boolean;
}

export interface PopoverProps extends PopoverContentProps {
  /** The pressable trigger (a `Button`); receives `aria-haspopup="dialog"` and `aria-expanded`. */
  trigger: ReactElement;
  placement?: AriaPopoverProps['placement'];
  showArrow?: boolean;
  isOpen?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
  className?: string;
}

function StatusBody({
  status,
  loadingFallback,
  errorMessage,
  onRetry,
  children,
}: Pick<
  PopoverContentProps,
  'status' | 'loadingFallback' | 'errorMessage' | 'onRetry' | 'children'
>) {
  const { t } = useTranslation('ds');
  const showSkeleton = useDelayedLoading(status === 'loading');
  if (status === 'loading') {
    return (
      <div className={cx(styles.body)} aria-busy="true">
        <span className="ds-visually-hidden">{t('popover.loading')}</span>
        {showSkeleton ? (loadingFallback ?? <SkeletonText lines={3} />) : null}
      </div>
    );
  }
  if (status === 'error') {
    return (
      <div className={cx(styles.body, styles.error)} role="alert">
        <p>{errorMessage ?? t('popover.loadError')}</p>
        {onRetry ? (
          <Button size="sm" onPress={onRetry}>
            {t('popover.retry')}
          </Button>
        ) : null}
      </div>
    );
  }
  return <div className={cx(styles.body)}>{children}</div>;
}

/** Moves focus to the first focusable element once the popover dialog has mounted (Part 2 §4.16). */
function FocusFirst({ rootRef }: { rootRef: RefObject<HTMLElement | null> }) {
  useEffect(() => {
    const root = rootRef.current;
    if (!root) return;
    const target = firstFieldIn(root) ?? tabbablesIn(root)[0];
    target?.focus();
  }, [rootRef]);
  return null;
}

/**
 * Popover content (a non-modal `role=dialog`) for use inside any RAC trigger. Tab past the last element
 * (or Shift+Tab before the first) closes it; Esc closes it and focus returns to the trigger.
 */
export function PopoverDialog({
  title,
  'aria-label': ariaLabel,
  children,
  footer,
  status = 'ready',
  loadingFallback,
  errorMessage,
  onRetry,
  hideClose = false,
}: PopoverContentProps) {
  const { t } = useTranslation('ds');
  const dialogRef = useRef<HTMLElement | null>(null);
  return (
    <AriaDialog
      ref={dialogRef}
      className={cx(styles.dialog)}
      {...(title ? {} : defined({ 'aria-label': ariaLabel }))}
    >
      {({ close }) => (
        <div
          className={cx(styles.frame)}
          onKeyDown={(event) => {
            const root = dialogRef.current;
            if (root && isTabPastEdge(event, root)) {
              event.preventDefault();
              event.stopPropagation();
              close();
            }
          }}
        >
          <FocusFirst rootRef={dialogRef} />
          {title ? (
            <div className={cx(styles.header)}>
              <Heading slot="title" className={cx(styles.title)}>
                {title}
              </Heading>
              {hideClose ? null : (
                <Button
                  variant="ghost"
                  size="sm"
                  icon={X}
                  label={t('popover.close')}
                  onPress={close}
                />
              )}
            </div>
          ) : null}
          <StatusBody status={status} {...defined({ loadingFallback, errorMessage, onRetry })}>
            {children}
          </StatusBody>
          {footer ? <div className={cx(styles.footer)}>{footer}</div> : null}
        </div>
      )}
    </AriaDialog>
  );
}

/**
 * Popover (Part 2 §4.16) on RAC `DialogTrigger` + `Popover` + `Dialog`: `material.popover` glass (solid
 * inside a modal), `radius.lg`, `elevation.3`, optional header (title + ×), body, footer; MI-28 open.
 */
export function Popover({
  trigger,
  placement = 'bottom start',
  showArrow = false,
  isOpen,
  defaultOpen,
  onOpenChange,
  size = 'md',
  className,
  ...content
}: PopoverProps) {
  const inModal = useModalDepth() > 0;
  return (
    <DialogTrigger {...defined({ isOpen, defaultOpen, onOpenChange })}>
      {/* React Aria omits aria-haspopup for dialogs; the guide asks for it (Part 2 §4.16). */}
      {cloneElement(trigger as ReactElement<{ 'aria-haspopup'?: string }>, {
        'aria-haspopup': 'dialog',
      })}
      <AriaPopover
        className={cx(styles.popover, className)}
        placement={placement}
        offset={8}
        data-material="popover"
        data-solid={inModal || undefined}
        data-size={size}
      >
        {showArrow ? (
          <OverlayArrow className={cx(styles.arrow)}>
            <svg width={12} height={8} viewBox="0 0 12 8" aria-hidden="true">
              <path d="M0 0 L6 6 L12 0" />
            </svg>
          </OverlayArrow>
        ) : null}
        <PopoverDialog {...content} />
      </AriaPopover>
    </DialogTrigger>
  );
}
