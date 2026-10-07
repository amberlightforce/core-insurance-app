import { X } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { Overlay, useLandmark } from 'react-aria';
import { Dialog as AriaDialog, Heading, Modal, ModalOverlay } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { durations } from '../../tokens';
import { cx } from '../../utils/cx';
import { Banner } from '../Banner';
import { Button } from '../Button';
import { ModalDepthContext, useModalDepth } from '../Dialog/modalContext';
import { SkeletonText, useDelayedLoading } from '../Skeleton';
import styles from './Drawer.module.css';
import { usePresence } from './usePresence';

/** 320 navigation · 360 filters · 400 notifications (Part 2 §4.19). */
export type DrawerWidth = 320 | 360 | 400;
export type DrawerStatus = 'ready' | 'loading' | 'error';

export interface DrawerProps {
  isOpen: boolean;
  onOpenChange: (isOpen: boolean) => void;
  title: string;
  children?: ReactNode;
  /** Sticky footer, e.g. «Εκκαθάριση» / «Εφαρμογή (124)». */
  footer?: ReactNode;
  /** Extra header buttons left of ×. */
  headerActions?: ReactNode;
  /**
   * Modal drawers trap focus behind a scrim (`role=dialog aria-modal`). Non-modal drawers are a labelled
   * `complementary` landmark reachable with F6, without a scrim or a focus trap.
   */
  isModal?: boolean;
  width?: DrawerWidth;
  placement?: 'end' | 'start';
  status?: DrawerStatus;
  loadingFallback?: ReactNode;
  errorMessage?: string;
  onRetry?: () => void;
}

interface ContentProps extends DrawerProps {
  titleId?: string;
  close: () => void;
  headingSlot: boolean;
}

function DrawerContent({
  title,
  children,
  footer,
  headerActions,
  status = 'ready',
  loadingFallback,
  errorMessage,
  onRetry,
  close,
  titleId,
  headingSlot,
}: ContentProps) {
  const { t } = useTranslation('ds');
  const showSkeleton = useDelayedLoading(status === 'loading');
  return (
    <>
      <div className={styles.header}>
        {headingSlot ? (
          <Heading slot="title" className={cx(styles.title)}>
            {title}
          </Heading>
        ) : (
          <h2 id={titleId} className={styles.title}>
            {title}
          </h2>
        )}
        {headerActions}
        <Button variant="ghost" size="sm" icon={X} label={t('drawer.close')} onPress={close} />
      </div>
      <div className={styles.body} aria-busy={status === 'loading' || undefined}>
        {status === 'loading' ? (
          <>
            <span className="ds-visually-hidden">{t('skeleton.loading')}</span>
            {showSkeleton ? (loadingFallback ?? <SkeletonText lines={6} />) : null}
          </>
        ) : null}
        {status === 'error' ? (
          <Banner
            variant="danger"
            title={errorMessage ?? t('drawer.loadError')}
            actions={
              onRetry ? (
                <Button variant="link" onPress={onRetry}>
                  {t('drawer.retry')}
                </Button>
              ) : undefined
            }
          />
        ) : null}
        {status === 'ready' ? children : null}
      </div>
      {footer ? <div className={styles.footer}>{footer}</div> : null}
    </>
  );
}

function NonModalDrawer(props: DrawerProps) {
  const { isOpen, onOpenChange, width = 360, placement = 'end' } = props;
  const titleId = useId();
  const ref = useRef<HTMLElement | null>(null);
  const { mounted, exiting } = usePresence(isOpen, durations.transitionMd);
  const { landmarkProps } = useLandmark(
    { role: 'complementary', 'aria-labelledby': titleId, focus: () => ref.current?.focus() },
    ref,
  );

  // Move focus into the panel when it opens; FocusScope (inside Overlay) returns it on close.
  useEffect(() => {
    if (isOpen) ref.current?.focus();
  }, [isOpen]);

  if (!mounted) return null;
  return (
    <Overlay>
      <section
        {...landmarkProps}
        ref={ref}
        tabIndex={-1}
        className={styles.panel}
        data-material="overlay"
        data-width={width}
        data-placement={placement}
        data-modal="false"
        data-exiting={exiting || undefined}
        onKeyDown={(event) => {
          if (event.key === 'Escape') {
            event.stopPropagation();
            onOpenChange(false);
          }
        }}
      >
        <DrawerContent
          {...props}
          titleId={titleId}
          headingSlot={false}
          close={() => {
            onOpenChange(false);
          }}
        />
      </section>
    </Overlay>
  );
}

/**
 * Drawer (Part 2 §4.19): an edge panel with a 48 px header, a scrolling body and a sticky footer.
 * Modal: RAC `ModalOverlay` + `Modal` + `Dialog` (focus trap, scrim). Non-modal (notifications):
 * a `role="complementary"` landmark reachable by F6, Esc closes, no trap. MI-30 slide.
 */
export function Drawer(props: DrawerProps) {
  const { isModal = true, isOpen, onOpenChange, width = 360, placement = 'end' } = props;
  const depth = useModalDepth();
  if (!isModal) return <NonModalDrawer {...props} />;
  return (
    <ModalOverlay
      className={cx(styles.overlay)}
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      isDismissable
    >
      <Modal
        className={cx(styles.panel)}
        data-material="overlay"
        data-width={width}
        data-placement={placement}
        data-modal="true"
      >
        <ModalDepthContext value={depth + 1}>
          <AriaDialog className={cx(styles.dialog)}>
            {({ close }) => <DrawerContent {...props} close={close} headingSlot />}
          </AriaDialog>
        </ModalDepthContext>
      </Modal>
    </ModalOverlay>
  );
}
