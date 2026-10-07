import type { LucideIcon } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode, type Ref } from 'react';
import { Button as AriaButton, type ButtonProps as AriaButtonProps } from 'react-aria-components';

import { Icon } from '../../icons';
import { durations } from '../../tokens';
import { cx, defined } from '../../utils/cx';
import { Kbd } from '../Kbd';
import { Spinner } from '../Spinner';
import { Tooltip } from '../Tooltip';
import styles from './Button.module.css';

/**
 * Button variants (Part 2 §4.1, v3 option 17 / D-FE-04):
 * - `primary`: surface fill with the 1.5 px violet→blue→teal gradient border; one per region.
 * - `commit`: primary plus the resting glow; one per view (Δέσμευση, Έκδοση, Έγκριση πληρωμής…).
 * - `secondary`, `ghost` (toolbars, rows), `danger` (only inside a confirmation dialog),
 *   `danger-ghost` (destructive entry points), `link`, `ai` (accept AI suggestion).
 */
export type ButtonVariant =
  'primary' | 'commit' | 'secondary' | 'ghost' | 'danger' | 'danger-ghost' | 'link' | 'ai';
export type ButtonSize = 'sm' | 'md' | 'lg';

type AriaPassThrough = Omit<
  AriaButtonProps,
  | 'children'
  | 'className'
  | 'style'
  | 'isDisabled'
  | 'isPending'
  | 'aria-label'
  | 'aria-describedby'
>;

interface ButtonBaseProps extends AriaPassThrough {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** 16 px leading icon (14 px at `sm`). */
  icon?: LucideIcon;
  trailingIcon?: LucideIcon;
  /** Shortcut, e.g. `Mod+Enter`. Shown as a chip inside `lg` buttons and in the tooltip otherwise. */
  shortcut?: string;
  /**
   * Loading: same-size spinner, width locked, presses ignored (double-submit guard). Uses React Aria's
   * pending state: `aria-disabled`, focus kept, and the change announced to screen readers.
   */
  isLoading?: boolean;
  /**
   * Disabled with a reason: the button stays focusable with `aria-disabled`, the reason is a tooltip and
   * the accessible description, and presses are swallowed (D-FE-08). Prefer this to `isDisabled`.
   */
  disabledReason?: string;
  /** Disabled without a reason (native `disabled`; leaves the tab order). Use only when no reason applies. */
  isDisabled?: boolean;
  /** Toggle buttons expose `aria-pressed`. */
  isPressed?: boolean;
  /** Extra description id(s) for `aria-describedby`. */
  'aria-describedby'?: string;
  ref?: Ref<HTMLButtonElement>;
}

/** Text button. */
interface TextButtonProps extends ButtonBaseProps {
  children: ReactNode;
  'aria-label'?: string;
}

/** Icon-only button: square, `aria-label` = `label`, tooltip with the label and shortcut. */
interface IconButtonProps extends ButtonBaseProps {
  icon: LucideIcon;
  label: string;
  children?: never;
  'aria-label'?: never;
}

export type ButtonProps = TextButtonProps | IconButtonProps;

function iconSizeFor(size: ButtonSize) {
  return size === 'sm' ? 14 : 16;
}

/**
 * MI-67: when a commit button turns enabled (its last blocker cleared), a sheen sweeps once.
 * Returns true for the duration of the sweep.
 */
function useCommitSweep(enabled: boolean, active: boolean): boolean {
  const previous = useRef(enabled);
  const [sweeping, setSweeping] = useState(false);
  useEffect(() => {
    const wasEnabled = previous.current;
    previous.current = enabled;
    if (!active || wasEnabled || !enabled) return;
    setSweeping(true);
    const id = setTimeout(() => {
      setSweeping(false);
    }, durations.sweep);
    return () => {
      clearTimeout(id);
    };
  }, [enabled, active]);
  return sweeping;
}

export function Button(props: ButtonProps) {
  const {
    variant = 'secondary',
    size = 'md',
    icon,
    trailingIcon,
    shortcut,
    isLoading = false,
    disabledReason,
    isDisabled = false,
    isPressed,
    onPress,
    onPressStart,
    onPressEnd,
    onPressChange,
    onPressUp,
    ref,
    ...rest
  } = props;
  const label = 'label' in props ? props.label : undefined;
  const children = 'label' in props ? undefined : props.children;
  const iconOnly = label !== undefined;
  const reasonId = useId();
  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const sweeping = useCommitSweep(!softDisabled && !isDisabled, variant === 'commit');

  // Strip the props we handle ourselves from the pass-through.
  const passThrough: Record<string, unknown> = { ...rest };
  delete passThrough.label;
  delete passThrough.children;
  delete passThrough['aria-label'];
  delete passThrough['aria-describedby'];

  const describedBy = [softDisabled ? reasonId : undefined, props['aria-describedby']]
    .filter(Boolean)
    .join(' ');
  const blocked = softDisabled || isLoading;
  const iconSize = iconSizeFor(size);
  const showChip = shortcut !== undefined && size === 'lg' && !iconOnly;

  const button = (
    <AriaButton
      {...passThrough}
      ref={ref}
      className={cx(styles.button, iconOnly && styles.iconOnly)}
      data-variant={variant}
      data-size={size}
      data-soft-disabled={softDisabled || undefined}
      data-sweep={sweeping || undefined}
      isDisabled={isDisabled}
      isPending={isLoading}
      {...(iconOnly
        ? { 'aria-label': label }
        : props['aria-label']
          ? { 'aria-label': props['aria-label'] }
          : {})}
      {...(describedBy ? { 'aria-describedby': describedBy } : {})}
      {...(softDisabled ? { 'aria-disabled': true } : {})}
      {...(isPressed === undefined ? {} : { 'aria-pressed': isPressed })}
      {...(blocked ? {} : defined({ onPress, onPressStart, onPressEnd, onPressChange, onPressUp }))}
    >
      <span className={styles.content}>
        {icon ? <Icon icon={icon} size={iconSize} /> : null}
        {children !== undefined ? <span className={styles.label}>{children}</span> : null}
        {trailingIcon ? <Icon icon={trailingIcon} size={iconSize} /> : null}
        {showChip ? <Kbd shortcut={shortcut} tone="subtle" /> : null}
      </span>
      {isLoading ? (
        <span className={styles.spinner}>
          <Spinner size={iconSize === 14 ? 14 : 16} delayed={false} label={null} />
        </span>
      ) : null}
    </AriaButton>
  );
  // The reason is the button's description even when the tooltip is closed (aria-describedby may point at
  // hidden content). It sits outside the button so it does not become part of the accessible name.
  const reason = softDisabled ? (
    <span id={reasonId} hidden>
      {disabledReason}
    </span>
  ) : null;

  const tooltip = softDisabled ? disabledReason : iconOnly ? label : undefined;
  const tooltipShortcut = shortcut !== undefined && !showChip ? shortcut : undefined;
  if (tooltip === undefined && tooltipShortcut === undefined) return button;
  return (
    <>
      <Tooltip
        content={tooltip ?? children}
        {...(tooltipShortcut ? { shortcut: tooltipShortcut } : {})}
      >
        {button}
      </Tooltip>
      {reason}
    </>
  );
}
