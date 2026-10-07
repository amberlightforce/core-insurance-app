import { X, type LucideIcon } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Dialog as AriaDialog,
  Heading,
  Modal,
  ModalOverlay,
  type DialogProps as AriaDialogProps,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { Banner } from '../Banner';
import { Button, type ButtonVariant } from '../Button';
import { matchesShortcut } from '../Kbd';
import { firstFieldIn } from '../Popover/focusables';
import styles from './Dialog.module.css';
import { ModalDepthContext, useModalDepth } from './modalContext';

/** sm 400 · md 560 · lg 720 · xl min(1040 px, 92vw) (Part 2 §4.18). */
export type DialogSize = 'sm' | 'md' | 'lg' | 'xl';
export type DialogTone = 'brand' | 'info' | 'success' | 'warning' | 'danger' | 'neutral';

export interface DialogAction {
  label: string;
  /** May return a promise: the dialog shows the busy state until it settles and closes on success. */
  onAction: () => unknown;
  variant?: ButtonVariant;
  icon?: LucideIcon;
  /** Keeps the button focusable with `aria-disabled` and shows the reason (Button pattern). */
  disabledReason?: string;
}

export interface DialogProps {
  title: string;
  /** Body (`type.body`, max 72 ch); scrolls when taller than the viewport allows. */
  children?: ReactNode;
  /** 20 px header icon in a 36 px tinted circle. */
  icon?: LucideIcon;
  tone?: DialogTone;
  size?: DialogSize;
  isOpen?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
  /** Rendered last in the footer. */
  primaryAction?: DialogAction;
  /** Rendered between «Άκυρο» and the primary action. */
  secondaryActions?: DialogAction[];
  /** Label of the first footer button, which closes the dialog (default «Άκυρο»). */
  cancelLabel?: string;
  onCancel?: () => void;
  hideCancel?: boolean;
  /** Controlled busy state; actions returning a promise also set it automatically. */
  isBusy?: boolean;
  /** Danger banner at the top of the body; input is preserved. */
  error?: string;
  /**
   * Destructive confirmations: `role="alertdialog"`, «Άκυρο» first and focused, scrim click and
   * Ctrl/⌘+Enter disabled. Defaults to true when the primary action is `danger`.
   */
  isDestructive?: boolean;
  /** `auto` (D-FE-17): first field, else «Άκυρο» for destructive dialogs. `cancel`: always «Άκυρο». */
  initialFocus?: 'auto' | 'cancel';
  /** Close after the primary or a secondary action succeeds (default true). */
  closeOnAction?: boolean;
  /** Clicking the scrim closes the dialog (default true unless destructive). */
  isDismissable?: boolean;
  'aria-describedby'?: string;
}

interface LayoutProps extends DialogProps {
  close: () => void;
  pending: boolean;
  setPending: (pending: boolean) => void;
}

function scrollEdges(body: HTMLElement): { top: boolean; bottom: boolean } {
  return {
    top: body.scrollTop > 0,
    bottom: body.scrollHeight - body.clientHeight - body.scrollTop > 1,
  };
}

