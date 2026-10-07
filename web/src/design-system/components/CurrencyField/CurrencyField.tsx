import { CircleAlert } from 'lucide-react';
import {
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ClipboardEvent,
  type KeyboardEvent,
} from 'react';
import { Group, Input, TextField } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import {
  compareMoney,
  formatLive,
  formatMoneyDisplay,
  formatMoneyInputText,
  parseMoneyInput,
  typedFractionDigits,
  type MoneyParseError,
} from '../../../format/money-input';
import type { RegionFormat } from '../../../format/numbers';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { useControlledValue } from '../Combobox/useControlledValue';
import { FieldChromeLabel, FieldMessageList, useFieldMessages } from '../FieldChrome';
import fieldStyles from '../FieldChrome/FieldChrome.module.css';
import styles from './CurrencyField.module.css';

export interface CurrencyFieldProps {
  label: string;
  /** Exact decimal string ("1234.50"), or null when empty. Never a float. */
  value?: string | null;
  defaultValue?: string | null;
  /** Called on commit (blur, Enter, paste) with a valid value or null; never while typing. */
  onChange?: (value: string | null) => void;
  /** Region format; defaults to the user's preference (el-GR «1.234,56 €», en-GB «€1,234.56»). */
  region?: RegionFormat;
  /** Precision from the currency rule (2 for EUR). */
  maxFractionDigits?: number;
  allowNegative?: boolean;
  /** Negative amounts are adverse: shown in `text.adverse`. */
  adverse?: boolean;
  /** The user's authority limit (PLT `authority.check`); above it a soft warning shows. */
  authorityLimit?: string;
  /** Show the amount in words under the field (payment approvals). */
  showAmountInWords?: boolean;
  /** A ready string for the words line; otherwise `toWords(value)` is used. */
  amountInWords?: string;
  toWords?: (value: string) => string;
  /** Announce the words line politely (approval screens only). */
  announceWords?: boolean;
  description?: string;
  /** An external error (form validation); the field's own parse errors take precedence. */
  errorMessage?: string;
  isRequired?: boolean;
  isReadOnly?: boolean;
  isDisabled?: boolean;
  disabledReason?: string;
  placeholder?: string;
  name?: string;
  autoFocus?: boolean;
  className?: string;
}

type FieldError = MoneyParseError;

/**
 * Currency input (Part 2 §4.6, D-FE-08): React Aria `TextField` with our el-GR money parser, not
 * `NumberField` (which rounds and steps). Live grouping with the caret kept; «,» and the numpad «.» both type
 * the decimal separator; paste heuristics with an info hint; `12k` / `1,5ε` shorthand; extra decimals are
 * rejected with a message, never rounded; ↑/↓ do nothing; Esc reverts to the last committed value.
 * Right-aligned `tabular-nums`; the «€» is visible but hidden from screen readers, which hear «ευρώ».
 * Money never animates.
 */
