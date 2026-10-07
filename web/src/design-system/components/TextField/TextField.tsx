import type { LucideIcon } from 'lucide-react';
import { Check, CircleAlert, Copy, Eye, Sparkles } from 'lucide-react';
import {
  useLayoutEffect,
  useRef,
  useState,
  type ClipboardEvent,
  type FocusEvent,
  type KeyboardEvent,
  type ReactNode,
  type Ref,
} from 'react';
import {
  Button as AriaButton,
  Input,
  TextArea,
  TextField as AriaTextField,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Spinner } from '../Spinner';
import {
  hasText,
  joinIds,
  messageIds,
  useCharacterCountAnnouncement,
  useCopy,
  useFieldIds,
  useHoverFocus,
  useMergedRef,
} from './fieldHooks';
import { AnchoredTooltip, FormField } from './FormField';
import { caretIndexFor, phoneFormatter, type FieldFormatter } from './formatters';
import styles from './TextField.module.css';

export type TextFieldType = 'text' | 'email' | 'tel' | 'url';
export type FieldSize = 'sm' | 'md' | 'lg';

type FieldElement = HTMLInputElement | HTMLTextAreaElement;

export interface TextFieldProps {
  /** Visible label above the field (wraps to 2 lines, never truncates). */
  label: string;
  /** Input id (for error-summary links and `<label for>` from outside). */
  id?: string;
  name?: string;
  /** Raw value (ungrouped for formatted variants). Pair with `onChange` (works with RHF `Controller`). */
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  onBlur?: (event: FocusEvent<FieldElement>) => void;
  onFocus?: (event: FocusEvent<FieldElement>) => void;
  onKeyDown?: (event: KeyboardEvent<FieldElement>) => void;
  /** Ref to the input or textarea (RHF focuses it on submit errors). */
  ref?: Ref<FieldElement>;
  /** `tel` shows a «+30» prefix and groups as «69x xxx xxxx» on blur. */
  type?: TextFieldType;
  /** Textarea: grows from `minRows` (3) to `maxRows` (12); vertical resize only. */
  multiline?: boolean;
  minRows?: number;
  maxRows?: number;
  /** Ctrl/⌘+Enter in a textarea. */
  onSubmit?: () => void;
  placeholder?: string;
  autoComplete?: string;
  inputMode?: 'text' | 'numeric' | 'decimal' | 'tel' | 'email' | 'url' | 'search' | 'none';
  spellCheck?: boolean;
  autoFocus?: boolean;
  /** Maximum length; shows a character count when `showCount` is not false. */
  maxLength?: number;
  showCount?: boolean;
  isRequired?: boolean;
  /** Content of the `circle-help` popover next to the label. */
  help?: ReactNode;
  /** Helper text (format hints such as «ηη/μμ/εεεε», or the source of a prefilled value). */
  helperText?: string;
  /** Error message; makes the field invalid (`aria-invalid`, danger border, `circle-alert`). */
  errorMessage?: string | undefined;
  /** Soft warning: `warning.solid` border and message; does not block. */
  warningMessage?: string | undefined;
  /** Short-lived polite message under the field (for example a conversion hint). */
  transientMessage?: string | undefined;
  /** Disabled without a reason (native `disabled`, leaves the tab order). Prefer `disabledReason`. */
  isDisabled?: boolean;
  /** Disabled with a reason: focusable, `aria-disabled`, reason tooltip and description. */
  disabledReason?: string;
  /** Async lookup in progress: `aria-busy`, a 14 px trailing spinner after 400 ms. */
  isLoading?: boolean;
  /** Read-only: value as text, no border or fill, padding kept, copy button on hover or focus. */
  isReadOnly?: boolean;
  /** Read-only masked P2/P3 value («•••• 4471»); shows a reveal button that calls `onReveal` (audited). */
  isMasked?: boolean;
  onReveal?: () => void;
  /** Read-only copy button (default true). */
  copyable?: boolean;
  /** Text copied by the copy button (defaults to the raw value). */
  copyValue?: string;
  /** AI-suggested value: iris border, `sparkles` badge (Enter accepts, Delete rejects), italic `ai.fg` value. */
  isAiSuggested?: boolean;
  /** Source and confidence of the suggestion, shown in the badge tooltip. */
  aiSource?: string;
  onAcceptSuggestion?: () => void;
  onRejectSuggestion?: () => void;
  /** Leading adornment text (for example «€»); described to assistive technology. */
  prefix?: ReactNode;
  /** Trailing unit. */
  suffix?: ReactNode;
  /** The unit is already part of the accessible value, so the suffix is hidden from assistive technology. */
  suffixInValue?: boolean;
  leadingIcon?: LucideIcon;
  size?: FieldSize;
  /** Monospaced value with tabular, slashed-zero numerals (identifiers). */
  mono?: boolean;
  /** Display formatter: raw ↔ display. `live` re-formats while typing, otherwise on blur. */
  formatter?: FieldFormatter;
  /** Extra ids for `aria-describedby` (appended after the field's own messages). */
  'aria-describedby'?: string;
  /** Extra content after the field (for example a lookup result). */
  children?: ReactNode;
}