function DialogLayout({
  title,
  children,
  icon,
  tone = 'brand',
  primaryAction,
  secondaryActions = [],
  cancelLabel,
  onCancel,
  hideCancel = false,
  isBusy: busyProp = false,
  error: errorProp,
  isDestructive: destructive = false,
  initialFocus = 'auto',
  closeOnAction = true,
  close,
  pending,
  setPending,
}: LayoutProps) {
  const { t } = useTranslation('ds');
  const [failed, setFailed] = useState(false);
  const [scroll, setScroll] = useState({ top: false, bottom: false });
  const bodyRef = useRef<HTMLDivElement | null>(null);
  const cancelRef = useRef<HTMLButtonElement | null>(null);
  const busy = busyProp || pending;
  const error = errorProp ?? (failed ? t('dialog.genericError') : undefined);
  const busyReason = busy ? t('dialog.busy') : undefined;

  // Initial focus (Part 2 §4.18, D-FE-17): the first field when the dialog has one; otherwise «Άκυρο»
  // for destructive dialogs (the least destructive action). `cancel` forces «Άκυρο».
  useEffect(() => {
    const body = bodyRef.current;
    const field = body ? firstFieldIn(body) : null;
    if (initialFocus === 'cancel') cancelRef.current?.focus();
    else if (field) field.focus();
    else if (destructive) cancelRef.current?.focus();
  }, [initialFocus, destructive]);

  // Header and footer borders appear only when the body scrolls.
  const measure = () => {
    const body = bodyRef.current;
    if (body) setScroll(scrollEdges(body));
  };
  useEffect(() => {
    const id = requestAnimationFrame(() => {
      const body = bodyRef.current;
      if (body) setScroll(scrollEdges(body));
    });
    return () => {
      cancelAnimationFrame(id);
    };
  }, []);

  const run = (action: DialogAction) => {
    if (busy || action.disabledReason) return;
    setFailed(false);
    const result = action.onAction();
    if (result instanceof Promise) {
      setPending(true);
      result.then(
        () => {
          setPending(false);
          if (closeOnAction) close();
        },
        () => {
          setPending(false);
          setFailed(true);
        },
      );
    } else if (closeOnAction) {
      close();
    }
  };

  return (
    <div
      className={styles.frame}
      onKeyDown={(event) => {
        // Busy: Esc must not close the dialog (Part 2 §4.18).
        if (busy && event.key === 'Escape') {
          event.preventDefault();
          event.stopPropagation();
          return;
        }
        if (
          primaryAction &&
          !destructive &&
          primaryAction.variant !== 'danger' &&
          matchesShortcut(event, 'Mod+Enter')
        ) {
          event.preventDefault();
          run(primaryAction);
        }
      }}
    >
      <div className={styles.header} data-scrolled={scroll.top || undefined}>
        {icon ? (
          <span className={styles.headerIcon} data-tone={tone}>
            <Icon icon={icon} size={20} />
          </span>
        ) : null}
        <Heading slot="title" className={cx(styles.title)}>
          {title}
        </Heading>
        <Button
          variant="ghost"
          icon={X}
          label={t('dialog.close')}
          {...(busyReason ? { disabledReason: busyReason } : { onPress: close })}
        />
      </div>
      <div ref={bodyRef} className={styles.body} onScroll={measure}>
        {error ? (
          <Banner variant="danger" title={t('dialog.errorTitle')}>
            {error}
          </Banner>
        ) : null}
        {children}
      </div>
      {primaryAction || secondaryActions.length > 0 || !hideCancel ? (
        <div className={styles.footer} data-scrolled={scroll.bottom || undefined}>
          <span className={styles.busy} role="status">
            {busy ? t('dialog.busy') : null}
          </span>
          {hideCancel ? null : (
            <Button
              ref={cancelRef}
              variant="secondary"
              {...(busyReason
                ? { disabledReason: busyReason }
                : {
                    onPress: () => {
                      onCancel?.();
                      close();
                    },
                  })}
            >
              {cancelLabel ?? t('dialog.cancel')}
            </Button>
          )}
          {secondaryActions.map((action) => {
            const reason = busyReason ?? action.disabledReason;
            return (
              <Button
                key={action.label}
                variant={action.variant ?? 'secondary'}
                {...defined({ icon: action.icon })}
                {...(reason
                  ? { disabledReason: reason }
                  : {
                      onPress: () => {
                        run(action);
                      },
                    })}
              >
                {action.label}
              </Button>
            );
          })}
          {primaryAction ? (
            <Button
              variant={primaryAction.variant ?? 'primary'}
              isLoading={busy}
              {...defined({
                icon: primaryAction.icon,
                disabledReason: primaryAction.disabledReason,
              })}
              onPress={() => {
                run(primaryAction);
              }}
            >
              {primaryAction.label}
            </Button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

/**
 * Modal dialog (Part 2 §4.18) on RAC `ModalOverlay` + `Modal` + `Dialog`: scrim, `material.overlay`,
 * `radius.xl`, `elevation.4`; header icon + `heading.2` title + ghost ×; scrolling body; footer with the
 * primary last. Busy blocks Esc and the scrim; Ctrl/⌘+Enter runs a non-destructive primary. Use inside a
 * RAC `DialogTrigger` or control it with `isOpen`. At most two modals stack.
 */
export function Dialog(props: DialogProps) {
  const {
    size = 'md',
    isOpen,
    defaultOpen,
    onOpenChange,
    isBusy = false,
    primaryAction,
    'aria-describedby': describedBy,
  } = props;
  const depth = useModalDepth();
  const [pending, setPending] = useState(false);
  const busy = isBusy || pending;
  const destructive = props.isDestructive ?? primaryAction?.variant === 'danger';
  const dismissable = (props.isDismissable ?? !destructive) && !busy;
  const role: AriaDialogProps['role'] = destructive ? 'alertdialog' : 'dialog';

  return (
    <ModalOverlay
      className={cx(styles.overlay)}
      data-depth={depth + 1}
      isDismissable={dismissable}
      isKeyboardDismissDisabled={busy}
      {...defined({ isOpen, defaultOpen, onOpenChange })}
    >
      <Modal className={cx(styles.modal)} data-material="overlay" data-size={size}>
        <ModalDepthContext value={depth + 1}>
          <AriaDialog
            className={cx(styles.dialog)}
            role={role}
            {...defined({ 'aria-describedby': describedBy })}
          >
            {({ close }) => (
              <DialogLayout
                {...props}
                isDestructive={destructive}
                close={close}
                pending={pending}
                setPending={setPending}
              />
            )}
          </AriaDialog>
        </ModalDepthContext>
      </Modal>
    </ModalOverlay>
  );
}
