import type { ReactElement, ReactNode } from 'react';
import {
  OverlayArrow,
  Tooltip as AriaTooltip,
  TooltipTrigger,
  type TooltipProps as AriaTooltipProps,
} from 'react-aria-components';

import { durations } from '../../tokens';
import { cx } from '../../utils/cx';
import { Kbd } from '../Kbd';
import styles from './Tooltip.module.css';

export interface TooltipProps {
  /** Tooltip text: icon labels, disabled reasons, abbreviations, truncated text. Never task-critical. */
  content: ReactNode;
  /** Optional shortcut chip, e.g. `Mod+K`. */
  shortcut?: string;
  /** A focusable React Aria trigger (Button, Link, `Focusable`). */
  children: ReactElement;
  placement?: AriaTooltipProps['placement'];
  /** Controlled open state (rare; for tests and stories). */
  isOpen?: boolean;
  isDisabled?: boolean;
}

/**
 * Tooltip (Part 2 §4.15): solid inverse surface (never glass), 400 ms hover delay, 0 ms on keyboard focus,
 * warm for 1,500 ms, Esc dismisses without moving focus. React Aria wires `aria-describedby`.
 */
export function Tooltip({
  content,
  shortcut,
  children,
  placement = 'top',
  isOpen,
  isDisabled,
}: TooltipProps) {
  return (
    <TooltipTrigger
      delay={durations.delayTooltip}
      closeDelay={0}
      {...(isOpen === undefined ? {} : { isOpen })}
      {...(isDisabled === undefined ? {} : { isDisabled })}
    >
      {children}
      <AriaTooltip className={cx(styles.tooltip)} placement={placement} offset={8}>
        <OverlayArrow className={cx(styles.arrow)}>
          <svg width={8} height={8} viewBox="0 0 8 8" aria-hidden="true">
            <path d="M0 0 L4 4 L8 0" />
          </svg>
        </OverlayArrow>
        <span className={styles.content}>
          {content}
          {shortcut ? <Kbd shortcut={shortcut} tone="inverse" /> : null}
        </span>
      </AriaTooltip>
    </TooltipTrigger>
  );
}
