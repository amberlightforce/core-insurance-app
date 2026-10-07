import { Check, ChevronDown, CircleAlert } from 'lucide-react';
import { useRef, type FocusEvent, type ReactNode, type Ref } from 'react';
import {
  Button as AriaButton,
  Header,
  ListBox,
  ListBoxItem,
  ListBoxSection,
  Popover,
  Select as AriaSelect,
  SelectValue,
  Text,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Spinner } from '../Spinner';
import {
  hasText,
  joinIds,
  messageIds,
  useAriaAttribute,
  useFieldIds,
  useMergedRef,
} from '../TextField/fieldHooks';
import { FormField } from '../TextField/FormField';
import { TextField, type FieldSize } from '../TextField/TextField';
import fieldStyles from '../TextField/TextField.module.css';
import { Tooltip } from '../Tooltip';
import styles from './Select.module.css';

export interface SelectOption {
  id: string;
  label: string;
  /** Secondary line under the label. */
  description?: string;
  /** Regulated code shown before the label («03 · Οδική βοήθεια»). Typeahead still matches the label. */
  code?: string;
  isDisabled?: boolean;
}

export interface SelectSection {
  id: string;
  /** Group header. */
  title: string;
  options: SelectOption[];
}

export interface SelectProps {
  label: string;
  id?: string;
  name?: string;
  /** Flat options, ordered by frequency or Greek collation, never by code. */
  options?: SelectOption[];
  /** Grouped options with headers (use instead of `options`). */
  sections?: SelectSection[];
  /** Selected option id (`null` for none). Pair with `onChange` (works with RHF `Controller`). */
  value?: string | null;
  defaultValue?: string | null;
  onChange?: (value: string | null) => void;
  onBlur?: (event: FocusEvent) => void;
  /** Ref to the trigger button (RHF focuses it on submit errors). */
  ref?: Ref<HTMLButtonElement>;
  /** Defaults to «Επιλέξτε…». */
  placeholder?: string;
  isRequired?: boolean;
  help?: ReactNode;
  helperText?: string;
  errorMessage?: string | undefined;
  warningMessage?: string | undefined;
  /** Disabled without a reason (leaves the tab order). Prefer `disabledReason`. */
  isDisabled?: boolean;
  /** Disabled with a reason: focusable, `aria-disabled`, reason tooltip and description; never opens. */
  disabledReason?: string;
  /** Options are loading: spinner after 400 ms in the trigger, 3 skeleton rows in the list, `aria-busy`. */
  isLoading?: boolean;
  /** Read-only: the selected label as plain text. */
  isReadOnly?: boolean;
  size?: FieldSize;
}

/**
 * Select (Part 2 §4.3) on React Aria `Select`: ≤ 7 options without search (use a combobox beyond that).
 * Space/Enter/↓ open; ↑↓, Home/End, typeahead (accent- and case-insensitive, so «α» finds «Άμεση»); Enter
 * selects; Esc closes; Tab selects the highlighted option. The list is a glass popover (`material.popover`).
 */
