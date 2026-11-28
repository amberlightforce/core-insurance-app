import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Dialog, Select, TextField, announce, toast } from '../../../design-system';
import { ProblemBanner } from '../../staff/ProblemBanner';
import {
  evidenceKinds,
  useSubmissionCommand,
  type SubmissionCommand,
  type SubmissionView,
} from './api';
import styles from './Submissions.module.css';

export type SubmissionAction =
  | 'request'
  | 'approve'
  | 'reject'
  | 'attach'
  | 'comment'
  | 'pack'
  | 'filing'
  | 'response'
  | 'resubmit';

const sha256 = /^[0-9a-f]{64}$/i;
const date = /^\d{4}-\d{2}-\d{2}$/;

/**
 * One lifecycle command of a submission in a dialog. The server decides what is allowed (state, maker-checker,
 * unverified due date, pack availability): its answer is shown in place and the dialog stays open.
 */
export function SubmissionActionDialog({
  action,
  submission,
  onClose,
}: {
  action: SubmissionAction;
  submission: SubmissionView;
  onClose: () => void;
}) {
  const { t } = useTranslation('compliance');
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const run = useSubmissionCommand();
  const id = submission.submissionId;
  const [text, setText] = useState('');
  const [kind, setKind] = useState<string>(evidenceKinds[0]);
  const [refId, setRefId] = useState('');
  const [hash, setHash] = useState('');
  const [label, setLabel] = useState('');
  const [from, setFrom] = useState(submission.periodFrom ?? '');
  const [to, setTo] = useState(submission.periodTo ?? '');
  const [subject, setSubject] = useState('');
  const [claim, setClaim] = useState('');
  const [channel, setChannel] = useState('PORTAL');
  const [reference, setReference] = useState('');
  const [receipt, setReceipt] = useState('');
  const [outcome, setOutcome] = useState('ACKNOWLEDGED');
  const [tried, setTried] = useState(false);

  const commentRequired = action === 'comment' || action === 'reject';
  const errors = {
    text: commentRequired && text.trim() === '',
    refId: action === 'attach' && refId.trim() === '',
    hash:
      (action === 'attach' && hash.trim() !== '' && !sha256.test(hash.trim())) ||
      (action === 'filing' && receipt.trim() !== '' && !sha256.test(receipt.trim())),
    dates:
      action === 'pack' && ((from !== '' && !date.test(from)) || (to !== '' && !date.test(to))),
    reference: action === 'filing' && reference.trim() === '',
    channel: action === 'filing' && channel.trim() === '',
  };
  const invalid = Object.values(errors).some(Boolean);
  const opt = (key: string, value: string) => (value.trim() ? { [key]: value.trim() } : {});

  const build = (): SubmissionCommand => {
    switch (action) {
      case 'request':
        return { name: 'approve', body: { id, decision: 'REQUEST', ...opt('comment', text) } };
      case 'approve':
        return { name: 'approve', body: { id, decision: 'APPROVE', ...opt('comment', text) } };
      case 'reject':
        return { name: 'approve', body: { id, decision: 'REJECT', comment: text.trim() } };
      case 'comment':
        return { name: 'comment', body: { id, text: text.trim() } };
      case 'attach':
        return {
          name: 'attach-content',
          body: { id, kind, refId: refId.trim(), ...opt('hash', hash), ...opt('label', label) },
        };
      case 'pack':
        return {
          name: 'evidence-pack',
          body: {
            id,
            ...opt('createdFrom', from),
            ...opt('createdTo', to),
            ...opt('subject', subject),
            ...opt('legalClaimReason', claim),
          },
        };
      case 'filing':
        return {
          name: 'record-filing',
          body: {
            submissionId: id,
            channel: channel.trim(),
            filingReference: reference.trim(),
            ...opt('receiptHash', receipt),
          },
        };
      case 'response':
        return {
          name: 'record-response',
          body: { id, outcome, ...opt('reference', reference), ...opt('note', text) },
        };
      case 'resubmit':
        return {
          name: 'create',
          body: {
            submissionType: submission.submissionType,
            regulator: submission.regulator,
            periodLabel: submission.periodLabel,
            ...(submission.periodFrom ? { periodFrom: submission.periodFrom } : {}),
            ...(submission.periodTo ? { periodTo: submission.periodTo } : {}),
            ...(submission.dueDate ? { dueDate: submission.dueDate } : {}),
            ...(submission.dueLegalStatus ? { dueLegalStatus: submission.dueLegalStatus } : {}),
            resubmitOf: id,
            ...opt('note', text),
          },
        };
    }
  };

  const submit = async () => {
    setTried(true);
    if (invalid) return;
    const command = build();
    // A rejected promise keeps the dialog open and is shown below by the mutation's error.
    const result = await run.mutateAsync({ command, key: keyFor(command) });
    release();
    const done = t(`submissions.actions.${action}.done`);
    announce(done);
    toast.success({ title: done, description: result.submission.number });
    onClose();
    if (action === 'resubmit')
      void navigate(`/compliance/submissions/${result.submission.submissionId}`);
  };

  const needsText = ['request', 'approve', 'reject', 'comment', 'resubmit', 'response'].includes(
    action,
  );
  const destructive = action === 'reject';

  return (
    <Dialog
      isOpen
      title={t(`submissions.actions.${action}.title`)}
      size="md"
      tone={destructive ? 'danger' : 'brand'}
      closeOnAction={false}
      isBusy={run.isPending}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      cancelLabel={t('submissions.actions.cancelButton')}
      primaryAction={{
        label: t(`submissions.actions.${action}.submit`),
        variant: destructive ? 'danger' : 'primary',
        onAction: submit,
      }}
    >
      <div className={styles.form}>
        <p>{t(`submissions.actions.${action}.lead`)}</p>
        {action === 'attach' ? (
          <>
            <Select
              label={t('submissions.evidence.kind')}
              options={evidenceKinds.map((k) => ({ id: k, label: t(`submissions.kind.${k}`) }))}
              value={kind}
              onChange={(v) => {
                if (v) setKind(v);
              }}
            />
            <TextField
              label={t('submissions.evidence.refId')}
              mono
              isRequired
              value={refId}
              onChange={setRefId}
              errorMessage={tried && errors.refId ? t('submissions.required') : undefined}
            />
            <TextField
              label={t('submissions.evidence.hash')}
              mono
              value={hash}
              onChange={setHash}
              errorMessage={tried && errors.hash ? t('submissions.hashInvalid') : undefined}
            />
            <TextField label={t('submissions.evidence.label')} value={label} onChange={setLabel} />
          </>
        ) : null}
        {action === 'pack' ? (
          <>
            <div className={styles.row}>
              <TextField
                label={t('submissions.pack.from')}
                helperText={t('submissions.dateHelp')}
                mono
                value={from}
                onChange={setFrom}
                errorMessage={tried && errors.dates ? t('submissions.dateInvalid') : undefined}
              />
              <TextField
                label={t('submissions.pack.to')}
                helperText={t('submissions.dateHelp')}
                mono
                value={to}
                onChange={setTo}
              />
            </div>
            <TextField
              label={t('submissions.pack.subject')}
              helperText={t('submissions.pack.subjectHelp')}
              mono
              value={subject}
              onChange={setSubject}
            />
            <TextField
              label={t('submissions.pack.claim')}
              helperText={t('submissions.pack.claimHelp')}
              value={claim}
              onChange={setClaim}
            />
          </>
        ) : null}
        {action === 'filing' ? (
          <>
            <TextField
              label={t('submissions.filing.channel')}
              isRequired
              value={channel}
              onChange={setChannel}
              errorMessage={tried && errors.channel ? t('submissions.required') : undefined}
            />
            <TextField
              label={t('submissions.filing.reference')}
              isRequired
              mono
              value={reference}
              onChange={setReference}
              errorMessage={tried && errors.reference ? t('submissions.required') : undefined}
            />
            <TextField
              label={t('submissions.filing.receiptHash')}
              mono
              value={receipt}
              onChange={setReceipt}
              errorMessage={tried && errors.hash ? t('submissions.hashInvalid') : undefined}
            />
          </>
        ) : null}
        {action === 'response' ? (
          <>
            <Select
              label={t('submissions.response.outcome')}
              options={['ACKNOWLEDGED', 'REJECTED'].map((o) => ({
                id: o,
                label: t(`submissions.status.${o}`),
              }))}
              value={outcome}
              onChange={(v) => {
                if (v) setOutcome(v);
              }}
            />
            <TextField
              label={t('submissions.response.reference')}
              mono
              value={reference}
              onChange={setReference}
            />
          </>
        ) : null}
        {needsText ? (
          <TextField
            label={t(commentRequired ? 'submissions.comment.label' : 'submissions.note')}
            isRequired={commentRequired}
            multiline
            minRows={2}
            value={text}
            onChange={setText}
            errorMessage={tried && errors.text ? t('submissions.required') : undefined}
          />
        ) : null}
        {run.isError ? (
          <ProblemBanner error={run.error} title={t(`submissions.actions.${action}.failed`)} />
        ) : null}
      </div>
    </Dialog>
  );
}
