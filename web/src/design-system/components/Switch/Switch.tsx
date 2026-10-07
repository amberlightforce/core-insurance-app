import { CircleCheck, CircleMinus } from 'lucide-react';
import { useId, useRef, useState, type ReactNode, type Ref } from 'react';
import { SwitchButton, SwitchField, Text } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Spinner } from '../Spinner';
import { hasText, useAriaAttribute, useHoverFocus, useMergedRef } from '../TextField/fieldHooks';
import { AnchoredTooltip } from '../TextField/FormField';
import styles from './Switch.module.css';

export interface SwitchProps {
  /** Label, on the left of the track. */
  children: ReactNode;
  /** Plain-text label for announcements when `children` is not a string. */
  labelText?: string;
  name?: string;
  value?: string;
  isSelected?: boolean;
  defaultSelected?: boolean;
  /** Called after the change took effect (immediately, or after `onChangeAsync` resolved). */
  onChange?: (isSelected: boolean) => void;
  /**
   * Server-persisted setting: the switch flips at once, shows a spinner in the thumb while the promise is
   * pending and reverts (with an assertive announcement and `onError`) when it rejects.
   */
  onChangeAsync?: (isSelected: boolean) => Promise<unknown>;
  onError?: (error: unknown) => void;
  /** Externally driven pending state (spinner in the thumb, toggling paused). */
  isPending?: boolean;
  /** Shows «Ενεργό» / «Ανενεργό» after the track. */
  showStateText?: boolean;
  description?: string;
  /** Disabled without a reason (leaves the tab order). Prefer `disabledReason`. */
  isDisabled?: boolean;
  /** Disabled with a reason: focusable, `aria-disabled`, reason tooltip and description. */
  disabledReason?: string;
  /** Read-only: the label and the state as text with `circle-check` / `circle-minus`. */
  isReadOnly?: boolean;
  /** Ref to the native input. */
  ref?: Ref<HTMLInputElement>;
}

/**
 * Switch (Part 2 §4.10) on React Aria `Switch`, for immediate-effect settings only (never in submit-later
 * forms). Track 32×18 (44×24 touch), MI-06 throw. Space or Enter toggles; `role="switch"` + `aria-checked`.
 */
export function Switch({
  children,
  labelText,
  name,
  value,
  isSelected,
  defaultSelected = false,
  onChange,
  onChangeAsync,
  onError,
  isPending = false,
  showStateText = false,
  description,
  isDisabled = false,
  disabledReason,
  isReadOnly = false,
  ref,
}: SwitchProps) {
  const { t } = useTranslation('ds');
  const base = useId();
  const reasonId = `${base}-reason`;
  const [inner, setInner] = useState(defaultSelected);
  const [optimistic, setOptimistic] = useState<boolean | null>(null);
  const [saving, setSaving] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const setInputRef = useMergedRef(inputRef, ref);
  const labelRef = useRef<HTMLLabelElement>(null);
  const reasonHover = useHoverFocus();
  const softDisabled = hasText(disabledReason);
  const pending = isPending || saving;
  const committed = isSelected ?? inner;
  const shown = optimistic ?? committed;
  const label = labelText ?? (typeof children === 'string' ? children : '');

  useAriaAttribute(inputRef, 'aria-disabled', softDisabled ? 'true' : undefined);
  useAriaAttribute(inputRef, 'aria-busy', pending ? 'true' : undefined);

  if (isReadOnly) {
    return (
      <div className={styles.readOnly}>
        <span className={styles.label}>{children}</span>
        <span className={styles.readOnlyState}>
          <Icon icon={shown ? CircleCheck : CircleMinus} size={16} />
          {shown ? t('switch.on') : t('switch.off')}
        </span>
      </div>
    );
  }

  const commit = (next: boolean) => {
    if (isSelected === undefined) setInner(next);
    onChange?.(next);
  };

  const handleChange = (next: boolean) => {
    if (softDisabled || pending) return;
    if (!onChangeAsync) {
      commit(next);
      return;
    }
    setOptimistic(next);
    setSaving(true);
    onChangeAsync(next).then(
      () => {
        setSaving(false);
        setOptimistic(null);
        commit(next);
      },
      (error: unknown) => {
        setSaving(false);
        setOptimistic(null);
        announce(t('switch.failed', { label }), 'assertive');
        onError?.(error);
      },
    );
  };

  return (
    <>
      <SwitchField
        inputRef={setInputRef}
        className={cx(styles.field)}
        {...(name ? { name } : {})}
        {...(value ? { value } : {})}
        isSelected={shown}
        onChange={handleChange}
        isDisabled={isDisabled}
        isReadOnly={softDisabled || pending}
        {...(softDisabled ? { 'aria-describedby': reasonId } : {})}
        onKeyDown={(event) => {
          reasonHover.handlers.onKeyDown(event);
          // Enter toggles too (Part 2 §4.10); the native checkbox only reacts to Space.
          if (event.key === 'Enter') {
            event.preventDefault();
            handleChange(!shown);
          } else {
            event.continuePropagation();
          }
        }}
        {...(softDisabled
          ? { onFocus: reasonHover.handlers.onFocus, onBlur: reasonHover.handlers.onBlur }
          : {})}
      >
        <SwitchButton
          ref={labelRef}
          className={cx(styles.switch)}
          data-soft-disabled={softDisabled || undefined}
          data-pending={pending || undefined}
          {...(softDisabled
            ? {
                onHoverStart: reasonHover.handlers.onPointerEnter,
                onHoverEnd: reasonHover.handlers.onPointerLeave,
              }
            : {})}
        >
          <span className={styles.label}>{children}</span>
          <span className={styles.track} aria-hidden="true">
            <span className={styles.thumb}>
              {pending ? <Spinner size={12} delayed={false} label={null} /> : null}
            </span>
          </span>
          {showStateText ? (
            <span className={styles.stateText} aria-hidden="true">
              {shown ? t('switch.on') : t('switch.off')}
            </span>
          ) : null}
        </SwitchButton>
        {description ? (
          <Text slot="description" className={styles.description}>
            {description}
          </Text>
        ) : null}
      </SwitchField>
      {softDisabled ? (
        <>
          <span id={reasonId} hidden>
            {disabledReason}
          </span>
          <AnchoredTooltip triggerRef={labelRef} isOpen={reasonHover.active}>
            {disabledReason}
          </AnchoredTooltip>
        </>
      ) : null}
      {pending ? (
        <span className="ds-visually-hidden" role="status">
          {t('switch.saving')}
        </span>
      ) : null}
    </>
  );
}
