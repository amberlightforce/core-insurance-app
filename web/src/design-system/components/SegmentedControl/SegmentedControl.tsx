import type { LucideIcon } from 'lucide-react';
import { useId, useRef, useState, type CSSProperties } from 'react';
import { RadioButton, RadioField, RadioGroup } from 'react-aria-components';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { hasText, useAriaAttribute, useHoverFocus } from '../TextField/fieldHooks';
import { AnchoredTooltip, FieldLabel } from '../TextField/FormField';
import styles from './SegmentedControl.module.css';

export interface SegmentOption {
  id: string;
  label: string;
  icon?: LucideIcon;
  /** Disabled with a reason: still reachable with the arrows, never selected, reason in a tooltip. */
  disabledReason?: string;
}

export interface SegmentedControlProps {
  /** Visible label above the control; or pass `aria-label`. */
  label?: string;
  'aria-label'?: string;
  /** 2–5 exclusive views or modes. */
  options: SegmentOption[];
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  name?: string;
  size?: 'sm' | 'md' | 'lg';
  isDisabled?: boolean;
  /** Read-only: the selected label as text. */
  isReadOnly?: boolean;
}

/**
 * Segmented control (Part 2 §4.12): `radiogroup` semantics on React Aria `RadioGroup`, arrows move and
 * select, a sliding thumb (MI-13, CSS transform) under equal-width segments. One segment may be disabled
 * with a reason.
 */
export function SegmentedControl({
  label,
  'aria-label': ariaLabel,
  options,
  value,
  defaultValue,
  onChange,
  name,
  size = 'md',
  isDisabled = false,
  isReadOnly = false,
}: SegmentedControlProps) {
  const [inner, setInner] = useState(defaultValue ?? options[0]?.id ?? '');
  const selected = value ?? inner;
  const index = options.findIndex((option) => option.id === selected);

  if (isReadOnly) {
    const current = options[index];
    return (
      <div className={styles.readOnly}>
        {label ? <span className={styles.readOnlyLabel}>{label}</span> : null}
        <span>{current?.label ?? '—'}</span>
      </div>
    );
  }

  const handleChange = (next: string) => {
    const option = options.find((o) => o.id === next);
    if (!option || hasText(option.disabledReason)) return;
    if (value === undefined) setInner(next);
    onChange?.(next);
  };

  const thumbStyle = {
    '--_count': String(options.length),
    '--_index': String(Math.max(index, 0)),
  } as CSSProperties;

  return (
    <RadioGroup
      className={cx(styles.root)}
      orientation="horizontal"
      value={selected}
      onChange={handleChange}
      isDisabled={isDisabled}
      {...(name ? { name } : {})}
      {...(ariaLabel ? { 'aria-label': ariaLabel } : {})}
    >
      {label ? (
        <FieldLabel elementType="span" labelText={label}>
          {label}
        </FieldLabel>
      ) : null}
      <div className={styles.track} data-size={size} style={thumbStyle}>
        {index >= 0 ? <span className={styles.thumb} aria-hidden="true" /> : null}
        {options.map((option) => (
          <Segment key={option.id} option={option} />
        ))}
      </div>
    </RadioGroup>
  );
}

function Segment({ option }: { option: SegmentOption }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const labelRef = useRef<HTMLLabelElement>(null);
  const reasonId = useId();
  const hover = useHoverFocus();
  const softDisabled = hasText(option.disabledReason);
  useAriaAttribute(inputRef, 'aria-disabled', softDisabled ? 'true' : undefined);
  return (
    <>
      <RadioField
        inputRef={inputRef}
        value={option.id}
        className={cx(styles.segmentField)}
        {...(softDisabled
          ? {
              'aria-describedby': reasonId,
              onFocus: hover.handlers.onFocus,
              onBlur: hover.handlers.onBlur,
              onKeyDown: (event: { key: string; continuePropagation: () => void }) => {
                hover.handlers.onKeyDown(event);
                event.continuePropagation();
              },
            }
          : {})}
      >
        <RadioButton
          ref={labelRef}
          className={cx(styles.segment)}
          data-soft-disabled={softDisabled || undefined}
          {...(softDisabled
            ? {
                onHoverStart: hover.handlers.onPointerEnter,
                onHoverEnd: hover.handlers.onPointerLeave,
              }
            : {})}
        >
          {option.icon ? <Icon icon={option.icon} size={16} /> : null}
          <span>{option.label}</span>
        </RadioButton>
      </RadioField>
      {softDisabled ? (
        <>
          <span id={reasonId} hidden>
            {option.disabledReason}
          </span>
          <AnchoredTooltip triggerRef={labelRef} isOpen={hover.active}>
            {option.disabledReason}
          </AnchoredTooltip>
        </>
      ) : null}
    </>
  );
}
