import { GitCompare } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { ClaimView } from '../../../api/types';
import {
  announce,
  Banner,
  Button,
  Dialog,
  Radio,
  RadioGroup,
  Select,
  TextField,
} from '../../../design-system';
import { useFormat } from '../../staff/useFormat';
import styles from '../../staff/staff.module.css';
import { useReverify, useSnapshot } from './api';
import { ReverifyProblem } from './ReverifyProblem';
import { reverifyDecisions, reverifyReasons, type ReverifyDecision } from './reasons';
import file from './reverify.module.css';
import { SnapshotCompare } from './SnapshotCompare';

function DecisionForm({ claim, onDone }: { claim: ClaimView; onDone: () => void }) {
  const { t } = useTranslation('claims');
  const pending = claim.pendingReverification;
  const { keyFor, release } = useIdempotencyKey();
  const [decision, setDecision] = useState<ReverifyDecision | null>(null);
  const [reason, setReason] = useState<string | null>(null);
  const [comment, setComment] = useState('');
  const [tried, setTried] = useState(false);
  const mutation = useReverify(claim.summary.claimId);
  // The form is offered only once both versions can be shown: a decision is never taken blind.
  const before = useSnapshot(pending?.oldSnapshotRef ?? '', pending !== undefined);
  const after = useSnapshot(pending?.newSnapshotRef ?? '', pending !== undefined);
  if (!pending) return null;
  const ready = Boolean(before.data && after.data);

  const submit = () => {
    setTried(true);
    if (!decision || !reason) return;
    const request = {
      claimId: claim.summary.claimId,
      decision,
      reasonCode: reason,
      ...(comment.trim() ? { comment: comment.trim() } : {}),
      // The decision is taken against this successor; a later supersession fails with CLM-ERR-SNAPSHOT-MISMATCH.
      expectedNewSnapshotRef: pending.newSnapshotRef,
    };
    mutation.mutate(
      { request, key: keyFor(request) },
      {
        onSuccess: (response) => {
          release();
          announce(t(`reverify.done.${response.decision}`));
          onDone();
        },
      },
    );
  };

  return (
    <>
      <SnapshotCompare oldRef={pending.oldSnapshotRef} newRef={pending.newSnapshotRef} />
      {ready ? (
        <form
          noValidate
          className={file.form}
          aria-label={t('reverify.decision.title')}
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <p className={styles.muted}>{t('reverify.decision.intro')}</p>
          <RadioGroup
            label={t('reverify.decision.label')}
            isRequired
            value={decision}
            onChange={(value) => {
              setDecision(value as ReverifyDecision);
              setReason(null);
            }}
            errorMessage={tried && !decision ? t('reverify.errors.decision') : undefined}
          >
            {reverifyDecisions.map((d) => (
              <Radio key={d} value={d} description={t(`reverify.decision.${d}.help`)}>
                {t(`reverify.decision.${d}.label`)}
              </Radio>
            ))}
          </RadioGroup>
          <Select
            label={t('reverify.reason.label')}
            isRequired
            isDisabled={!decision}
            options={(decision ? reverifyReasons[decision] : []).map((r) => ({
              id: r,
              label: t(`reverify.reason.codes.${r}`),
            }))}
            value={reason}
            onChange={setReason}
            errorMessage={tried && decision && !reason ? t('reverify.errors.reason') : undefined}
          />
          <TextField
            label={t('reverify.comment.label')}
            multiline
            value={comment}
            onChange={setComment}
          />
          {mutation.isError ? <ReverifyProblem error={mutation.error} /> : null}
          <div className={styles.actions}>
            <Button type="submit" variant="primary" isLoading={mutation.isPending}>
              {t('reverify.decision.submit')}
            </Button>
          </div>
        </form>
      ) : null}
    </>
  );
}

/**
 * The «Φάκελος ζημίας» alert slot for the coverage basis (D-SL3-03): CLM never changes the claim by itself. When POL
 * superseded the policy snapshot the claim was verified against, the handler compares old and new and keeps or adopts.
 */
export function ReverifyBanner({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const [open, setOpen] = useState(false);
  const status = claim.summary.snapshotStatus;
  const pending = claim.pendingReverification;
  if (status === 'VERIFIED') return null;

  if (status !== 'REVERIFICATION_REQUIRED' || !pending) {
    return (
      <Banner variant="info" live="none" title={t('view.snapshot.notVerifiedTitle')}>
        {t(`view.snapshot.status.${status}`)}
      </Banner>
    );
  }

  return (
    <>
      <Banner
        variant="warning"
        live="none"
        title={t('reverify.banner.title')}
        actions={
          <Button
            variant="secondary"
            size="sm"
            onPress={() => {
              setOpen(true);
            }}
          >
            {t('reverify.banner.review')}
          </Button>
        }
      >
        <p>
          {t('reverify.banner.body')}
          {pending.raisedAt
            ? ` ${t('reverify.banner.raisedAt', { at: fmt.dateTime(pending.raisedAt) })}`
            : ''}
        </p>
        <p>{t('reverify.banner.keepsOld')}</p>
      </Banner>
      <Dialog
        title={t('reverify.dialog.title')}
        icon={GitCompare}
        tone="warning"
        size="lg"
        isOpen={open}
        onOpenChange={setOpen}
        hideCancel
      >
        <DecisionForm
          claim={claim}
          onDone={() => {
            setOpen(false);
          }}
        />
      </Dialog>
    </>
  );
}