export function CurrencyField({
  label,
  value: valueProp,
  defaultValue = null,
  onChange,
  region: regionProp,
  maxFractionDigits = 2,
  allowNegative = false,
  adverse = false,
  authorityLimit,
  showAmountInWords = false,
  amountInWords,
  toWords,
  announceWords = false,
  description,
  errorMessage,
  isRequired = false,
  isReadOnly = false,
  isDisabled = false,
  disabledReason,
  placeholder,
  name,
  autoFocus,
  className,
}: CurrencyFieldProps) {
  const { t } = useTranslation('ds');
  const preferredRegion = useRegionFormat();
  const region = regionProp ?? preferredRegion;
  const [value, setValue] = useControlledValue<string | null>(valueProp, defaultValue, onChange);
  /** The text being edited; null when the field shows the committed value. */
  const [draft, setDraft] = useState<string | null>(null);
  const [error, setError] = useState<FieldError | null>(null);
  const [interpreted, setInterpreted] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const pendingCaret = useRef<number | null>(null);
  const unitId = useId();

  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const readOnlyLike = isReadOnly || softDisabled;
  const parseOptions = { locale: region, maxFractionDigits, allowNegative };
  const displayText = draft ?? (value === null ? '' : formatMoneyInputText(value, region));

  useLayoutEffect(() => {
    const caret = pendingCaret.current;
    pendingCaret.current = null;
    if (caret !== null && inputRef.current && document.activeElement === inputRef.current) {
      inputRef.current.setSelectionRange(caret, caret);
    }
  }, [displayText]);

  const errorText = (code: FieldError): string => {
    switch (code) {
      case 'tooManyDecimals':
        return t('currencyField.tooManyDecimals', { count: maxFractionDigits });
      case 'negativeNotAllowed':
        return t('currencyField.negativeNotAllowed');
      default:
        return t('currencyField.invalid', { example: formatMoneyInputText('1234.56', region) });
    }
  };

  const commitText = (text: string) => {
    if (text.trim() === '') {
      setDraft(null);
      setError(null);
      setInterpreted(null);
      if (value !== null) setValue(null);
      return;
    }
    const result = parseMoneyInput(text, parseOptions);
    if (result.ok) {
      setDraft(null);
      setError(null);
      if (result.value !== value) setValue(result.value);
    } else {
      setDraft(text);
      setError(result.error);
    }
  };

  const handleChange = (raw: string) => {
    const caret = inputRef.current?.selectionStart ?? raw.length;
    const live = formatLive(raw, caret, { locale: region });
    pendingCaret.current = live.caret;
    setDraft(live.text);
    setInterpreted(null);
    if (typedFractionDigits(live.text, region) > maxFractionDigits) {
      setError('tooManyDecimals');
    } else if (error !== null) {
      // Once an error shows, re-validate on every change.
      const result = parseMoneyInput(live.text, parseOptions);
      setError(result.ok || live.text.trim() === '' ? null : result.error);
    }
  };

  const handlePaste = (e: ClipboardEvent<HTMLInputElement>) => {
    if (readOnlyLike) return;
    const pasted = e.clipboardData.getData('text');
    e.preventDefault();
    const result = parseMoneyInput(pasted, parseOptions);
    if (result.ok) {
      setDraft(null);
      setError(null);
      setInterpreted(result.interpreted ? formatMoneyDisplay(result.value, region) : null);
      if (result.value !== value) setValue(result.value);
    } else {
      setDraft(pasted.trim());
      setError(result.error);
      setInterpreted(null);
    }
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      // No stepper on money (Part 2 §4.6).
      e.preventDefault();
    } else if (e.key === 'Escape' && (draft !== null || error !== null)) {
      e.preventDefault();
      e.stopPropagation();
      setDraft(null);
      setError(null);
      setInterpreted(null);
    } else if (e.key === 'Enter' && draft !== null) {
      commitText(draft);
    }
  };

  const overLimit =
    authorityLimit !== undefined &&
    value !== null &&
    error === null &&
    draft === null &&
    compareMoney(value.replace(/^-/, ''), authorityLimit, maxFractionDigits) > 0;
  const warning = overLimit
    ? t('currencyField.overAuthority', { limit: formatMoneyDisplay(authorityLimit, region) })
    : undefined;
  const info =
    interpreted !== null ? t('currencyField.interpreted', { amount: interpreted }) : undefined;
  const ownError = error === null ? undefined : errorText(error);
  const shownError = ownError ?? errorMessage;
  const messages = useFieldMessages({
    description,
    errorMessage: shownError,
    warning,
    info,
    disabledReason,
    extraDescribedBy: unitId,
  });
  const isInvalid = messages.showError;
  const negative = displayText.startsWith('\u2212') || displayText.startsWith('-');
  const words =
    showAmountInWords && value !== null && draft === null
      ? (amountInWords ?? toWords?.(value))
      : undefined;
  const currencySymbol = '€';
  const prefix = region === 'en-GB';

  return (
    <TextField
      className={cx(fieldStyles.field, styles.currency, className)}
      value={displayText}
      onChange={handleChange}
      onBlur={() => {
        if (draft !== null) commitText(draft);
      }}
      isRequired={isRequired}
      isInvalid={isInvalid}
      isDisabled={isDisabled}
      isReadOnly={readOnlyLike}
      validationBehavior="aria"
      {...defined({ name, autoFocus, 'aria-describedby': messages.describedBy })}
    >
      <FieldChromeLabel isRequired={isRequired}>{label}</FieldChromeLabel>
      <Group
        className={cx(fieldStyles.frame, styles.frame)}
        isInvalid={isInvalid}
        isDisabled={isDisabled}
        data-readonly={isReadOnly || undefined}
        data-soft-disabled={softDisabled || undefined}
        data-warning={overLimit || undefined}
        data-negative={negative || undefined}
        data-adverse={(adverse && negative) || undefined}
      >
        {isInvalid ? (
          <span className={fieldStyles.statusIcon}>
            <Icon icon={CircleAlert} size={16} />
          </span>
        ) : null}
        {prefix ? (
          <span className={styles.unit} aria-hidden="true">
            {currencySymbol}
          </span>
        ) : null}
        <Input
          ref={inputRef}
          className={cx(fieldStyles.input, styles.input)}
          inputMode="decimal"
          autoComplete="off"
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
          {...(softDisabled ? { 'aria-disabled': true } : {})}
          {...defined({ placeholder })}
        />
        {prefix ? null : (
          <span className={styles.unit} aria-hidden="true">
            {currencySymbol}
          </span>
        )}
        <span id={unitId} hidden>
          {t('currencyField.unit')}
        </span>
      </Group>
      <FieldMessageList
        messages={messages}
        description={description}
        errorMessage={shownError}
        warning={warning}
        info={info}
        disabledReason={disabledReason}
      />
      {showAmountInWords ? (
        // The live region exists before its text changes so screen readers pick up updates.
        <div
          className={styles.words}
          data-empty={words === undefined || undefined}
          {...(announceWords ? { 'aria-live': 'polite' as const } : {})}
        >
          {words}
        </div>
      ) : null}
    </TextField>
  );
}
