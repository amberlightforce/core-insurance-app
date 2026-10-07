import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { mergeProps, useFocusRing, useHover, useLink } from 'react-aria';
import { Popover as AriaPopover } from 'react-aria-components';

import { cx, defined } from '../../utils/cx';
import { matchesShortcut } from '../Kbd';
import { useModalDepth } from '../Dialog/modalContext';
import type { PopoverSize, PopoverStatus } from './Popover';
import styles from './Popover.module.css';

/** Hover intent before a quick-look opens (Part 2 §4.16). */
const hoverCardOpenDelay = 500;
/** Grace period for moving the pointer from the link to the card. */
const hoverCardCloseDelay = 300;

export interface HoverCardProps {
  /** Link content, e.g. the mono record ID «ΑΣΦ-2026-004471». */
  label: ReactNode;
  href?: string;
  /** Enter or click opens the record. */
  onPress?: () => void;
  /** Ctrl/⌘+Enter on the link: open in a new tab. */
  onOpenInNewTab?: () => void;
  /** Accessible name of the card (the record title). */
  title: string;
  /** Mini record: title, mono ID, pill, 4 facts, last 2 activities, and the caller's actions. */
  children: ReactNode;
  /** Called when the card opens or closes (prefetch on open). */
  onOpenChange?: (isOpen: boolean) => void;
  status?: PopoverStatus;
  size?: PopoverSize;
  className?: string;
}

/**
 * Hover card / quick-look (Part 2 §4.16, MI-55), built on `useLink` + `useHover` + a non-modal RAC
 * `Popover`: opens after 500 ms of hover or with Space on the focused link, never takes focus, stays open
 * while the pointer is over the card, and closes with Esc or when the pointer leaves.
 */
export function HoverCard({
  label,
  href,
  onPress,
  onOpenInNewTab,
  title,
  children,
  onOpenChange,
  status = 'ready',
  size = 'md',
  className,
}: HoverCardProps) {
  const linkRef = useRef<HTMLAnchorElement | null>(null);
  const cardId = useId();
  const [isOpen, setIsOpen] = useState(false);
  const openTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const inModal = useModalDepth() > 0;

  const clearTimers = () => {
    clearTimeout(openTimer.current);
    clearTimeout(closeTimer.current);
  };

  const openRef = useRef(false);
  const setOpen = (next: boolean) => {
    clearTimers();
    if (openRef.current === next) return;
    openRef.current = next;
    setIsOpen(next);
    onOpenChange?.(next);
  };

  const scheduleOpen = () => {
    clearTimers();
    openTimer.current = setTimeout(() => {
      setOpen(true);
    }, hoverCardOpenDelay);
  };

  const scheduleClose = () => {
    clearTimers();
    closeTimer.current = setTimeout(() => {
      setOpen(false);
    }, hoverCardCloseDelay);
  };

  useEffect(
    () => () => {
      clearTimeout(openTimer.current);
      clearTimeout(closeTimer.current);
    },
    [],
  );

  const { linkProps } = useLink({ ...defined({ onPress }), elementType: 'a' }, linkRef);
  const { hoverProps: linkHover } = useHover({
    onHoverStart: scheduleOpen,
    onHoverEnd: scheduleClose,
  });
  const { hoverProps: cardHover } = useHover({
    onHoverStart: () => {
      clearTimers();
    },
    onHoverEnd: scheduleClose,
  });
  const { focusProps, isFocusVisible } = useFocusRing();

  return (
    <span
      className={cx(styles.hoverAnchor)}
      // Capture phase: the link's press handling would otherwise consume Ctrl/⌘+Enter.
      onKeyDownCapture={(event) => {
        if (event.key === ' ') {
          event.preventDefault();
          setOpen(!isOpen);
        } else if (event.key === 'Escape' && isOpen) {
          event.preventDefault();
          setOpen(false);
        } else if (onOpenInNewTab && matchesShortcut(event, 'Mod+Enter')) {
          event.preventDefault();
          event.stopPropagation();
          onOpenInNewTab();
        }
      }}
    >
      <a
        {...mergeProps(linkProps, linkHover, focusProps)}
        ref={linkRef}
        href={href}
        className={cx(styles.hoverLink, className)}
        data-focus-visible={isFocusVisible || undefined}
        aria-haspopup="dialog"
        aria-expanded={isOpen}
        aria-controls={isOpen ? cardId : undefined}
      >
        {label}
      </a>
      <AriaPopover
        triggerRef={linkRef}
        isOpen={isOpen}
        onOpenChange={setOpen}
        isNonModal
        placement="bottom start"
        offset={8}
        className={cx(styles.popover)}
        data-material="popover"
        data-solid={inModal || undefined}
        data-size={size}
      >
        <div
          {...cardHover}
          id={cardId}
          role="dialog"
          aria-label={title}
          aria-busy={status === 'loading' || undefined}
          className={cx(styles.frame, styles.hoverBody)}
        >
          {children}
        </div>
      </AriaPopover>
    </span>
  );
}
