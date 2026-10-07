import { ChevronDown, CircleAlert, X } from 'lucide-react';
import {
  use,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type RefObject,
} from 'react';
import {
  Button as AriaButton,
  ComboBox as AriaComboBox,
  ComboBoxStateContext,
  Group,
  Input,
  Tag,
  TagGroup,
  TagList,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import {
  CREATE_KEY,
  filterOptions,
  groupOptions,
  highlightOptions,
  RETRY_KEY,
  type ComboboxOption,
} from '../Combobox/options';
import { ResultsPopover, type ResultsFooter } from '../Combobox/ResultsPopover';
import { useAsyncOptions, type LoadOptions } from '../Combobox/useAsyncOptions';
import { useControlledValue } from '../Combobox/useControlledValue';
import { FieldChromeLabel, FieldMessageList, useFieldMessages } from '../FieldChrome';
import fieldStyles from '../FieldChrome/FieldChrome.module.css';
import styles from './MultiCombobox.module.css';
import { useChipOverflow } from './useChipOverflow';

export interface MultiComboboxProps<T extends ComboboxOption = ComboboxOption> {
  label: string;
  /** Static options, filtered locally with the Greek matcher. */
  items?: readonly T[];
  /** Async options (150 ms debounce, minimum 2 characters or 1 for numeric IDs). */
  loadOptions?: LoadOptions<T>;
  minQueryLength?: number;
  selectedKeys?: readonly Key[];
  defaultSelectedKeys?: readonly Key[];
  /** The selected options are passed along (in selection order). */
  onSelectionChange?: (keys: Key[], items: T[]) => void;
  /** Options already selected elsewhere, so their chips have labels (async callers). */
  selectedItems?: readonly T[];
  onCreate?: (query: string) => void;
  createLabel?: string;
  description?: string;
  errorMessage?: string;
  isRequired?: boolean;
  isReadOnly?: boolean;
  isDisabled?: boolean;
  disabledReason?: string;
  placeholder?: string;
  /** Chips wrap to this many lines before «+N» (default 3). */
  maxChipLines?: number;
  /** Hard cap on visible chips before «+N» (in addition to the line limit). */
  maxVisibleChips?: number;
  name?: string;
  className?: string;
}

interface MultiInputProps {
  placeholder?: string | undefined;
  softDisabled: boolean;
  inputRef: RefObject<HTMLInputElement | null>;
  onRemoveLast: (() => void) | undefined;
  onFocusLastChip: (() => void) | undefined;
  onClear: (() => void) | undefined;
}

/**
 * The text input: Alt+↓ opens; Backspace on an empty input removes the last chip; ← at the start of the input
 * moves to the chips; a second Esc clears the typed text.
 */
function MultiInput({
  placeholder,
  softDisabled,
  inputRef,
  onRemoveLast,
  onFocusLastChip,
  onClear,
}: MultiInputProps) {
  const state = use(ComboBoxStateContext);
  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const input = e.currentTarget;
    const atStart = input.selectionStart === 0 && input.selectionEnd === 0;
    if (e.key === 'ArrowDown' && e.altKey && state && !state.isOpen && !softDisabled) {
      e.preventDefault();
      state.open(null, 'manual');
    } else if (e.key === 'Backspace' && input.value === '' && onRemoveLast) {
      e.preventDefault();
      onRemoveLast();
    } else if (e.key === 'ArrowLeft' && atStart && onFocusLastChip) {
      e.preventDefault();
      state?.close();
      onFocusLastChip();
    } else if (e.key === 'Escape' && state && !state.isOpen && input.value !== '' && onClear) {
      e.stopPropagation();
      onClear();
    }
  };
  return (
    <Input
      ref={inputRef}
      className={cx(fieldStyles.input, styles.input)}
      onKeyDown={handleKeyDown}
      {...(softDisabled ? { 'aria-disabled': true } : {})}
      {...defined({ placeholder })}
    />
  );
}

/**
 * Multi-select combobox (Part 2 §4.4 `multi`): React Aria `ComboBox selectionMode="multiple"` composed with a
 * `TagGroup` of chips inside the field. Chips: `radius.sm`, 20/24 px, `status.neutral.bg`, 16 px ×; at most 3
 * lines, then «+N». Backspace on the empty input removes the last chip; ← from the start of the input walks
 * the chips; Delete or Backspace removes the focused chip; → past the last chip returns to the input. Same
 * Greek/Greeklish matching as Combobox. (React Aria's `TokenField` was not used: it edits free-text tokens
 * in a content-editable, whereas this is a selection from a list.)
 */
