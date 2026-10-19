import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, TextField, announce, toast } from '../../design-system';
import { useBatchCommand, type BatchCommand, type BatchView } from './api';
import styles from './Migration.module.css';
import { ProblemBanner } from './ProblemBanner';

type Pending = 'startReal' | 'reverseReal' | 'approve' | 'reject' | null;

/**
 * The batch decision bar. Draft: dry run, then (once the dry run matches the content) start, which asks for the
 * MIG.BATCH_RUN approval. Pending approval: a second manager approves or rejects (maker ≠ checker is the server's
 * rule; its refusal is shown as is). Completed: dry-run the reversal, then reverse. Buttons only reflect state.
 */
export function BatchActions({ batch }: { batch: BatchView }) {
  const { t } = useTranslation('migration');
  const { keyFor, release } = useIdempotencyKey();
  const run = useBatchCommand(batch.batchId);
  const [pending, setPending] = useState<Pending>(null);
  const [comment, setComment] = useState('');
  const id = batch.batchId;
  const busy = run.isPending;

  const send = (command: BatchCommand, done: string) => {
    run.mutate(
      { command, key: keyFor(command) },
      {
        onSuccess: () => {
          release();
          setPending(null);
          setComment('');
          announce(t(done));
          toast.success({ title: t(done), description: batch.name });
        },
      },
    );
  };
  const sendDry = (action: 'start' | 'reverse', dryRun: boolean) => {
    send(
      { action, body: { batchId: id, dryRun } },
      dryRun ? `done.${action}DryRun` : `done.${action}`,
    );
  };
  const decide = (decision: 'APPROVE' | 'REJECT') => {
    const text = comment.trim();
    send(
      { action: 'approve', body: { batchId: id, decision, ...(text ? { comment: text } : {}) } },
      decision === 'APPROVE' ? 'done.approve' : 'done.reject',
    );
  };
  const pause = () => {
    send({ action: 'pause', body: { batchId: id } }, 'done.pause');
  };

  const confirmBar = (text: string, confirmLabel: string, onConfirm: () => void) => (
    <div className={styles.stack}>
      <Banner variant="info" live="none" title={t('confirm.title')}>
        {text}
      </Banner>
      <div className={styles.actions}>
        <Button variant="commit" isLoading={busy} onPress={onConfirm}>
          {confirmLabel}
        </Button>
        <Button
          variant="ghost"
          isDisabled={busy}
          onPress={() => {
            setPending(null);
          }}
        >
          {t('actions.cancel')}
        </Button>
      </div>
    </div>
  );

  let body;
  switch (batch.status) {
    case 'DRAFT':
      body =
        pending === 'startReal' ? (
          confirmBar(t('confirm.start'), t('actions.confirmStart'), () => {
            sendDry('start', false);
          })
        ) : (
          <div className={styles.stack}>
            <p className="ds-caption">
              {batch.dryRunDone ? t('help.dryRunDone') : t('help.dryRunNeeded')}
            </p>
            <div className={styles.actions}>
              <Button
                variant="secondary"
                isLoading={busy}
                onPress={() => {
                  sendDry('start', true);
                }}
              >
                {t('actions.dryRun')}
              </Button>
              <Button
                variant="commit"
                isDisabled={busy || !batch.dryRunDone}
                onPress={() => {
                  setPending('startReal');
                }}
              >
                {t('actions.start')}
              </Button>
              <Button variant="ghost" isDisabled={busy} onPress={pause}>
                {t('actions.pause')}
              </Button>
            </div>
          </div>
        );
      break;
    case 'PENDING_APPROVAL':
      body =
        pending === 'approve' || pending === 'reject' ? (
          <form
            className={styles.form}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              decide(pending === 'approve' ? 'APPROVE' : 'REJECT');
            }}
          >
            <TextField
              label={t('approval.comment')}
              multiline
              minRows={2}
              autoFocus
              value={comment}
              onChange={setComment}
            />
            <div className={styles.actions}>
              <Button
                type="submit"
                variant={pending === 'approve' ? 'commit' : 'primary'}
                isLoading={busy}
              >
                {t(pending === 'approve' ? 'actions.confirmApprove' : 'actions.confirmReject')}
              </Button>
              <Button
                variant="ghost"
                isDisabled={busy}
                onPress={() => {
                  setPending(null);
                }}
              >
                {t('actions.cancel')}
              </Button>
            </div>
          </form>
        ) : (
          <div className={styles.stack}>
            <p className="ds-caption">
              {t('help.pendingApproval', { who: batch.startedBy ?? '—' })}
            </p>
            <div className={styles.actions}>
              <Button
                variant="secondary"
                onPress={() => {
                  setPending('reject');
                }}
              >
                {t('actions.reject')}
              </Button>
              <Button
                variant="commit"
                onPress={() => {
                  setPending('approve');
                }}
              >
                {t('actions.approve')}
              </Button>
              <Button variant="ghost" isDisabled={busy} onPress={pause}>
                {t('actions.pause')}
              </Button>
            </div>
          </div>
        );
      break;
    case 'PAUSED':
      body = (
        <div className={styles.actions}>
          <Button
            variant="primary"
            isLoading={busy}
            onPress={() => {
              send({ action: 'resume', body: { batchId: id } }, 'done.resume');
            }}
          >
            {t('actions.resume')}
          </Button>
        </div>
      );
      break;
    case 'COMPLETED':
    case 'COMPLETED_WITH_ERRORS':
    case 'REVERSAL_BLOCKED':
    case 'NOT_REVERSIBLE':
      body =
        pending === 'reverseReal' ? (
          confirmBar(t('confirm.reverse'), t('actions.confirmReverse'), () => {
            sendDry('reverse', false);
          })
        ) : (
          <div className={styles.stack}>
            <p className="ds-caption">{t('help.reverse')}</p>
            <div className={styles.actions}>
              <Button
                variant="secondary"
                isLoading={busy}
                onPress={() => {
                  sendDry('reverse', true);
                }}
              >
                {t('actions.reverseDryRun')}
              </Button>
              <Button
                variant="primary"
                isDisabled={busy}
                onPress={() => {
                  setPending('reverseReal');
                }}
              >
                {t('actions.reverse')}
              </Button>
            </div>
          </div>
        );
      break;
    default:
      body = <p className="ds-caption">{t(`final.${batch.status}`)}</p>;
  }

  return (
    <div className={styles.stack}>
      {body}
      {run.isError ? <ProblemBanner error={run.error} /> : null}
    </div>
  );
}