const supportsFieldSizing =
  typeof CSS !== 'undefined' &&
  typeof CSS.supports === 'function' &&
  CSS.supports('field-sizing', 'content');

/**
 * Text input and FormField (Part 2 §4.2) on React Aria `TextField`. Variants: text, email, tel, url,
 * textarea; identifiers build on it (`IdentifierField`). All eight states: default, hover, focus (halo on
 * any focus), disabled (with reason), loading, error, warning, read-only (with copy and masked reveal),
 * plus AI-suggested.
 */
export function TextField(props: TextFieldProps) {
  const {
    label,
    id,
    name,
    value,
    defaultValue,
    onChange,
    onBlur,
    onFocus,
    onKeyDown,
    ref,
    type = 'text',
    multiline = false,
    minRows = 3,
    maxRows = 12,
    onSubmit,
    placeholder,
    autoComplete,
    inputMode,
    spellCheck,
    autoFocus,
    maxLength,
    showCount = true,
    isRequired = false,
    help,
    helperText,
    errorMessage,
    warningMessage,
    transientMessage,
    isDisabled = false,
    disabledReason,
    isLoading = false,
    isReadOnly = false,
    isMasked = false,
    onReveal,
    copyable = true,
    copyValue,
    isAiSuggested = false,
    aiSource,
    onAcceptSuggestion,
    onRejectSuggestion,
    prefix,
    suffix,
    suffixInValue = false,
    leadingIcon,
    size = 'md',
    mono = false,
    formatter: formatterProp,
    children,
  } = props;
  const { t } = useTranslation('ds');
  const ids = useFieldIds();
  const formatter = formatterProp ?? (type === 'tel' ? phoneFormatter : undefined);
  const parse = formatter?.parse ?? identity;
  const format = formatter?.format ?? identity;

  // Raw value: controlled by `value`, otherwise internal.
  const [innerRaw, setInnerRaw] = useState(defaultValue ?? '');
  const raw = value ?? innerRaw;
  // What the input shows; re-derived when the raw value changes from outside.
  const [display, setDisplay] = useState(() => format(raw));
  const [syncedRaw, setSyncedRaw] = useState(raw);
  if (raw !== syncedRaw) {
    setSyncedRaw(raw);
    if (parse(display) !== raw) setDisplay(format(raw));
  }

  const inputRef = useRef<FieldElement>(null);
  const setInputRef = useMergedRef<FieldElement>(inputRef, ref);
  const groupRef = useRef<HTMLDivElement>(null);
  const copyRef = useRef<HTMLButtonElement>(null);
  const pendingCaret = useRef<number | null>(null);
  // Bumped on every live re-format so the caret is restored even when the display text is unchanged.
  const [caretTick, setCaretTick] = useState(0);
  const reasonHover = useHoverFocus();
  const { copied, copy } = useCopy();
  const countMessage = useCharacterCountAnnouncement(raw.length, maxLength);

  const softDisabled = hasText(disabledReason);
  const invalid = hasText(errorMessage);
  const readOnlyView = isReadOnly && !softDisabled;
  const editable = !isReadOnly && !softDisabled && !isDisabled;

  useLayoutEffect(() => {
    const el = inputRef.current;
    if (pendingCaret.current !== null && el && document.activeElement === el) {
      el.setSelectionRange(pendingCaret.current, pendingCaret.current);
    }
    pendingCaret.current = null;
  }, [display, caretTick]);

  // Textarea auto-grow fallback where `field-sizing: content` is unsupported.
  useLayoutEffect(() => {
    const el = inputRef.current;
    if (!multiline || supportsFieldSizing || !el || el.scrollHeight === 0) return;
    const style = getComputedStyle(el);
    const lineHeight = Number.parseFloat(style.lineHeight) || 20;
    const padding =
      Number.parseFloat(style.paddingBlockStart) + Number.parseFloat(style.paddingBlockEnd);
    el.style.blockSize = 'auto';
    const min = lineHeight * minRows + padding;
    const max = lineHeight * maxRows + padding;
    el.style.blockSize = `${String(Math.min(Math.max(el.scrollHeight, min), max))}px`;
  }, [display, multiline, minRows, maxRows]);

  const handleChange = (text: string) => {
    const nextRaw = parse(text);
    if (formatter?.live) {
      const el = inputRef.current;
      const caret = el?.selectionStart ?? text.length;
      const significant = parse(text.slice(0, caret)).length;
      const next = format(nextRaw);
      pendingCaret.current = caretIndexFor(next, significant, parse);
      setDisplay(next);
      setCaretTick((tick) => tick + 1);
    } else {
      setDisplay(text);
    }
    setSyncedRaw(nextRaw);
    if (nextRaw === raw) return;
    if (value === undefined) setInnerRaw(nextRaw);
    onChange?.(nextRaw);
  };

  const handleBlur = (event: FocusEvent<FieldElement>) => {
    if (formatter && !formatter.live) setDisplay(format(parse(display)));
    onBlur?.(event);
  };

  const handleKeyDown = (event: KeyboardEvent<FieldElement>) => {
    if (multiline && onSubmit && event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      onSubmit();
    }
    onKeyDown?.(event);
  };

  // Copying grouped text yields the ungrouped value (Part 2 §4.7).
  const handleCopy = (event: ClipboardEvent<FieldElement>) => {
    if (!formatter) return;
    const el = event.currentTarget;
    const selected = display.slice(el.selectionStart ?? 0, el.selectionEnd ?? display.length);
    if (selected === '') return;
    event.preventDefault();
    event.clipboardData.setData('text/plain', parse(selected));
  };

  const prefixContent =
    prefix ??
    (type === 'tel' ? (
      <>
        <span aria-hidden="true">+30</span>
        <span className="ds-visually-hidden">{t('textField.countryCode')}</span>
      </>
    ) : undefined);
  const hasPrefix = prefixContent !== undefined;
  const hasSuffix = suffix !== undefined && suffix !== null;

  const describedBy = joinIds(
    ...messageIds(ids, {
      errorMessage,
      warningMessage,
      helperText,
      transientMessage,
      disabledReason,
    }),
    hasPrefix ? ids.prefix : undefined,
    hasSuffix && !suffixInValue ? ids.suffix : undefined,
    isMasked ? ids.masked : undefined,
    props['aria-describedby'],
  );

  const count =
    maxLength !== undefined && showCount && !isReadOnly ? (
      <span className={styles.count}>
        <span aria-hidden="true">
          {t('formField.characterCount', { count: raw.length, max: maxLength })}
        </span>
        <span className="ds-visually-hidden" aria-live="polite">
          {countMessage}
        </span>
      </span>
    ) : undefined;

  const fieldProps = {
    // A callback ref fits both <input> and <textarea>.
    ref: setInputRef,
    className: cx(styles.input),
    onCopy: handleCopy,
    onKeyDown: handleKeyDown,
    ...(placeholder !== undefined || readOnlyView
      ? { placeholder: readOnlyView ? '—' : placeholder }
      : {}),
    ...(autoComplete !== undefined
      ? { autoComplete }
      : type === 'tel'
        ? { autoComplete: 'tel-national' }
        : {}),
    ...(spellCheck !== undefined ? { spellCheck } : {}),
    ...(isLoading ? { 'aria-busy': true } : {}),
    ...(softDisabled ? { 'aria-disabled': true } : {}),
  };

  const showCopy = readOnlyView && copyable && !isMasked && raw !== '';
  const showReveal = isReadOnly && isMasked && onReveal !== undefined;

  return (
    <AriaTextField
      className={cx(styles.root)}
      {...(id ? { id } : {})}
      {...(name ? { name } : {})}
      value={display}
      onChange={handleChange}
      onBlur={handleBlur}
      {...(onFocus ? { onFocus } : {})}
      type={type === 'tel' ? 'tel' : type}
      {...(inputMode ? { inputMode } : type === 'tel' ? { inputMode: 'tel' as const } : {})}
      {...(maxLength !== undefined && !formatter ? { maxLength } : {})}
      {...(autoFocus ? { autoFocus } : {})}
      isRequired={isRequired}
      isDisabled={isDisabled}
      isReadOnly={isReadOnly || softDisabled}
      isInvalid={invalid}
      validationBehavior="aria"
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
        transientMessage={transientMessage}
        disabledReason={disabledReason}
        count={count}
      >
        <div
          ref={groupRef}
          className={styles.control}
          data-size={size}
          data-multiline={multiline || undefined}
          data-invalid={invalid || undefined}
          data-warning={(!invalid && hasText(warningMessage)) || undefined}
          data-disabled={isDisabled || softDisabled || undefined}
          data-readonly={readOnlyView || undefined}
          data-loading={isLoading || undefined}
          data-ai={(isAiSuggested && !readOnlyView) || undefined}
          data-mono={mono || undefined}
          {...(softDisabled ? reasonHover.handlers : {})}
        >
          {leadingIcon ? (
            <Icon icon={leadingIcon} size={16} className={styles.leadingIcon} />
          ) : null}
          {hasPrefix ? (
            <span id={ids.prefix} className={styles.adornment}>
              {prefixContent}
            </span>
          ) : null}
          {multiline ? <TextArea {...fieldProps} rows={minRows} /> : <Input {...fieldProps} />}
          {hasSuffix ? (
            <span
              id={ids.suffix}
              className={styles.adornment}
              {...(suffixInValue ? { 'aria-hidden': true } : {})}
            >
              {suffix}
            </span>
          ) : null}
          {isMasked ? (
            <span id={ids.masked} hidden>
              {t('textField.masked')}
            </span>
          ) : null}
          {invalid && !readOnlyView ? (
            <Icon icon={CircleAlert} size={16} className={styles.statusIcon} />
          ) : null}
          {isLoading ? (
            <span className={styles.spinner}>
              <Spinner size={14} label={null} />
            </span>
          ) : null}
          {isAiSuggested && editable ? (
            <AiBadge
              label={label}
              source={aiSource}
              onAccept={onAcceptSuggestion}
              onReject={onRejectSuggestion}
            />
          ) : null}
          {showReveal ? (
            <AriaButton
              className={cx(styles.iconButton)}
              aria-label={t('textField.reveal', { label })}
              onPress={onReveal}
            >
              <Icon icon={Eye} size={14} />
            </AriaButton>
          ) : null}
          {showCopy ? (
            <AriaButton
              ref={copyRef}
              className={cx(styles.iconButton, styles.copyButton)}
              aria-label={t('textField.copy', { label })}
              data-copied={copied || undefined}
              onPress={() => {
                copy(copyValue ?? raw);
              }}
            >
              <Icon icon={copied ? Check : Copy} size={14} className={styles.copyIcon} />
            </AriaButton>
          ) : null}
        </div>
        {children}
      </FormField>
      {softDisabled ? (
        <AnchoredTooltip triggerRef={groupRef} isOpen={reasonHover.active}>
          {disabledReason}
        </AnchoredTooltip>
      ) : null}
      {showCopy ? (
        <AnchoredTooltip triggerRef={copyRef} isOpen={copied}>
          {t('textField.copied')}
        </AnchoredTooltip>
      ) : null}
    </AriaTextField>
  );
}

