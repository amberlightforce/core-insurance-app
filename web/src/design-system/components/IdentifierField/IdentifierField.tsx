import { useEffect, useMemo, useRef, useState, type FocusEvent } from 'react';
import { useTranslation } from 'react-i18next';

import {
  identifierCodecs,
  normalizePlate,
  type IdentifierIssue,
  type IdentifierKind,
} from '../../../format/identifiers';
import { hasText } from '../TextField/fieldHooks';
import type { FieldFormatter } from '../TextField/formatters';
import { TextField, type TextFieldProps } from '../TextField/TextField';
import { useIssueMessage } from './issueMessage';

export interface IdentifierFieldProps extends Omit<
  TextFieldProps,
  'type' | 'multiline' | 'formatter' | 'mono' | 'autoComplete' | 'spellCheck' | 'maxLength'
> {
  /** `afm` ΑΦΜ · `iban` · `plate` Greek plate · `vin` · `reference` policy/claim/invoice number. */
  kind: IdentifierKind;
  /** Scheme pattern for `reference` (from pack data), tested against the raw value. */
  referencePattern?: RegExp;
  /** Built-in format validation on blur (and live once an issue shows). Default true. */
  validate?: boolean;
  /** Called with the current issue (or `null`) whenever validation runs. */
  onValidityChange?: (issue: IdentifierIssue | null) => void;
}

const CONVERSION_HINT_MS = 1000;

/**
 * Identifier input (Part 2 §4.7) on TextField with an in-house mask: ΑΦΜ «000 000 000» (mod 11), IBAN in
 * groups of 4 (length + mod 97), Greek plate «ΑΑΑ-0000» (Latin lookalikes become Greek capitals with a 1 s
 * hint), VIN (17, no I/O/Q, check digit as a warning) and references. Monospaced with tabular, slashed-zero
 * numerals; `autocomplete="off"`, `spellcheck="false"`. `onChange` and copy yield the **ungrouped** value.
 */
export function IdentifierField({
  kind,
  referencePattern,
  validate = true,
  onValidityChange,
  onChange,
  onBlur,
  value,
  defaultValue,
  errorMessage,
  warningMessage,
  transientMessage,
  ...rest
}: IdentifierFieldProps) {
  const { t } = useTranslation('ds');
  const codec = identifierCodecs[kind];
  const messageFor = useIssueMessage();
  const converted = useRef(false);
  const [hint, setHint] = useState(false);
  const [issue, setIssue] = useState<IdentifierIssue | null>(null);
  const [innerRaw, setInnerRaw] = useState(() => codec.normalize(defaultValue ?? ''));
  const raw = value === undefined ? innerRaw : codec.normalize(value);

  const formatter = useMemo<FieldFormatter>(
    () => ({
      parse: (text) => {
        if (kind === 'plate' && normalizePlate(text).converted) converted.current = true;
        return codec.normalize(text);
      },
      format: codec.format,
      live: true,
    }),
    [codec, kind],
  );

  useEffect(() => {
    if (!hint) return;
    const id = setTimeout(() => {
      setHint(false);
    }, CONVERSION_HINT_MS);
    return () => {
      clearTimeout(id);
    };
  }, [hint]);

  const runValidation = (next: string) => {
    const found = validate && next !== '' ? codec.validate(next, referencePattern) : null;
    setIssue(found);
    onValidityChange?.(found);
  };

  const handleChange = (next: string) => {
    if (converted.current) {
      converted.current = false;
      setHint(true);
    }
    if (value === undefined) setInnerRaw(next);
    // Validation timing (Part 2 §4.2): on blur, then on change once an issue is showing.
    if (issue) runValidation(next);
    onChange?.(next);
  };

  const handleBlur = (event: FocusEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    runValidation(raw);
    onBlur?.(event);
  };

  const issueText = issue ? messageFor(kind, issue) : undefined;
  const ownError = issue?.severity === 'error' ? issueText : undefined;
  const ownWarning = issue?.severity === 'warning' ? issueText : undefined;

  return (
    <TextField
      {...rest}
      value={raw}
      onChange={handleChange}
      onBlur={handleBlur}
      formatter={formatter}
      mono
      autoComplete="off"
      spellCheck={false}
      inputMode={kind === 'afm' ? 'numeric' : 'text'}
      errorMessage={hasText(errorMessage) ? errorMessage : ownError}
      warningMessage={hasText(warningMessage) ? warningMessage : ownWarning}
      transientMessage={
        hasText(transientMessage)
          ? transientMessage
          : hint
            ? t('identifierField.converted')
            : undefined
      }
    />
  );
}
