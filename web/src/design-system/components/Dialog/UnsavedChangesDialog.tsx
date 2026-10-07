import { TriangleAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { defined } from '../../utils/cx';
import { Dialog } from './Dialog';
import styles from './Dialog.module.css';

const maxListed = 5;

export interface UnsavedChangesDialogProps {
  isOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
  /** Labels of the changed fields; up to 5 are listed, then «και {n} ακόμη». */
  changedFields: string[];
  /** «Απόρριψη αλλαγών»: drop the edits and leave. */
  onDiscard: () => void;
  /** «Αποθήκευση και έξοδος»: may return a promise; the dialog stays busy until it settles. */
  onSave: () => unknown;
  /** «Συνέχεια επεξεργασίας» (default focus): stay. */
  onContinue?: () => void;
  error?: string;
}

/**
 * Leaving with unsaved changes (DESIGN-B A.12): `sm` modal «Υπάρχουν μη αποθηκευμένες αλλαγές» listing the
 * changed fields; «Συνέχεια επεξεργασίας» (focused) · «Απόρριψη αλλαγών» (danger-ghost) ·
 * «Αποθήκευση και έξοδος» (primary).
 */
export function UnsavedChangesDialog({
  isOpen,
  onOpenChange,
  changedFields,
  onDiscard,
  onSave,
  onContinue,
  error,
}: UnsavedChangesDialogProps) {
  const { t } = useTranslation('ds');
  const listed = changedFields.slice(0, maxListed);
  const rest = changedFields.length - listed.length;

  return (
    <Dialog
      title={t('unsavedChanges.title')}
      icon={TriangleAlert}
      tone="warning"
      size="sm"
      initialFocus="cancel"
      cancelLabel={t('unsavedChanges.continue')}
      {...defined({ isOpen, onOpenChange, onCancel: onContinue, error })}
      secondaryActions={[
        { label: t('unsavedChanges.discard'), variant: 'danger-ghost', onAction: onDiscard },
      ]}
      primaryAction={{
        label: t('unsavedChanges.saveAndExit'),
        variant: 'primary',
        onAction: onSave,
      }}
    >
      {listed.length > 0 ? (
        <>
          <p>{t('unsavedChanges.intro')}</p>
          <ul className={styles.changed}>
            {listed.map((field) => (
              <li key={field}>{field}</li>
            ))}
            {rest > 0 ? <li>{t('unsavedChanges.more', { count: rest })}</li> : null}
          </ul>
        </>
      ) : null}
    </Dialog>
  );
}
