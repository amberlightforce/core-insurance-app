import { Check, CircleAlert, Pencil, RotateCcw } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Input, TextField } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import styles from './KeyValueList.module.css';

export interface InlineEdit {
  /** The raw editable text. */
  value: string;
  /** Persists the change; reject (with an Error whose message is user-facing) to revert. */
  onCommit: (next: string) => Promise<void>;
  /** Client-side check; return a message (what, where, how to fix) or null. */
  validate?: (next: string) => string | null;
  /** Formats the raw value for display while saving; defaults to the raw text. */
  format?: (raw: string) => ReactNode;
}

interface EditableValueProps {
  label: string;
  display: ReactNode;
  edit: InlineEdit;
  mono?: boolean;
}

type Mode = 'view' | 'edit' | 'saving';

const RECEIPT_MS = 1400;

/**
 * IB-14 inline edit: pencil on hover, click or Enter swaps in an input at identical metrics (MI-23), Enter
 * commits, Esc cancels, MI-09 «Αποθηκεύτηκε» receipt on success, revert + danger message + «Επανάληψη» on failure.
 */
export function EditableValue({ label, display, edit, mono = false }: EditableValueProps) {
  const { t } = useTranslation('ds');
  const [mode, setMode] = useState<Mode>('view');
  const [draft, setDraft] = useState(edit.value);
  const [invalid, setInvalid] = useState<string | null>(null);
  const [failure, setFailure] = useState<{ message: string; attempted: string } | null>(null);
  const [saved, setSaved] = useState(false);
  const pencil = useRef<HTMLButtonElement>(null);
  const restoreFocus = useRef(false);
  /** Set when Enter or Esc already decided the outcome, so the input's blur does not commit again. */
  const settled = useRef(false);
  const errorId = useId();

  useEffect(() => {
    if (mode === 'view' && restoreFocus.current) {
      restoreFocus.current = false;
      pencil.current?.focus();
    }
  }, [mode]);

  useEffect(() => {
    if (!saved) return;
    const id = setTimeout(() => {
      setSaved(false);
    }, RECEIPT_MS);
    return () => {
      clearTimeout(id);
    };
  }, [saved]);

  const start = () => {
    setDraft(failure?.attempted ?? edit.value);
    setInvalid(null);
    setFailure(null);
    setSaved(false);
    settled.current = false;
    setMode('edit');
  };

  const cancel = () => {
    restoreFocus.current = true;
    setInvalid(null);
    setMode('view');
  };

  const commit = async (next: string) => {
    const message = edit.validate?.(next) ?? null;
    if (message) {
      setInvalid(message);
      return;
    }
    if (next === edit.value) {
      cancel();
      return;
    }
    restoreFocus.current = true;
    setDraft(next);
    setMode('saving');
    setFailure(null);
    try {
      await edit.onCommit(next);
      setMode('view');
      setSaved(true);
      announce(t('keyValueList.savedField', { label }));
    } catch (error) {
      const reason = error instanceof Error && error.message ? error.message : null;
      setMode('view');
      setFailure({ message: reason ?? t('keyValueList.saveFailed'), attempted: next });
      announce(t('keyValueList.saveFailedField', { label }), 'assertive');
    }
  };

  if (mode === 'edit') {
    return (
      <TextField
        className={cx(styles.editor)}
        aria-label={label}
        value={draft}
        onChange={(value) => {
          setDraft(value);
          setInvalid(null);
        }}
        isInvalid={invalid !== null}
        autoFocus
        {...(invalid ? { 'aria-describedby': errorId } : {})}
      >
        <Input
          className={cx(styles.input, mono && styles.mono)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              settled.current = true;
              void commit(draft).then(() => {
                settled.current = false;
              });
            } else if (event.key === 'Escape') {
              event.preventDefault();
              event.stopPropagation();
              settled.current = true;
              cancel();
            }
          }}
          onBlur={() => {
            if (!settled.current && invalid === null) void commit(draft);
          }}
        />
        {invalid ? (
          <span id={errorId} className={styles.fieldError}>
            <Icon icon={CircleAlert} size={12} />
            {invalid}
          </span>
        ) : null}
        <span className={styles.hint}>{t('keyValueList.editHint')}</span>
      </TextField>
    );
  }

  const shown = mode === 'saving' ? (edit.format ? edit.format(draft) : draft) : display;

  return (
    <span
      className={styles.editable}
      data-saving={mode === 'saving' || undefined}
      aria-busy={mode === 'saving' || undefined}
    >
      <span className={styles.valueRow}>
        <span
          className={cx(styles.valueText, mono && styles.mono)}
          onClick={mode === 'view' ? start : undefined}
          data-saved={saved || undefined}
        >
          {shown}
        </span>
        <span className={styles.pencil}>
          <Button
            ref={pencil}
            variant="ghost"
            size="sm"
            icon={Pencil}
            label={t('keyValueList.edit', { label })}
            onPress={start}
            {...(mode === 'saving' ? { isLoading: true } : {})}
          />
        </span>
        {saved ? (
          <span className={styles.receipt}>
            <Icon icon={Check} size={12} />
            {t('keyValueList.saved')}
          </span>
        ) : null}
      </span>
      {failure ? (
        <span className={styles.failure} role="alert">
          <Icon icon={CircleAlert} size={12} />
          {failure.message}
          <Button
            variant="link"
            size="sm"
            icon={RotateCcw}
            onPress={() => {
              void commit(failure.attempted);
            }}
          >
            {t('keyValueList.retry')}
          </Button>
        </span>
      ) : null}
    </span>
  );
}
