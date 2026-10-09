import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, TextField, announce, toast } from '../../design-system';
import { problemOf } from '../staff/problem';
import styles from './Reinsurance.module.css';
import {
  approveContract,
  submitContract,
  useContractCommand,
  useRefreshContracts,
  type ContractApproveRequest,
  type ContractSubmitRequest,
  type ContractView,
} from './api';
import { accountantRole, currentUser, hasRole, managerRole } from './roles';

type Mode = 'approve' | 'return' | 'submit' | null;

/**
 * The treaty's decision bar (v3 approvals pattern). Draft: the enterer submits for approval after a confirmation.
 * Pending approval: a Reinsurance manager approves, or returns with a reason; the enterer sees no decision buttons
 * and is told why (maker ≠ checker, REQ-RI-065). The preview only hides buttons: the server re-checks the
 * separation of duties on every call and its refusal (RI-ERR-SOD) is explained here (PITFALLS 1-7, 27).
 */
export function DecisionBar({ contract }: { contract: ContractView }) {
  const { t, i18n } = useTranslation('reinsurance');
  const refresh = useRefreshContracts();
  const { keyFor, release } = useIdempotencyKey();
  const [mode, setMode] = useState<Mode>(null);
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const user = currentUser();

  const done = () => {
    release();
    setMode(null);
    setReason('');
    setTried(false);
  };
  const submit = useContractCommand<ContractSubmitRequest>(submitContract);
  const decide = useContractCommand<ContractApproveRequest>(approveContract);
  const mutation = mode === 'submit' ? submit : decide;
  const pending = submit.isPending || decide.isPending;

  const isEnterer = contract.maker !== undefined && contract.maker === user.id;
  const isManager = hasRole(user.roles, managerRole);
  const isAccountant = hasRole(user.roles, accountantRole) || hasRole(user.roles, 'Platform.Admin');

  const begin = (next: Exclude<Mode, null>) => {
    submit.reset();
    decide.reset();
    setTried(false);
    setMode(next);
  };
  const cancel = () => {
    submit.reset();
    decide.reset();
    setMode(null);
    setReason('');
    setTried(false);
  };

  const label = contract.contractNumber ?? t('contract.draftTitle');

  const send = (decision: 'APPROVE' | 'RETURN') => {
    setTried(true);
    const text = reason.trim();
    if (decision === 'RETURN' && text === '') return;
    const body: ContractApproveRequest = {
      contractId: contract.contractId,
      expectedRecordVersion: contract.recordVersion,
      decision,
      ...(text ? { reason: text } : {}),
    };
    decide.mutate(
      { body, key: keyFor(body) },
      {
        onSuccess: () => {
          done();
          announce(
            t(decision === 'APPROVE' ? 'decision.announceApproved' : 'decision.announceReturned', {
              contract: label,
            }),
          );
          toast.success({
            title: t(decision === 'APPROVE' ? 'decision.approvedToast' : 'decision.returnedToast'),
            description: label,
          });
        },
      },
    );
  };

  const sendSubmit = () => {
    const body: ContractSubmitRequest = {
      contractId: contract.contractId,
      expectedRecordVersion: contract.recordVersion,
    };
    submit.mutate(
      { body, key: keyFor(body) },
      {
        onSuccess: () => {
          done();
          announce(t('decision.announceSubmitted', { contract: label }));
          toast.success({ title: t('decision.submittedToast'), description: label });
        },
      },
    );
  };

  const problem = mutation.isError ? problemOf(mutation.error) : null;
  const code = problem?.code;
  const known =
    code && i18n.exists(`reinsurance:errors.codes.${code}`) ? t(`errors.codes.${code}`) : null;
  const explained =
    known ??
    (problem?.status === 403 ? t('errors.forbidden') : (problem?.detail ?? t('errors.generic')));
  const stale = code === 'RI-ERR-STALE';

  const error = problem ? (
    <Banner
      variant="danger"
      live="alert"
      title={code === 'RI-ERR-SOD' ? t('errors.sodTitle') : t('errors.title')}
      actions={
        stale ? (
          <Button
            variant="secondary"
            size="sm"
            onPress={() => {
              cancel();
              void refresh();
            }}
          >
            {t('errors.reload')}
          </Button>
        ) : undefined
      }
    >
      <p>{explained}</p>
      {code || problem.traceId ? (
        <p className="ds-caption">
          {code ? <span className="ds-mono">{code}</span> : null}
          {code && problem.traceId ? ' · ' : null}
          {problem.traceId ? t('errors.trace', { id: problem.traceId }) : null}
        </p>
      ) : null}
    </Banner>
  ) : null;

  if (contract.status === 'DRAFT') {
    if (!isAccountant) return <p className="ds-caption">{t('decision.draftReadOnly')}</p>;
    return (
      <div className={styles.decision}>
        <p className="ds-caption">{t('decision.draftHelp')}</p>
        {mode === 'submit' ? (
          <div className={styles.decision}>
            <Banner variant="info" live="none" title={t('decision.confirmSubmitTitle')}>
              {t('decision.confirmSubmitBody')}
            </Banner>
            <div className={styles.reasonActions}>
              <Button variant="primary" isLoading={pending} onPress={sendSubmit}>
                {t('decision.confirmSubmit')}
              </Button>
              <Button variant="ghost" isDisabled={pending} onPress={cancel}>
                {t('decision.cancel')}
              </Button>
            </div>
          </div>
        ) : (
          <div className={styles.reasonActions}>
            <Button
              variant="commit"
              onPress={() => {
                begin('submit');
              }}
            >
              {t('decision.submit')}
            </Button>
          </div>
        )}
        {error}
      </div>
    );
  }

  if (contract.status !== 'PENDING_APPROVAL') {
    return <p className="ds-caption">{t(`decision.final.${contract.status}`)}</p>;
  }

  if (isEnterer) {
    return (
      <div className={styles.sodNote}>
        <Banner variant="info" live="none" title={t('decision.sodTitle')}>
          {t('decision.sodBody')}
        </Banner>
      </div>
    );
  }

  if (!isManager) {
    return <p className="ds-caption">{t('decision.managerOnly')}</p>;
  }

  return (
    <div className={styles.decision}>
      <p className="ds-caption">{t('decision.pendingHelp', { maker: contract.maker ?? '—' })}</p>
      {mode === 'approve' || mode === 'return' ? (
        <form
          className={styles.decision}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            send(mode === 'approve' ? 'APPROVE' : 'RETURN');
          }}
        >
          <TextField
            label={t(mode === 'approve' ? 'decision.reasonApprove' : 'decision.reasonReturn')}
            multiline
            minRows={2}
            isRequired={mode === 'return'}
            autoFocus
            value={reason}
            onChange={setReason}
            helperText={t(
              mode === 'approve' ? 'decision.reasonApproveHelp' : 'decision.reasonReturnHelp',
            )}
            errorMessage={
              tried && mode === 'return' && reason.trim() === ''
                ? t('decision.reasonRequired')
                : undefined
            }
          />
          <div className={styles.reasonActions}>
            <Button
              type="submit"
              variant={mode === 'approve' ? 'commit' : 'primary'}
              isLoading={pending}
            >
              {t(mode === 'approve' ? 'decision.confirmApprove' : 'decision.confirmReturn')}
            </Button>
            <Button variant="ghost" isDisabled={pending} onPress={cancel}>
              {t('decision.cancel')}
            </Button>
          </div>
        </form>
      ) : (
        <div className={styles.reasonActions}>
          <Button
            variant="secondary"
            onPress={() => {
              begin('return');
            }}
          >
            {t('decision.return')}
          </Button>
          <Button
            variant="commit"
            onPress={() => {
              begin('approve');
            }}
          >
            {t('decision.approve')}
          </Button>
        </div>
      )}
      {error}
    </div>
  );
}
