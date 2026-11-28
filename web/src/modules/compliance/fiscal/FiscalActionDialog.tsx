import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Banner, Dialog, TextField, announce, toast } from '../../../design-system';
import { ProblemBanner } from '../../staff/ProblemBanner';
import {
  useFiscalCommand,
  type CorrectResult,
  type FiscalCommand,
  type FiscalDocument,
} from './api';
import styles from './Fiscal.module.css';

export type FiscalAction = 'resubmit' | 'cancel' | 'correct';

const amountPattern = /^-?\d+(\.\d{1,4})?$/;

/** The server sends four decimals ("121.5000"); show the amount the way it is typed, at least two decimals. */
const tidy = (amount: string): string => {
  const [whole = '', fraction = ''] = amount.split('.');
  const trimmed = fraction.replace(/0+$/, '').padEnd(2, '0');
  return `${whole}.${trimmed}`;
};

/**
 * Reason dialog of one BO-CMP-06 command on a fiscal document (resubmit a rejected one, cancel, or correct with a
 * credit note plus a replacement). The server decides what is allowed (NOT-REJECTED, NOT-CANCELLABLE,
 * NOT-CORRECTABLE, FISCAL-TOTAL): its answer is shown in place and the dialog stays open so the input is kept.
 */
export function FiscalActionDialog({
  action,
  data,
  onClose,
}: {
  action: FiscalAction;
  data: FiscalDocument;
  onClose: () => void;
}) {
  const { t } = useTranslation('compliance');
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const run = useFiscalCommand();
  const doc = data.document;
  const [reason, setReason] = useState('');
  const [amounts, setAmounts] = useState<string[]>(() =>
    doc.lines.map((l) => tidy(l.amount.amount)),
  );
  const [tried, setTried] = useState(false);

  const reasonValid = reason.trim().length > 0 && reason.trim().length <= 500;
  const amountsValid = action !== 'correct' || amounts.every((a) => amountPattern.test(a.trim()));
  const destructive = action === 'cancel';

  const submit = async () => {
    setTried(true);
    if (!reasonValid || !amountsValid) return;
    const base = { fiscalDocumentId: doc.fiscalDocumentId, reason: reason.trim() };
    const command: FiscalCommand =
      action === 'correct'
        ? {
            name: 'correct',
            body: {
              ...base,
              corrections: doc.lines.map((l, i) => ({
                fiscalCategoryKey: l.fiscalCategoryKey,
                amount: {
                  amount: (amounts[i] ?? l.amount.amount).trim(),
                  currency: l.amount.currency,
                },
                ...(l.chargeId ? { chargeId: l.chargeId } : {}),
              })),
            },
          }
        : { name: action, body: base };
    // A rejected promise keeps the dialog open and is shown below by the mutation's error.
    const result = await run.mutateAsync({ command, key: keyFor(command) });
    release();
    const done = t(`fiscal.actions.${action}.done`);
    announce(done);
    toast.success({ title: done, description: doc.fiscalDocumentId });
    onClose();
    if (action === 'correct') {
      void navigate(`/compliance/fiscal/${(result as CorrectResult).correctedFiscalDocumentId}`);
    }
  };

  return (
    <Dialog
      isOpen
      title={t(`fiscal.actions.${action}.title`)}
      size={action === 'correct' ? 'lg' : 'md'}
      tone={destructive ? 'danger' : 'brand'}
      closeOnAction={false}
      isBusy={run.isPending}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      cancelLabel={t('fiscal.actions.cancelButton')}
      primaryAction={{
        label: t(`fiscal.actions.${action}.submit`),
        variant: destructive ? 'danger' : 'primary',
        onAction: submit,
      }}
    >
      <div className={styles.form}>
        <p>{t(`fiscal.actions.${action}.lead`)}</p>
        {action === 'cancel' ? (
          <Banner variant="warning" live="none" title={t('fiscal.actions.cancel.warnTitle')}>
            {t('fiscal.actions.cancel.warn')}
          </Banner>
        ) : null}
        {action === 'correct'
          ? doc.lines.map((line, i) => (
              <TextField
                key={`${line.fiscalCategoryKey}-${line.chargeId ?? ''}-${String(i)}`}
                label={t('fiscal.actions.correct.amountFor', {
                  category: line.fiscalCategoryKey,
                  currency: line.amount.currency,
                })}
                mono
                value={amounts[i] ?? ''}
                onChange={(v) => {
                  setAmounts((prev) => prev.map((a, j) => (j === i ? v : a)));
                }}
                errorMessage={
                  tried && !amountPattern.test((amounts[i] ?? '').trim())
                    ? t('fiscal.actions.correct.amountInvalid')
                    : undefined
                }
              />
            ))
          : null}
        <TextField
          label={t('fiscal.actions.reason')}
          helperText={t('fiscal.actions.reasonHelp')}
          isRequired
          multiline
          minRows={2}
          value={reason}
          onChange={setReason}
          errorMessage={tried && !reasonValid ? t('fiscal.actions.reasonInvalid') : undefined}
        />
        {run.isError ? (
          <ProblemBanner error={run.error} title={t(`fiscal.actions.${action}.failed`)} />
        ) : null}
      </div>
    </Dialog>
  );
}
