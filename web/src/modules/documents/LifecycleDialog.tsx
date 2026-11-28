import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Dialog, Select, TextField } from '../../design-system';
import { ProblemBanner } from '../staff/ProblemBanner';
import {
  documentMedia,
  useDocumentLifecycle,
  type DocumentView,
  type LifecycleAction,
} from './api';
import local from './documents.module.css';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export interface LifecycleDialogProps {
  action: LifecycleAction;
  document: DocumentView;
  onClose: () => void;
  onDone: (action: LifecycleAction) => void;
}

/**
 * Reason dialog of one lifecycle command (resend, hide, unhide, supersede, withdraw). The server decides who may do
 * what (a statutory notice needs Platform.Admin, a channel may be refused, a restricted subject is blocked): its
 * answer is shown in place and the dialog stays open so the input is kept.
 */
export function LifecycleDialog({ action, document, onClose, onDone }: LifecycleDialogProps) {
  const { t } = useTranslation('documents');
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState('');
  const [replacement, setReplacement] = useState('');
  const [channel, setChannel] = useState<string>(document.medium);
  const [tried, setTried] = useState(false);
  const mutation = useDocumentLifecycle(document.documentId, (done) => {
    release();
    onDone(done);
  });

  const reasonValid = reason.trim().length >= 3;
  const replacementValid = action !== 'supersede' || uuid.test(replacement.trim());
  const valid = reasonValid && replacementValid;
  const destructive = action === 'withdraw' || action === 'supersede';

  const submit = async () => {
    setTried(true);
    if (!valid) return;
    const input = {
      action,
      reason: reason.trim(),
      ...(action === 'supersede' ? { replacementDocumentId: replacement.trim() } : {}),
      ...(action === 'resend' ? { channel } : {}),
    };
    try {
      await mutation.mutateAsync({ ...input, key: keyFor(input) });
    } catch {
      // Shown below with the server's explanation; the dialog stays open.
    }
  };

  return (
    <Dialog
      isOpen
      title={t(`lifecycle.${action}.title`, { number: document.documentNumber })}
      size="md"
      tone={destructive ? 'danger' : 'brand'}
      closeOnAction={false}
      isBusy={mutation.isPending}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      cancelLabel={t('lifecycle.cancel')}
      primaryAction={{
        label: t(`lifecycle.${action}.submit`),
        variant: destructive ? 'danger' : 'primary',
        onAction: submit,
      }}
    >
      <div className={local.dialogForm}>
        <p>{t(`lifecycle.${action}.lead`)}</p>
        {action === 'resend' ? (
          <Select
            label={t('lifecycle.channel')}
            helperText={t('lifecycle.channelHelp')}
            options={documentMedia.map((m) => ({ id: m, label: t(`media.${m}`) }))}
            value={channel}
            onChange={(v) => {
              if (v) setChannel(v);
            }}
          />
        ) : null}
        {action === 'supersede' ? (
          <TextField
            label={t('lifecycle.replacement')}
            helperText={t('lifecycle.replacementHelp')}
            mono
            isRequired
            value={replacement}
            onChange={setReplacement}
            errorMessage={
              tried && !replacementValid ? t('lifecycle.replacementInvalid') : undefined
            }
          />
        ) : null}
        <TextField
          label={t('lifecycle.reason')}
          isRequired
          value={reason}
          onChange={setReason}
          errorMessage={tried && !reasonValid ? t('lifecycle.reasonInvalid') : undefined}
        />
        {mutation.isError ? (
          <ProblemBanner error={mutation.error} title={t(`lifecycle.${action}.failed`)} />
        ) : null}
      </div>
    </Dialog>
  );
}