export function MultiCombobox<T extends ComboboxOption = ComboboxOption>(
  props: MultiComboboxProps<T>,
) {
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
    maxChipLines = 3,
    maxVisibleChips,
    name,
    className,
  } = props;
  const { t } = useTranslation('ds');
  const [selectedKeys, setSelectedKeys] = useControlledValue<readonly Key[]>(
    props.selectedKeys,
    props.defaultSelectedKeys ?? [],
  );
  const [inputValue, setInputValue] = useState('');
  const [known, setKnown] = useState<ReadonlyMap<Key, T>>(new Map());
  const [expanded, setExpanded] = useState(false);
  const [closeSignal, setCloseSignal] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const frameRef = useRef<HTMLDivElement>(null);
  const labelId = useId();
  const chipsRef = useRef<HTMLDivElement>(null);
  const [focusChipsSignal, setFocusChipsSignal] = useState(0);
  const [focusInputSignal, setFocusInputSignal] = useState(0);

  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const readOnlyLike = isReadOnly || softDisabled;
  const isAsync = loadOptions !== undefined;
  const asyncOptions = useAsyncOptions(loadOptions, inputValue, minQueryLength);

  const lookup = useMemo(() => {
    const map = new Map<Key, T>(known);
    for (const item of props.selectedItems ?? []) map.set(item.id, item);
    for (const item of items ?? []) map.set(item.id, item);
    for (const item of asyncOptions.items) map.set(item.id, item);
    return map;
  }, [known, props.selectedItems, items, asyncOptions.items]);

  const selectedOptions = useMemo(
    () =>
      selectedKeys.map(
        (key) => lookup.get(key) ?? ({ id: key, label: String(key) } as unknown as T),
      ),
    [selectedKeys, lookup],
  );

  const hits = useMemo(
    () =>
      isAsync
        ? highlightOptions(asyncOptions.items, inputValue)
        : filterOptions(items ?? [], inputValue),
    [isAsync, asyncOptions.items, items, inputValue],
  );
  const sections = useMemo(() => groupOptions(hits), [hits]);

  const valueArray = useMemo(() => [...selectedKeys], [selectedKeys]);
  const signature = selectedKeys.map(String).join('\u0000');
  const fitting = useChipOverflow(chipsRef, signature, maxChipLines);
  let visibleCount = selectedOptions.length;
  if (!expanded) {
    if (fitting !== null) visibleCount = Math.min(visibleCount, fitting);
    if (maxVisibleChips !== undefined) visibleCount = Math.min(visibleCount, maxVisibleChips);
  }
  const visibleChips = selectedOptions.slice(0, visibleCount);
  const hiddenCount = selectedOptions.length - visibleCount;

  // Focus moves are requested by handlers and performed after the chips re-render.
  useEffect(() => {
    if (focusChipsSignal === 0) return;
    const rows = chipsRef.current?.querySelectorAll<HTMLElement>('[role="row"]');
    const last = rows?.[rows.length - 1];
    if (last) last.focus();
    else inputRef.current?.focus();
  }, [focusChipsSignal]);
  useEffect(() => {
    if (focusInputSignal > 0) inputRef.current?.focus();
  }, [focusInputSignal]);

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
  if (isAsync && status === 'tooShort')
    emptyText = t('combobox.tooShort', { count: asyncOptions.minLength });
  else if (loading) emptyText = t('combobox.loading');
  else if (trimmed === '') emptyText = t('combobox.noOptions');
  else emptyText = t('combobox.empty', { query: trimmed });

  const messages = useFieldMessages({ description, errorMessage, disabledReason });
  const isInvalid = messages.showError;

  const commit = (keys: readonly Key[], extra?: T) => {
    const next = [...keys];
    if (extra) {
      setKnown((previous) => new Map(previous).set(extra.id, extra));
    }
    setSelectedKeys(next);
    const byKey = new Map(lookup);
    if (extra) byKey.set(extra.id, extra);
    onSelectionChange?.(
      next,
      next.map((key) => byKey.get(key)).filter((item): item is T => item !== undefined),
    );
  };

  const removeKeys = (keys: ReadonlySet<Key>) => {
    const remaining = selectedKeys.filter((k) => !keys.has(k));
    commit(remaining);
    for (const key of keys) {
      announce(t('multiCombobox.removed', { label: lookup.get(key)?.label ?? String(key) }));
    }
    if (remaining.length === 0) setFocusInputSignal((n) => n + 1);
  };

  const handleChange = (value: Key[]) => {
    if (value.includes(CREATE_KEY)) {
      onCreate?.(trimmed);
      setCloseSignal((n) => n + 1);
      return;
    }
    if (value.includes(RETRY_KEY)) {
      asyncOptions.retry();
      return;
    }
    const added = value.find((key) => !selectedKeys.includes(key));
    const removed = selectedKeys.find((key) => !value.includes(key));
    const addedItem = added === undefined ? undefined : hits.find((h) => h.item.id === added)?.item;
    commit(value, addedItem);
    if (added !== undefined) {
      announce(t('multiCombobox.added', { label: addedItem?.label ?? String(added) }));
      setInputValue('');
    } else if (removed !== undefined) {
      announce(
        t('multiCombobox.removed', { label: lookup.get(removed)?.label ?? String(removed) }),
      );
    }
  };

  const handleChipKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const rows = Array.from(chipsRef.current?.querySelectorAll<HTMLElement>('[role="row"]') ?? []);
    const focused = document.activeElement;
    const isLast = rows.length > 0 && rows[rows.length - 1]?.contains(focused ?? null) === true;
    if ((e.key === 'ArrowRight' && isLast) || e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      setFocusInputSignal((n) => n + 1);
    }
  };

  const chevron = (
    <AriaButton className={cx(fieldStyles.fieldButton, fieldStyles.hideWhenReadOnly)}>
      <Icon icon={ChevronDown} size={16} />
    </AriaButton>
  );

  // The chips sit outside React Aria's ComboBox (a TagGroup cannot live inside its collection context), in
  // the same visual frame; the combobox is labelled by the field label and its list anchors to the frame.
  return (
    <div className={cx(fieldStyles.field, styles.multi, className)}>
      <FieldChromeLabel id={labelId} isRequired={isRequired}>
        {label}
      </FieldChromeLabel>
      <Group
        ref={frameRef}
        className={cx(fieldStyles.frame, styles.frame)}
        isInvalid={isInvalid}
        isDisabled={isDisabled}
        data-readonly={isReadOnly || undefined}
        data-soft-disabled={softDisabled || undefined}
      >
        <div className={styles.content}>
          {selectedOptions.length > 0 ? (
            // Capture phase: React Aria's grid handles arrow keys on the rows before bubbling.
            <div className={styles.tagWrap} onKeyDownCapture={handleChipKeyDown}>
              <TagGroup
                aria-label={t('multiCombobox.selected', { label })}
                className={cx(styles.tagGroup)}
                {...defined({ onRemove: readOnlyLike || isDisabled ? undefined : removeKeys })}
              >
                <TagList ref={chipsRef} items={visibleChips} className={cx(styles.tagList)}>
                  {(item) => (
                    <Tag id={item.id} textValue={item.label} className={cx(styles.chip)}>
                      {({ allowsRemoving }) => (
                        <>
                          <span className={styles.chipLabel}>{item.label}</span>
                          {allowsRemoving ? (
                            <AriaButton
                              slot="remove"
                              className={cx(styles.chipRemove)}
                              aria-label={t('multiCombobox.remove')}
                            >
                              <Icon icon={X} size={16} />
                            </AriaButton>
                          ) : null}
                        </>
                      )}
                    </Tag>
                  )}
                </TagList>
              </TagGroup>
            </div>
          ) : null}
          {hiddenCount > 0 ? (
            <AriaButton
              className={cx(styles.more)}
              aria-label={t('multiCombobox.showAll', { count: hiddenCount })}
              onPress={() => {
                setExpanded(true);
              }}
            >
              {t('multiCombobox.more', { count: hiddenCount })}
            </AriaButton>
          ) : null}
          <AriaComboBox
            className={cx(styles.combo)}
            aria-labelledby={labelId}
            selectionMode="multiple"
            value={valueArray}
            onChange={handleChange}
            inputValue={inputValue}
            onInputChange={setInputValue}
            allowsCustomValue
            allowsEmptyCollection
            defaultFilter={() => true}
            menuTrigger="input"
            isRequired={isRequired}
            isInvalid={isInvalid}
            isDisabled={isDisabled}
            isReadOnly={readOnlyLike}
            validationBehavior="aria"
            {...defined({ name, 'aria-describedby': messages.describedBy })}
          >
            <MultiInput
              placeholder={selectedOptions.length === 0 ? placeholder : undefined}
              softDisabled={softDisabled}
              inputRef={inputRef}
              onRemoveLast={
                readOnlyLike || selectedKeys.length === 0
                  ? undefined
                  : () => {
                      const last = selectedKeys[selectedKeys.length - 1];
                      if (last !== undefined) removeKeys(new Set([last]));
                    }
              }
              onFocusLastChip={
                selectedKeys.length === 0
                  ? undefined
                  : () => {
                      setFocusChipsSignal((n) => n + 1);
                    }
              }
              onClear={
                readOnlyLike
                  ? undefined
                  : () => {
                      setInputValue('');
                    }
              }
            />
            {isInvalid ? (
              <span className={fieldStyles.statusIcon}>
                <Icon icon={CircleAlert} size={16} />
              </span>
            ) : null}
            {chevron}
            <ResultsPopover
              sections={sections}
              footer={footer}
              emptyText={emptyText}
              isStale={asyncOptions.isStale}
              isBusy={loading}
              showBar={asyncOptions.showBar}
              closeSignal={closeSignal}
              triggerRef={frameRef}
              showSelection
            />
          </AriaComboBox>
        </div>
      </Group>
      <FieldMessageList
        messages={messages}
        description={description}
        errorMessage={errorMessage}
        disabledReason={disabledReason}
      />
    </div>
  );
}