interface AiBadgeProps {
  label: string;
  source: string | undefined;
  onAccept: (() => void) | undefined;
  onReject: (() => void) | undefined;
}

/** `sparkles` badge on an AI-suggested value: Enter (or a click) accepts, Delete or Backspace rejects. */
function AiBadge({ label, source, onAccept, onReject }: AiBadgeProps) {
  const { t } = useTranslation('ds');
  const ids = useFieldIds();
  const badgeRef = useRef<HTMLButtonElement>(null);
  const hover = useHoverFocus();
  return (
    <>
      <AriaButton
        ref={badgeRef}
        className={cx(styles.aiBadge)}
        aria-label={t('textField.aiBadge', { label })}
        aria-describedby={ids.helper}
        onHoverStart={hover.handlers.onPointerEnter}
        onHoverEnd={hover.handlers.onPointerLeave}
        onFocus={hover.handlers.onFocus}
        onBlur={hover.handlers.onBlur}
        onPress={() => {
          onAccept?.();
          announce(t('textField.aiAccepted'));
        }}
        onKeyDown={(event) => {
          if (event.key === 'Delete' || event.key === 'Backspace') {
            event.preventDefault();
            onReject?.();
            announce(t('textField.aiRejected'));
          } else {
            event.continuePropagation();
          }
        }}
      >
        <Icon icon={Sparkles} size={14} />
      </AriaButton>
      <span id={ids.helper} hidden>
        {t('textField.aiInstructions')}
        {hasText(source) ? ` ${t('textField.aiSource', { source })}` : ''}
      </span>
      <AnchoredTooltip triggerRef={badgeRef} isOpen={hover.active}>
        {hasText(source) ? t('textField.aiSource', { source }) : t('textField.aiInstructions')}
      </AnchoredTooltip>
    </>
  );
}

function identity(text: string): string {
  return text;
}
