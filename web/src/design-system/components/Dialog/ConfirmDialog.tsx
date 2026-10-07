import { TriangleAlert, UserCheck } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Input, Label, TextField } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { Dialog } from './Dialog';
import styles from './Dialog.module.css';
import { ReasonSelect, type ReasonOption } from './ReasonSelect';

/**
 * Proportional friction (DESIGN-B A.13): 1 recoverable · 2 contractual/financial · 3 regulated
 * (maker-checker). Level 0 (reversible) needs no dialog: use an undo toast.
 */
export type ConfirmLevel = 1 | 2 | 3;

export interface ConfirmConsequence {
  label: string;
  value: ReactNode;
}

export interface ConfirmDetails {
  reason: string | null;
}

export interface ConfirmDialogProps {
  level: ConfirmLevel;
  /** Action + object, e.g. «Ακύρωση ασφαλιστηρίου ΑΣΦ-2026-004471;». */
  title: string;
  /** One consequence line, consequence first («Το ασφαλιστήριο θα ακυρωθεί από 15/10/2026.»). */
  consequence?: ReactNode;
  /** Level 2–3 key–value summary: effective date, refund or charge, documents, notifications. */
  consequences?: ConfirmConsequence[];
  /** Level 2–3 required reason; or pass your own field in `reasonField` with `isReasonValid`. */
  reasons?: ReasonOption[];
  reasonField?: ReactNode;
  isReasonValid?: boolean;
  /** Type-to-confirm token (the last 4 characters of the business id), level 2–3. */
  confirmToken?: string;
  /** Danger button label: verb + object («Ακύρωση ασφαλιστηρίου»), never «Ναι». */
  confirmLabel: string;
  /** Level 3: the approver group named in the note. */
  approverGroup?: string;
  onConfirm: (details: ConfirmDetails) => unknown;
  isOpen?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
  error?: string;
  isBusy?: boolean;
}

/**
 * Destructive confirmation with friction proportional to impact (DESIGN-B A.13, Part 2 §4.18):
 * `role="alertdialog"`, «Άκυρο» first. Initial focus (D-FE-17): the first field when there is one (the
 * reason select at levels 2–3), otherwise «Άκυρο». Level 1: `sm`, one consequence line. Level 2: `md`,
 * consequences, a required reason and type-to-confirm; the danger button enables only on a match.
 * Level 3: level 2 sent for approval («Αποστολή για έγκριση» with the user-check icon).
 */
export function ConfirmDialog({
  level,
  title,
  consequence,
  consequences = [],
  reasons,
  reasonField,
  isReasonValid,
  confirmToken,
  confirmLabel,
  approverGroup,
  onConfirm,
  isOpen,
  defaultOpen,
  onOpenChange,
  error,
  isBusy,
}: ConfirmDialogProps) {
  const { t } = useTranslation('ds');
  const [reason, setReason] = useState<string | null>(null);
  const [typed, setTyped] = useState('');
  const [wasOpen, setWasOpen] = useState(isOpen);

  // Start every opening with an empty form (React's "adjust state on prop change" pattern).
  if (isOpen !== wasOpen) {
    setWasOpen(isOpen);
    if (isOpen) {
      setReason(null);
      setTyped('');
    }
  }

  const strict = level >= 2;
  const needsReason = strict && (reasons !== undefined || reasonField !== undefined);
  const reasonOk =
    !needsReason || (reasonField !== undefined ? isReasonValid === true : reason !== null);
  const tokenOk =
    !strict ||
    confirmToken === undefined ||
    typed.trim().toLocaleLowerCase('el') === confirmToken.toLocaleLowerCase('el');
  let blocker: string | undefined;
  if (!reasonOk) blocker = t('confirmDialog.reasonRequired');
  else if (!tokenOk) blocker = t('confirmDialog.typeToConfirm', { token: confirmToken });
  const approval = level === 3;

  return (
    <Dialog
      title={title}
      icon={approval ? UserCheck : TriangleAlert}
      tone={approval ? 'warning' : 'danger'}
      size={strict ? 'md' : 'sm'}
      isDestructive
      {...defined({ isOpen, defaultOpen, error, isBusy })}
      onOpenChange={(open) => {
        if (!open) {
          setReason(null);
          setTyped('');
        }
        onOpenChange?.(open);
      }}
      primaryAction={{
        label: approval ? t('confirmDialog.sendForApproval') : confirmLabel,
        variant: approval ? 'primary' : 'danger',
        ...(approval ? { icon: UserCheck } : {}),
        ...(blocker ? { disabledReason: blocker } : {}),
        onAction: () => onConfirm({ reason }),
      }}
    >
      {consequence ? <p className={styles.consequence}>{consequence}</p> : null}
      {strict && consequences.length > 0 ? (
        <section aria-label={t('confirmDialog.consequences')}>
          <dl className={styles.facts}>
            {consequences.map((item) => (
              <div key={item.label} style={{ display: 'contents' }}>
                <dt>{item.label}</dt>
                <dd>{item.value}</dd>
              </div>
            ))}
          </dl>
        </section>
      ) : null}
      {strict && reasonField !== undefined ? reasonField : null}
      {strict && reasonField === undefined && reasons ? (
        <ReasonSelect
          label={t('confirmDialog.reasonLabel')}
          placeholder={t('confirmDialog.reasonPlaceholder')}
          options={reasons}
          selectedKey={reason}
          onSelectionChange={setReason}
        />
      ) : null}
      {strict && confirmToken !== undefined ? (
        <TextField
          className={cx(styles.field)}
          value={typed}
          onChange={setTyped}
          autoComplete="off"
          spellCheck="false"
        >
          <Label className={cx(styles.fieldLabel)}>
            {t('confirmDialog.typeToConfirm', { token: confirmToken })}
          </Label>
          <Input className={cx(styles.input)} />
        </TextField>
      ) : null}
      {approval ? (
        <p className={styles.note}>
          <Icon icon={UserCheck} size={16} />
          <span>
            {approverGroup
              ? t('confirmDialog.approvalNoteGroup', { group: approverGroup })
              : t('confirmDialog.approvalNote')}
          </span>
        </p>
      ) : null}
    </Dialog>
  );
}
