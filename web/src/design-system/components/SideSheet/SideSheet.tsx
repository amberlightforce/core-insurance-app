import { Pencil, X } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { FocusScope, useDialog } from 'react-aria';
import { useTranslation } from 'react-i18next';

import { cx } from '../../utils/cx';
import { Banner } from '../Banner';
import { Button } from '../Button';
import { UnsavedChangesDialog } from '../Dialog';
import { matchesShortcut } from '../Kbd';
import { firstFieldIn } from '../Popover/focusables';
import styles from './SideSheet.module.css';

/** md 480 · lg 640 · split 50 % (document + form) (Part 2 §4.20). */
export type SideSheetSize = 'md' | 'lg' | 'split';

export interface SideSheetProps {
  isOpen: boolean;
  /** Called when the sheet should close (after the dirty check). */
  onClose: () => void;
  title: string;
  /** Context chip text, e.g. «ΑΣΦ-2026-004471» → «για ΑΣΦ-2026-004471». */
  context?: string;
  size?: SideSheetSize;
  children: ReactNode;
  /** Ctrl/⌘+S and the primary button. May return a promise. */
  onSave?: () => unknown;
  saveLabel?: string;
  /** Unsaved edits: shows the 6 px warning dot (MI-64) and asks before closing. */
  isDirty?: boolean;
  /** Changed field labels for the unsaved-changes dialog. */
  changedFields?: string[];
  /** Called when the user discards changes in the unsaved-changes dialog (before `onClose`). */
  onDiscard?: () => void;
  isBusy?: boolean;
  /** Summary banner at the top of the body; focus moves to it. */
  error?: string;
  /** View mode: no footer; «Επεξεργασία» in the header when `onEdit` is given. */
  isReadOnly?: boolean;
  onEdit?: () => void;
  className?: string;
}

/**
 * Side sheet (Part 2 §4.20): a non-modal dialog (`role="dialog" aria-modal="false"`, `useDialog` +
 * `FocusScope` without a trap) placed inside the main sheet region — render it in a positioned container.
 * Opaque sheet with a left `elevation.4`; the record underneath dims but stays readable and scrollable.
 * Esc and × check for unsaved changes; Ctrl/⌘+S saves; Ctrl/⌘+Enter saves and closes. Focus goes to the
 * first field on open and back to the trigger on close.
 *
 * At most one side sheet is open at a time: opening another replaces it, so the caller keeps a single
 * "current sheet" state and runs the same dirty check (this component's `isDirty` + `UnsavedChangesDialog`)
 * before swapping.
 */
export function SideSheet(props: SideSheetProps) {
  if (!props.isOpen) return null;
  return <OpenSideSheet {...props} />;
}

function OpenSideSheet({
  onClose,
  title,
  context,
  size = 'md',
  children,
  onSave,
  saveLabel,
  isDirty = false,
  changedFields = [],
  onDiscard,
  isBusy = false,
  error,
  isReadOnly = false,
  onEdit,
  className,
}: SideSheetProps) {
  const { t } = useTranslation('ds');
  const ref = useRef<HTMLDivElement | null>(null);
  const bodyRef = useRef<HTMLDivElement | null>(null);
  const titleId = useId();
  const [confirming, setConfirming] = useState(false);
  const { dialogProps, titleProps } = useDialog({ 'aria-labelledby': titleId }, ref);

  // Focus the first field on open (useDialog has already focused the sheet itself).
  useEffect(() => {
    const body = bodyRef.current;
    // An error banner that already took focus wins.
    if (!body || body.contains(document.activeElement)) return;
    firstFieldIn(body)?.focus();
  }, []);

  const requestClose = () => {
    if (isBusy) return;
    if (isDirty) setConfirming(true);
    else onClose();
  };

  const save = (thenClose: boolean) => {
    if (!onSave || isBusy || isReadOnly) return;
    const result = onSave();
    if (!thenClose) return;
    Promise.resolve(result).then(onClose, () => undefined);
  };

  return (
    <>
      <div className={styles.dim} aria-hidden="true" />
      <FocusScope restoreFocus>
        <div
          {...dialogProps}
          ref={ref}
          aria-modal="false"
          aria-busy={isBusy || undefined}
          className={cx(styles.sheet, className)}
          data-size={size}
          data-readonly={isReadOnly || undefined}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              event.stopPropagation();
              requestClose();
            } else if (matchesShortcut(event, 'Mod+s')) {
              event.preventDefault();
              save(false);
            } else if (matchesShortcut(event, 'Mod+Enter')) {
              event.preventDefault();
              save(true);
            }
          }}
        >
          <div className={styles.header}>
            <h2 {...titleProps} id={titleId} className={styles.title}>
              {title}
              {isDirty ? (
                <span className={styles.dirty}>
                  <span className="ds-visually-hidden">{t('sideSheet.unsaved')}</span>
                </span>
              ) : null}
            </h2>
            {context ? (
              <span className={styles.chip}>{t('sideSheet.context', { context })}</span>
            ) : null}
            {isReadOnly && onEdit ? (
              <Button size="sm" icon={Pencil} onPress={onEdit}>
                {t('sideSheet.edit')}
              </Button>
            ) : null}
            <Button
              variant="ghost"
              size="sm"
              icon={X}
              label={t('sideSheet.close')}
              onPress={requestClose}
            />
          </div>
          <div ref={bodyRef} className={styles.body}>
            {error ? (
              <Banner key={error} variant="danger" title={t('sideSheet.errorTitle')} autoFocus>
                {error}
              </Banner>
            ) : null}
            {children}
          </div>
          {isReadOnly ? null : (
            <div className={styles.footer}>
              <Button
                variant="secondary"
                {...(isBusy ? { disabledReason: t('dialog.busy') } : { onPress: requestClose })}
              >
                {t('sideSheet.cancel')}
              </Button>
              {onSave ? (
                <Button
                  variant="primary"
                  isLoading={isBusy}
                  shortcut="Mod+S"
                  onPress={() => {
                    save(false);
                  }}
                >
                  {saveLabel ?? t('sideSheet.save')}
                </Button>
              ) : null}
            </div>
          )}
        </div>
      </FocusScope>
      <UnsavedChangesDialog
        isOpen={confirming}
        onOpenChange={setConfirming}
        changedFields={changedFields}
        onDiscard={() => {
          onDiscard?.();
          onClose();
        }}
        onSave={() => Promise.resolve(onSave?.()).then(onClose)}
      />
    </>
  );
}
