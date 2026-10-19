import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Banner, Button, Select, TextField, announce, toast } from '../../../design-system';
import { useDsarCaller } from './access';
import { identityMethods, useDsarCommand, type DsarCase, type DsarCommand } from './api';
import styles from './Dsar.module.css';
import { downloadPackage } from './download';
import { ProblemBanner } from './ProblemBanner';

type Pending = 'erase' | 'restrict' | 'refuse' | 'extend' | 'approve' | null;

/**
 * The decision bar of a case. The buttons only reflect state; the server enforces identity, four eyes (the approver
 * is not the case creator), the legal hold and the NOT_BUILT blockers, and its refusal is shown as is.
 */
export function CaseActions({ data }: { data: DsarCase }) {
  const { t } = useTranslation('compliance');
  const { keyFor, release } = useIdempotencyKey();
  const run = useDsarCommand();
  const caller = useDsarCaller();
  const [pending, setPending] = useState<Pending>(null);
  const [method, setMethod] = useState<string>(identityMethods[0]);
  const [evidence, setEvidence] = useState('');
  const [reason, setReason] = useState('');
  const { request } = data;
  const id = request.dsarId;
  const busy = run.isPending;
  const open = request.status !== 'COMPLETED' && request.status !== 'REFUSED';
  const erasure = request.requestType === 'ERASURE';
  const restriction = request.requestType === 'RESTRICTION';
  const approvalAction = erasure ? 'ERASE' : 'RESTRICT';
  const approved = request.approvalAction === approvalAction;
  const reasonMissing = reason.trim() === '';

  const send = (command: DsarCommand, done: string, after?: (response: DsarCase) => void) => {
    run.mutate(
      { command, key: keyFor(command) },
      {
        onSuccess: (response) => {
          release();
          setPending(null);
          setReason('');
          announce(t(done));
          toast.success({ title: t(done), description: id });
          after?.(response);
        },
      },
    );
  };

  const confirm = (text: string, label: string, onConfirm: () => void, needsReason = true) => (
    <form
      className={styles.form}
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        if (needsReason && reasonMissing) return;
        onConfirm();
      }}
    >
      <Banner variant="info" live="none" title={t('dsar.confirm.title')}>
        {text}
      </Banner>
      {needsReason ? (
        <TextField
          label={t('dsar.actions.reason')}
          multiline
          minRows={2}
          autoFocus
          value={reason}
          onChange={setReason}
          isRequired
        />
      ) : null}
      <div className={styles.actions}>
        <Button
          type="submit"
          variant="commit"
          isLoading={busy}
          isDisabled={needsReason && reasonMissing}
        >
          {label}
        </Button>
        <Button
          variant="ghost"
          isDisabled={busy}
          onPress={() => {
            setPending(null);
          }}
        >
          {t('dsar.actions.cancel')}
        </Button>
      </div>
    </form>
  );

  let body;
  if (!open) {
    body = <p className="ds-caption">{t(`dsar.help.${request.status}`)}</p>;
  } else if (pending === 'refuse') {
    body = confirm(t('dsar.confirm.refuse'), t('dsar.actions.confirmRefuse'), () => {
      send({ name: 'refuse', body: { id, reason: reason.trim() } }, 'dsar.done.refuse');
    });
  } else if (pending === 'extend') {
    body = confirm(t('dsar.confirm.extend'), t('dsar.actions.confirmExtend'), () => {
      send({ name: 'extend', body: { id, reason: reason.trim() } }, 'dsar.done.extend');
    });
  } else if (pending === 'approve') {
    body = confirm(
      t(`dsar.confirm.approve.${approvalAction}`),
      t('dsar.actions.confirmApprove'),
      () => {
        send(
          { name: 'approve', body: { id, action: approvalAction, reason: reason.trim() } },
          'dsar.done.approve',
        );
      },
    );
  } else if (pending === 'erase' || pending === 'restrict') {
    body = confirm(
      t(`dsar.confirm.${pending}`),
      t(`dsar.actions.confirm${pending === 'erase' ? 'Erase' : 'Restrict'}`),
      () => {
        send({ name: pending, body: { dsarId: id } }, `dsar.done.${pending}`);
      },
      false,
    );
  } else {
    const stage = (
      <div className={styles.stack}>
        {request.status === 'RECEIVED' ? (
          <form
            className={styles.inline}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              send(
                {
                  name: 'verify-identity',
                  body: {
                    id,
                    method,
                    ...(evidence.trim() ? { evidenceRef: evidence.trim() } : {}),
                  },
                },
                'dsar.done.verify',
              );
            }}
          >
            <Select
              label={t('dsar.identity.method')}
              options={identityMethods.map((m) => ({
                id: m,
                label: t(`dsar.identity.methods.${m}`),
              }))}
              value={method}
              onChange={(value) => {
                if (value) setMethod(value);
              }}
            />
            <TextField
              label={t('dsar.identity.evidence')}
              value={evidence}
              onChange={setEvidence}
            />
            <Button type="submit" variant="primary" isLoading={busy}>
              {t('dsar.identity.verify')}
            </Button>
          </form>
        ) : null}
        {request.status === 'IDENTITY_VERIFIED' ? (
          <div className={styles.actions}>
            <Button
              variant="primary"
              isLoading={busy}
              onPress={() => {
                send({ name: 'start-fan-out', body: { id } }, 'dsar.done.fanOut');
              }}
            >
              {t('dsar.actions.startFanOut')}
            </Button>
          </div>
        ) : null}
        {request.status === 'IN_REVIEW' && (erasure || restriction) ? (
          <>
            {caller.actorKey && caller.actorKey === request.createdBy ? (
              <Banner variant="warning" live="none" title={t('dsar.fourEyes.title')}>
                {t('dsar.fourEyes.body')}
              </Banner>
            ) : null}
            <div className={styles.actions}>
              <Button
                variant="secondary"
                isDisabled={busy || approved}
                onPress={() => {
                  setPending('approve');
                }}
              >
                {t(approved ? 'dsar.actions.approved' : `dsar.actions.approve.${approvalAction}`, {
                  who: request.approvedBy ?? '',
                })}
              </Button>
              <Button
                variant="commit"
                isDisabled={busy || !approved}
                onPress={() => {
                  setPending(erasure ? 'erase' : 'restrict');
                }}
              >
                {t(erasure ? 'dsar.actions.erase' : 'dsar.actions.restrict')}
              </Button>
            </div>
          </>
        ) : null}
        {request.status === 'IN_REVIEW' && !erasure && !restriction ? (
          <div className={styles.actions}>
            <Button
              variant="commit"
              isLoading={busy}
              onPress={() => {
                send({ name: 'export', body: { dsarId: id } }, 'dsar.done.export', (response) => {
                  downloadPackage(id, response.package ?? data.package);
                });
              }}
            >
              {t('dsar.actions.export')}
            </Button>
          </div>
        ) : null}
        <div className={styles.actions}>
          <Button
            variant="ghost"
            isDisabled={busy || request.extended}
            onPress={() => {
              setPending('extend');
            }}
          >
            {t('dsar.actions.extend')}
          </Button>
          <Button
            variant="ghost"
            isDisabled={busy}
            onPress={() => {
              setPending('refuse');
            }}
          >
            {t('dsar.actions.refuse')}
          </Button>
        </div>
      </div>
    );
    body = stage;
  }

  return (
    <div className={styles.stack}>
      {body}
      {run.isError ? <ProblemBanner error={run.error} /> : null}
    </div>
  );
}
