import { Search, X } from 'lucide-react';
import { useRef, type ReactNode, type Ref } from 'react';
import { Button as AriaButton, Input, SearchField as AriaSearchField } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Kbd } from '../Kbd';
import { useFieldIds, useMergedRef } from './fieldHooks';
import { FieldLabel, FieldMessages } from './FormField';
import type { FieldSize } from './TextField';
import fieldStyles from './TextField.module.css';
import styles from './SearchField.module.css';

export interface SearchFieldProps {
  /** Visible label; omit and pass `aria-label` for toolbar search boxes. */
  label?: string;
  'aria-label'?: string;
  id?: string;
  name?: string;
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  /** Enter. */
  onSubmit?: (value: string) => void;
  onClear?: () => void;
  placeholder?: string;
  helperText?: string;
  isDisabled?: boolean;
  size?: FieldSize;
  /** Shortcut chip shown while the field is empty, e.g. `Mod+K`. */
  shortcut?: string;
  ref?: Ref<HTMLInputElement>;
  children?: ReactNode;
}

/**
 * Search field (Part 2 §4.2) on React Aria `SearchField`: Esc clears the text, a second Esc (on an empty
 * field) leaves the field. The clear button is not a tab stop (Esc does the same).
 */
export function SearchField({
  label,
  'aria-label': ariaLabel,
  id,
  name,
  value,
  defaultValue,
  onChange,
  onSubmit,
  onClear,
  placeholder,
  helperText,
  isDisabled = false,
  size = 'md',
  shortcut,
  ref,
  children,
}: SearchFieldProps) {
  const { t } = useTranslation('ds');
  const ids = useFieldIds();
  const inputRef = useRef<HTMLInputElement>(null);
  const setInputRef = useMergedRef(inputRef, ref);
  return (
    <AriaSearchField
      className={cx(styles.root)}
      {...(id ? { id } : {})}
      {...(name ? { name } : {})}
      {...(value !== undefined ? { value } : {})}
      {...(defaultValue !== undefined ? { defaultValue } : {})}
      {...(onChange ? { onChange } : {})}
      {...(onSubmit ? { onSubmit } : {})}
      {...(onClear ? { onClear } : {})}
      {...(ariaLabel ? { 'aria-label': ariaLabel } : {})}
      {...(helperText ? { 'aria-describedby': ids.helper } : {})}
      isDisabled={isDisabled}
      onKeyDown={(event) => {
        // RA clears on the first Esc; on an empty field the second Esc blurs.
        if (event.key === 'Escape' && inputRef.current?.value === '') {
          inputRef.current.blur();
        }
      }}
    >
      {label ? <FieldLabel labelText={label}>{label}</FieldLabel> : null}
      <div className={cx(fieldStyles.control, styles.control)} data-size={size}>
        <Icon icon={Search} size={16} className={fieldStyles.leadingIcon} />
        <Input
          ref={setInputRef}
          className={cx(fieldStyles.input, styles.input)}
          placeholder={placeholder ?? t('searchField.placeholder')}
        />
        {shortcut ? (
          <span className={styles.shortcut} aria-hidden="true">
            <Kbd shortcut={shortcut} tone="subtle" />
          </span>
        ) : null}
        <AriaButton
          className={cx(fieldStyles.iconButton, styles.clear)}
          aria-label={t('searchField.clear')}
        >
          <Icon icon={X} size={14} />
        </AriaButton>
      </div>
      <FieldMessages ids={ids} helperText={helperText} />
      {children}
    </AriaSearchField>
  );
}
