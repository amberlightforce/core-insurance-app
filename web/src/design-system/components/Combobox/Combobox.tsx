import { ChevronDown, CircleAlert, Search, X } from 'lucide-react';
import { use, useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import {
  Button as AriaButton,
  ComboBox as AriaComboBox,
  ComboBoxStateContext,
  Group,
  Input,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { FieldChromeLabel, FieldMessageList, useFieldMessages } from '../FieldChrome';
import fieldStyles from '../FieldChrome/FieldChrome.module.css';
import styles from './Combobox.module.css';
import {
  CREATE_KEY,
  filterOptions,
  groupOptions,
  highlightOptions,
  RETRY_KEY,
  type ComboboxOption,
} from './options';
import { ResultsPopover, type ResultsFooter } from './ResultsPopover';
import { useAsyncOptions, type LoadOptions } from './useAsyncOptions';
import { useControlledValue } from './useControlledValue';

export interface ComboboxProps<T extends ComboboxOption = ComboboxOption> {
  label: string;
  /** Static options, filtered locally with the Greek matcher (accents, Greeklish, IDs). */
  items?: readonly T[];
  /**
   * Async options: called after a 150 ms debounce once the query has `minQueryLength` characters
   * (default 2, or 1 for a numeric ID). Reject to show the error state with Retry.
   */
  loadOptions?: LoadOptions<T>;
  minQueryLength?: number;
  selectedKey?: Key | null;
  defaultSelectedKey?: Key | null;
  /** The selected option is passed along so async callers do not need to look it up. */
  onSelectionChange?: (key: Key | null, item: T | null) => void;
  inputValue?: string;
  defaultInputValue?: string;
  onInputChange?: (value: string) => void;
  /** Creatable: adds a «Δημιουργία νέου…» row; called with the typed text. */
  onCreate?: (query: string) => void;
  /** Label of the create row, e.g. «Δημιουργία νέου προσώπου…». */
  createLabel?: string;
  description?: string;
  errorMessage?: string;
  isRequired?: boolean;
  isReadOnly?: boolean;
  /** Disabled without a reason (leaves the tab order). Prefer `disabledReason`. */
  isDisabled?: boolean;
  /** Disabled with a reason: stays focusable with `aria-disabled` and the reason as its description. */
  disabledReason?: string;
  placeholder?: string;
  /** Leading search icon. */
  showSearchIcon?: boolean;
  name?: string;
  autoFocus?: boolean;
  className?: string;
}

interface ComboboxInputProps {
  placeholder?: string | undefined;
  softDisabled: boolean;
  /** The second Esc (list already closed) clears the field. */
  onClear: (() => void) | undefined;
}

/** The input, with Alt+↓ to open and the second-Esc-clears rule on top of React Aria's keyboard handling. */
function ComboboxInput({ placeholder, softDisabled, onClear }: ComboboxInputProps) {
  const state = use(ComboBoxStateContext);
  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (!state) return;
    // `state.isOpen` is the state rendered before this key press (React Aria's handler runs first).
    if (e.key === 'ArrowDown' && e.altKey && !state.isOpen && !softDisabled) {
      e.preventDefault();
      state.open(null, 'manual');
    } else if (e.key === 'Escape' && !state.isOpen && onClear) {
      e.stopPropagation();
      onClear();
    }
  };
  return (
    <Input
      className={cx(fieldStyles.input)}
      onKeyDown={handleKeyDown}
      {...(softDisabled ? { 'aria-disabled': true } : {})}
      {...defined({ placeholder })}
    />
  );
}

/**
 * Combobox (Part 2 §4.4): React Aria `ComboBox` with Greek/Greeklish matching and highlighting, grouped
 * results, async loading, creatable, empty / error / loading states. Typed text is kept on blur (with the
 * warning «Δεν επιλέχθηκε τιμή»); Esc closes, a second Esc clears; Alt+↓ opens. The result count is
 * announced politely.
 */
export function Combobox<T extends ComboboxOption = ComboboxOption>(props: ComboboxProps<T>) {
  const {
    label,
    items,
    loadOptions,
    minQueryLength,
    onSelectionChange,
    onCreate,
    createLabel,
    description,
    errorMessage,
    isRequired = false,
    isReadOnly = false,
    isDisabled = false,
    disabledReason,
    placeholder,
    showSearchIcon = false,
    name,
    autoFocus,
    className,
  } = props;
  const { t } = useTranslation('ds');
  const [inputValue, setInputValue] = useControlledValue(
    props.inputValue,
    props.defaultInputValue ?? '',
    props.onInputChange,
  );
  const [selectedKey, setSelectedKey] = useControlledValue<Key | null>(
    props.selectedKey,
    props.defaultSelectedKey ?? null,
  );
  const [selectedItem, setSelectedItem] = useState<T | null>(null);
  const [isOpen, setIsOpen] = useState(false);
  const [openedManually, setOpenedManually] = useState(false);
  const [hasFocus, setHasFocus] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [closeSignal, setCloseSignal] = useState(0);

  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const isAsync = loadOptions !== undefined;
  const asyncOptions = useAsyncOptions(loadOptions, inputValue, minQueryLength);

  const currentItem: T | null = useMemo(() => {
    if (selectedKey === null) return null;
    const found = (isAsync ? asyncOptions.items : (items ?? [])).find((i) => i.id === selectedKey);
    return found ?? (selectedItem?.id === selectedKey ? selectedItem : null);
  }, [selectedKey, isAsync, asyncOptions.items, items, selectedItem]);

  // With the menu opened by the button or ↓ while an option is selected, show every option.
  const query = openedManually && currentItem?.label === inputValue ? '' : inputValue;
  const hits = useMemo(
    () =>
      isAsync ? highlightOptions(asyncOptions.items, query) : filterOptions(items ?? [], query),
    [isAsync, asyncOptions.items, items, query],
  );
  const sections = useMemo(() => groupOptions(hits), [hits]);

  const trimmed = inputValue.trim();
  const status = asyncOptions.status;
  const loading = isAsync && status === 'loading';
  let footer: ResultsFooter | undefined;
  if (isAsync && status === 'error') {
    footer = { kind: 'retry', label: t('combobox.retry'), header: t('combobox.error') };
  } else if (onCreate && trimmed !== '' && !loading) {
    footer = {
      kind: 'create',
      label: createLabel ?? t('combobox.create'),
      header: hits.length === 0 ? t('combobox.empty', { query: trimmed }) : undefined,
    };
  }
  let emptyText: string;
  if (isAsync && status === 'tooShort') {
    emptyText = t('combobox.tooShort', { count: asyncOptions.minLength });
  } else if (loading) {
    emptyText = t('combobox.loading');
  } else if (trimmed === '') {
    emptyText = t('combobox.noOptions');
  } else {
    emptyText = t('combobox.empty', { query: trimmed });
  }

  // Polite result count once the results settle (not on every keystroke).
  const count = hits.length;
  const settled = !isAsync || status === 'ready';
  useEffect(() => {
    if (!isOpen || !settled || !dirty) return;
    const id = setTimeout(() => {
      announce(t('combobox.count', { count }));
    }, 500);
    return () => {
      clearTimeout(id);
    };
  }, [isOpen, settled, dirty, count, query, t]);

  useEffect(() => {
    if (isAsync && status === 'error' && dirty) announce(t('combobox.error'));
  }, [isAsync, status, dirty, t]);

  const showNoSelection =
    dirty && !hasFocus && !isOpen && trimmed !== '' && selectedKey === null && !isReadOnly;
  const messages = useFieldMessages({
    description,
    errorMessage,
    warning: showNoSelection ? t('combobox.noSelection') : undefined,
    disabledReason,
  });
  const isInvalid = messages.showError;

  const select = (key: Key | null, item: T | null) => {
    setSelectedKey(key);
    setSelectedItem(item);
    onSelectionChange?.(key, item);
  };

  const handleSelectionChange = (key: Key | null) => {
    if (key === CREATE_KEY) {
      onCreate?.(trimmed);
      setCloseSignal((n) => n + 1);
      return;
    }
    if (key === RETRY_KEY) {
      asyncOptions.retry();
      return;
    }
    if (key === null) {
      if (selectedKey !== null) select(null, null);
      return;
    }
    const item =
      hits.find((h) => h.item.id === key)?.item ?? (currentItem?.id === key ? currentItem : null);
    select(key, item);
    if (item) setInputValue(item.label);
    setOpenedManually(false);
  };

  const handleInputChange = (value: string) => {
    setDirty(true);
    setOpenedManually(false);
    setInputValue(value);
    if (currentItem && value !== currentItem.label) select(null, null);
  };

  const clear = () => {
    setInputValue('');
    if (selectedKey !== null) select(null, null);
  };

  const readOnlyLike = isReadOnly || softDisabled;

  return (
    <AriaComboBox
      className={cx(fieldStyles.field, styles.combobox, className)}
      inputValue={inputValue}
      value={selectedKey}
      onInputChange={handleInputChange}
      onChange={handleSelectionChange}
      onOpenChange={(open, trigger) => {
        setIsOpen(open);
        if (open) setOpenedManually(trigger === 'manual');
      }}
      onFocusChange={setHasFocus}
      allowsCustomValue
      allowsEmptyCollection
      defaultFilter={() => true}
      menuTrigger="input"
      isRequired={isRequired}
      isInvalid={isInvalid}
      isDisabled={isDisabled}
      isReadOnly={readOnlyLike}
      validationBehavior="aria"
      {...defined({ name, autoFocus, 'aria-describedby': messages.describedBy })}
    >
      <FieldChromeLabel isRequired={isRequired}>{label}</FieldChromeLabel>
      <Group
        className={cx(fieldStyles.frame)}
        isInvalid={isInvalid}
        isDisabled={isDisabled}
        data-readonly={isReadOnly || undefined}
        data-soft-disabled={softDisabled || undefined}
        data-warning={showNoSelection || undefined}
      >
        {showSearchIcon ? (
          <span className={fieldStyles.adornment}>
            <Icon icon={Search} size={16} />
          </span>
        ) : null}
        <ComboboxInput
          placeholder={placeholder}
          softDisabled={softDisabled}
          onClear={!readOnlyLike && (inputValue !== '' || selectedKey !== null) ? clear : undefined}
        />
        {isInvalid ? (
          <span className={fieldStyles.statusIcon}>
            <Icon icon={CircleAlert} size={16} />
          </span>
        ) : null}
        {inputValue !== '' && !readOnlyLike && !isDisabled ? (
          <AriaButton
            className={cx(fieldStyles.fieldButton, fieldStyles.hideWhenReadOnly)}
            aria-label={t('combobox.clear')}
            excludeFromTabOrder
            slot={null}
            onPress={clear}
          >
            <Icon icon={X} size={14} />
          </AriaButton>
        ) : null}
        <AriaButton className={cx(fieldStyles.fieldButton, fieldStyles.hideWhenReadOnly)}>
          <Icon icon={ChevronDown} size={16} />
        </AriaButton>
      </Group>
      <FieldMessageList
        messages={messages}
        description={description}
        errorMessage={errorMessage}
        warning={t('combobox.noSelection')}
        disabledReason={disabledReason}
      />
      <ResultsPopover
        sections={sections}
        footer={footer}
        emptyText={emptyText}
        isStale={asyncOptions.isStale}
        isBusy={loading}
        showBar={asyncOptions.showBar}
        closeSignal={closeSignal}
      />
    </AriaComboBox>
  );
}
