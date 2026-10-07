import type { CalendarDate } from '@internationalized/date';
import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { Input, TextField } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { parseDateInput } from '../../../format/date-input';
import type { RegionFormat } from '../../../format/numbers';
import { cx } from '../../utils/cx';
import { formatLongDate } from './dateHelpers';
import styles from './DatePicker.module.css';

export interface QuickEntryProps {
  /** The character that opened the box (empty for Ctrl+Space). */
  initialText: string;
  today: CalendarDate;
  region: RegionFormat;
  /** A parsed date (Enter, or blur with a valid entry). */
  onCommit: (date: CalendarDate) => void;
  /** Closed without a date (Esc, or blur with an invalid entry). */
  onCancel: (reason: 'escape' | 'blur') => void;
}

/**
 * Date accelerators (Part 2 §4.5, D-FE-08): a small text box over the segments that accepts `σ`, `αύριο`,
 * `+30`, `+6μ`, `τμ`, `7/10`, `07102026`… with a live preview «→ Πέμπτη, 6 Νοεμβρίου 2026». Enter or blur
 * parses and sets the value; Esc closes without a change.
 */
export function QuickEntry({ initialText, today, region, onCommit, onCancel }: QuickEntryProps) {
  const { t } = useTranslation('ds');
  const [text, setText] = useState(initialText);
  const [showError, setShowError] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const previewId = useId();
  const parsed = parseDateInput(text, { today });

  useEffect(() => {
    const input = inputRef.current;
    if (!input) return;
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }, []);

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      e.stopPropagation();
      if (parsed) onCommit(parsed);
      else setShowError(true);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      onCancel('escape');
    }
  };

  let preview: string;
  if (parsed) preview = t('dateField.preview', { date: formatLongDate(parsed, region) });
  else if (text.trim() !== '' && showError) preview = t('dateField.notRecognised');
  else preview = t('dateField.hint');

  return (
    <div
      className={styles.quickEntry}
      onPointerDown={(e) => {
        e.stopPropagation();
      }}
    >
      <TextField
        aria-label={t('dateField.quickEntry')}
        aria-describedby={previewId}
        value={text}
        onChange={(value) => {
          setText(value);
          setShowError(false);
        }}
        isInvalid={showError && !parsed}
        className={cx(styles.quickEntryField)}
      >
        <Input
          ref={inputRef}
          className={cx(styles.quickEntryInput)}
          autoComplete="off"
          onKeyDown={handleKeyDown}
          onBlur={() => {
            if (parsed) onCommit(parsed);
            else onCancel('blur');
          }}
        />
      </TextField>
      <div
        id={previewId}
        className={styles.quickEntryPreview}
        data-error={(showError && !parsed) || undefined}
        aria-live="polite"
      >
        {preview}
      </div>
    </div>
  );
}