export function Select(props: SelectProps) {
  const {
    label,
    id,
    name,
    options,
    sections,
    value,
    defaultValue,
    onChange,
    onBlur,
    ref,
    placeholder,
    isRequired = false,
    help,
    helperText,
    errorMessage,
    warningMessage,
    isDisabled = false,
    disabledReason,
    isLoading = false,
    isReadOnly = false,
    size = 'md',
  } = props;
  const { t } = useTranslation('ds');
  const ids = useFieldIds();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const setTriggerRef = useMergedRef(triggerRef, ref);
  // React Aria's Button does not pass aria-busy through.
  useAriaAttribute(triggerRef, 'aria-busy', isLoading ? 'true' : undefined);
  const softDisabled = hasText(disabledReason);
  const invalid = hasText(errorMessage);
  const allOptions = sections ? sections.flatMap((section) => section.options) : (options ?? []);

  if (isReadOnly) {
    const selected = allOptions.find((option) => option.id === (value ?? defaultValue));
    return (
      <TextField
        label={label}
        {...(id ? { id } : {})}
        isReadOnly
        copyable={false}
        value={selected ? optionText(selected) : ''}
        {...(helperText ? { helperText } : {})}
        size={size}
      />
    );
  }

  const describedBy = joinIds(
    ...messageIds(ids, { errorMessage, warningMessage, helperText, disabledReason }),
  );

  const trigger = (
    <AriaButton
      ref={setTriggerRef}
      className={cx(fieldStyles.control, styles.trigger)}
      data-size={size}
      data-invalid={invalid || undefined}
      data-warning={(!invalid && hasText(warningMessage)) || undefined}
      data-disabled={isDisabled || softDisabled || undefined}
      data-loading={isLoading || undefined}
      {...(softDisabled ? { 'aria-disabled': true } : {})}
    >
      <SelectValue className={cx(styles.value)} />
      {invalid ? <Icon icon={CircleAlert} size={16} className={fieldStyles.statusIcon} /> : null}
      {isLoading ? (
        <span className={fieldStyles.spinner}>
          <Spinner size={14} label={null} />
        </span>
      ) : null}
      <Icon icon={ChevronDown} size={16} className={styles.chevron} />
    </AriaButton>
  );

  const renderOption = (option: SelectOption) => (
    <ListBoxItem
      key={option.id}
      id={option.id}
      textValue={option.label}
      className={cx(styles.option)}
      {...(option.isDisabled ? { isDisabled: true } : {})}
    >
      {({ isSelected }) => (
        <>
          <span className={styles.check}>
            {isSelected ? <Icon icon={Check} size={16} /> : null}
          </span>
          <span className={styles.optionText}>
            <Text slot="label" className={styles.optionLabel}>
              {optionText(option)}
            </Text>
            {option.description ? (
              <Text slot="description" className={styles.optionDescription}>
                {option.description}
              </Text>
            ) : null}
          </span>
        </>
      )}
    </ListBoxItem>
  );

  return (
    <AriaSelect
      className={cx(styles.root)}
      {...(id ? { id } : {})}
      {...(name ? { name } : {})}
      {...(value !== undefined
        ? { value }
        : softDisabled
          ? { value: defaultValue ?? null }
          : defaultValue !== undefined
            ? { defaultValue }
            : {})}
      onChange={(key: Key | null) => {
        if (softDisabled) return;
        onChange?.(key === null ? null : String(key));
      }}
      {...(onBlur ? { onBlur } : {})}
      placeholder={placeholder ?? t('select.placeholder')}
      isRequired={isRequired}
      isDisabled={isDisabled}
      isInvalid={invalid}
      validationBehavior="aria"
      {...(softDisabled ? { isOpen: false } : {})}
      {...(isLoading ? { allowsEmptyCollection: true } : {})}
      {...(describedBy ? { 'aria-describedby': describedBy } : {})}
    >
      <FormField
        label={label}
        labelText={label}
        isRequired={isRequired}
        help={help}
        ids={ids}
        errorMessage={errorMessage}
        warningMessage={warningMessage}
        helperText={helperText}
        disabledReason={disabledReason}
      >
        {softDisabled ? <Tooltip content={disabledReason}>{trigger}</Tooltip> : trigger}
      </FormField>
      <Popover
        className={cx(styles.popover)}
        data-material="popover"
        placement="bottom start"
        offset={4}
      >
        <ListBox
          className={cx(styles.listbox)}
          renderEmptyState={() =>
            isLoading ? (
              <div className={styles.loading} aria-busy="true">
                <span className="ds-visually-hidden">{t('select.loading')}</span>
                <span className={styles.skeleton} aria-hidden="true" />
                <span className={styles.skeleton} aria-hidden="true" />
                <span className={styles.skeleton} aria-hidden="true" />
              </div>
            ) : null
          }
        >
          {isLoading
            ? []
            : sections
              ? sections.map((section) => (
                  <ListBoxSection key={section.id} id={section.id} className={cx(styles.section)}>
                    <Header className={styles.header}>{section.title}</Header>
                    {section.options.map(renderOption)}
                  </ListBoxSection>
                ))
              : (options ?? []).map(renderOption)}
        </ListBox>
      </Popover>
    </AriaSelect>
  );
}

function optionText(option: SelectOption): string {
  return option.code ? `${option.code} · ${option.label}` : option.label;
}
