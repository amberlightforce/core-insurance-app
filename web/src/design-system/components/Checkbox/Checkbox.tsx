import { Check } from 'lucide-react';
import type { FocusEvent, ReactNode, Ref } from 'react';
import {
  CheckboxButton,
  CheckboxField,
  CheckboxGroup as AriaCheckboxGroup,
  Text,
} from 'react-aria-components';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { hasText, joinIds, messageIds, useFieldIds } from '../TextField/fieldHooks';
import { FieldLabel, FieldMessages } from '../TextField/FormField';
import styles from './Checkbox.module.css';

export interface CheckboxProps {
  /** Label; the hit area covers box + label (at least 24 px tall). */
  children: ReactNode;
  /** Value inside a CheckboxGroup. */
  value?: string;
  name?: string;
  isSelected?: boolean;
  defaultSelected?: boolean;
  onChange?: (isSelected: boolean) => void;
  onBlur?: (event: FocusEvent) => void;
  /** Mixed state (`aria-checked="mixed"`), for example a «select all» box. */
  isIndeterminate?: boolean;
  isDisabled?: boolean;
  isInvalid?: boolean;
  isRequired?: boolean;
  /** Read-only: a check icon or «—» before the label. */
  isReadOnly?: boolean;
  /** Secondary text under the label, linked as the description. */
  description?: string;
  /** Ref to the native input (RHF focus). */
  ref?: Ref<HTMLInputElement>;
  'aria-label'?: string;
}

/**
 * Checkbox (Part 2 §4.8) on React Aria `Checkbox`: 16 px box (20 on touch), MI-04 tick, mixed state, checked
 * hover uses `accent.bg-hover` (D-FE-06), disabled checked fill `--color-control-disabled-checked`.
 * Never for an action with immediate legal effect.
 */
export function Checkbox({
  children,
  value,
  name,
  isSelected,
  defaultSelected,
  onChange,
  onBlur,
  isIndeterminate = false,
  isDisabled = false,
  isInvalid,
  isRequired,
  isReadOnly,
  description,
  ref,
  'aria-label': ariaLabel,
}: CheckboxProps) {
  return (
    <CheckboxField
      className={cx(styles.field)}
      inputRef={ref ?? null}
      {...(value !== undefined ? { value } : {})}
      {...(name ? { name } : {})}
      {...(isSelected !== undefined ? { isSelected } : {})}
      {...(defaultSelected !== undefined ? { defaultSelected } : {})}
      {...(onChange ? { onChange } : {})}
      {...(onBlur ? { onBlur } : {})}
      {...(isInvalid !== undefined ? { isInvalid } : {})}
      {...(isRequired !== undefined ? { isRequired } : {})}
      {...(isReadOnly !== undefined ? { isReadOnly } : {})}
      {...(ariaLabel ? { 'aria-label': ariaLabel } : {})}
      isIndeterminate={isIndeterminate}
      isDisabled={isDisabled}
    >
      {(state) => (
        <>
          <CheckboxButton className={cx(styles.checkbox)}>
            {state.isReadOnly ? (
              <span className={styles.readOnlyGlyph} aria-hidden="true">
                {state.isSelected ? <Icon icon={Check} size={16} /> : '—'}
              </span>
            ) : (
              <span className={styles.box} aria-hidden="true">
                <svg className={styles.glyph} viewBox="0 0 16 16" fill="none">
                  {state.isIndeterminate ? (
                    <path d="M4 8h8" />
                  ) : (
                    // MI-04: the check draws with stroke-dashoffset 14 → 0.
                    <path className={styles.tick} d="M3.5 8.5l3 3 6-7" pathLength={14} />
                  )}
                </svg>
              </span>
            )}
            <span className={styles.label}>{children}</span>
          </CheckboxButton>
          {description ? (
            <Text slot="description" className={styles.description}>
              {description}
            </Text>
          ) : null}
        </>
      )}
    </CheckboxField>
  );
}

export interface CheckboxGroupProps {
  /** Group label (the group is `role="group"` labelled by it, the ARIA equivalent of fieldset/legend). */
  label: string;
  children: ReactNode;
  name?: string;
  value?: string[];
  defaultValue?: string[];
  onChange?: (value: string[]) => void;
  orientation?: 'vertical' | 'horizontal';
  isRequired?: boolean;
  isDisabled?: boolean;
  isReadOnly?: boolean;
  help?: ReactNode;
  helperText?: string;
  /** Group message; every box gets the danger border. */
  errorMessage?: string | undefined;
  id?: string;
}

/** A labelled group of checkboxes with one helper or error message (Part 2 §4.8). Tab moves between boxes. */
export function CheckboxGroup({
  label,
  children,
  name,
  value,
  defaultValue,
  onChange,
  orientation = 'vertical',
  isRequired = false,
  isDisabled = false,
  isReadOnly = false,
  help,
  helperText,
  errorMessage,
  id,
}: CheckboxGroupProps) {
  const ids = useFieldIds();
  const invalid = hasText(errorMessage);
  const describedBy = joinIds(...messageIds(ids, { errorMessage, helperText }));
  return (
    <AriaCheckboxGroup
      className={cx(styles.group)}
      {...(id ? { id } : {})}
      {...(name ? { name } : {})}
      {...(value !== undefined ? { value } : {})}
      {...(defaultValue !== undefined ? { defaultValue } : {})}
      {...(onChange ? { onChange } : {})}
      {...(describedBy ? { 'aria-describedby': describedBy } : {})}
      isRequired={isRequired}
      isDisabled={isDisabled}
      isReadOnly={isReadOnly}
      isInvalid={invalid}
      validationBehavior="aria"
    >
      <FieldLabel isRequired={isRequired} help={help} labelText={label} elementType="span">
        {label}
      </FieldLabel>
      <div className={styles.items} data-orientation={orientation}>
        {children}
      </div>
      <FieldMessages ids={ids} errorMessage={errorMessage} helperText={helperText} />
    </AriaCheckboxGroup>
  );
}
