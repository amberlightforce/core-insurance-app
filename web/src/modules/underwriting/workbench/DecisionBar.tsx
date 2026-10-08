import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { readSession } from '../../../dev-auth/devAuth';
import {
  Banner,
  Button,
  TextField,
  announce,
  toast,
  useRegionFormat,
} from '../../../design-system';
import { formatDateTime } from '../../../format';
import { displayName } from '../../../app-shell/user';
import { problemOf } from '../../staff/problem';
import {
  decideIssues,
  useRefreshReferrals,
  type IssueDecideRequest,
  type IssueDecision,
  type ReferralView,
} from '../api';
import { mostRestrictive, openIssuesOf, useIssueTypeLabel } from './helpers';
import styles from './Workbench.module.css';

export interface DecisionBarProps {
  referral: ReferralView;
}

function Signature() {
  return (
    <svg className={styles.sig} viewBox="0 0 150 18" aria-hidden="true">
      <path d="M2 12 C 20 2, 30 16, 46 9 S 70 4, 84 11 S 112 15, 148 6" />
    </svg>
  );
}

/**
 * The decision bar (mockup `.decision`): the authority line with its meter, «Απόρριψη…» and «Έγκριση…» which open an
 * inline reason field (required; Ctrl+Enter submits only with a reason), and the receipt after a decision.
 *
 * The «can I decide» preview only enables or disables the buttons: the server re-checks authority and separation
 * of duties on every call and its refusal is explained here (PITFALLS 1-7). Nothing about roles or authority is sent.
 */
export function DecisionBar({ referral }: DecisionBarProps) {
  const { t, i18n } = useTranslation('underwriting');
  const region = useRegionFormat();
  const typeLabel = useIssueTypeLabel();
  const refresh = useRefreshReferrals();
  const { keyFor, release } = useIdempotencyKey();
  const [mode, setMode] = useState<IssueDecision | null>(null);
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);

  const summary = referral.summary;
  const open = openIssuesOf(referral);
  const restrictive = mostRestrictive(open);
  const canDecide = referral.decidability.canDecide && open.length > 0;
  const jobLabel = summary.jobNumber ?? summary.jobRef;
  const customer = summary.customer?.displayName ?? summary.customer?.partyNumber ?? '';

  const mutation = useMutation({
    mutationFn: ({ body, key }: { body: IssueDecideRequest; key: string }) =>
      decideIssues(body, key),
    onSuccess: (_, { body }) => {
      release();
      setMode(null);
      setReason('');
      setTried(false);
      const approved = body.decision === 'APPROVE';
      announce(t(approved ? 'decision.announceApproved' : 'decision.announceRejected', { job: jobLabel }));
      // A confirmation without undo: a decision is a recorded fact, not a draft (D-SL5-03).
      toast.success({
        title: t(approved ? 'decision.approvedToast' : 'decision.rejectedToast'),
        description: t('decision.toastBody', { customer, job: jobLabel }),
      });
      void refresh();
    },
  });

  const submit = (decision: IssueDecision) => {
    setTried(true);
    const text = reason.trim();
    if (text === '') return;
    const body: IssueDecideRequest = {
      issueIds: open.map((entry) => entry.issue.id),
      decision,
      reason: text,
      expectedRecordVersions: Object.fromEntries(
        open.map((entry) => [entry.issue.id, entry.issue.recordVersion]),
      ),
    };
    mutation.mutate({ body, key: keyFor(body) });
  };

  const begin = (decision: IssueDecision) => {
    mutation.reset();
    setTried(false);
    setMode(decision);
  };

  const cancel = () => {
    setMode(null);
    setReason('');
    setTried(false);
    mutation.reset();
  };

  // The server's reasons in plain words: the tooltip and the accessible description of the disabled buttons.
  const disabledReason = referral.decidability.reasons
    .map((code) => t(`decision.reasons.${code}`))
    .join(' ') || t('decision.cannotDecide');

  const problem = mutation.isError ? problemOf(mutation.error) : null;
  const known =
    problem?.code && i18n.exists(`underwriting:errors.codes.${problem.code}`)
      ? t(`errors.codes.${problem.code}`)
      : null;
  const explained =
    known ??
    (problem?.status === 403 ? t('errors.forbidden') : (problem?.detail ?? t('errors.generic')));
  const stale = problem?.code === 'UW-ERR-STALE';

  const decidedBy = summary.lastDecidedBy
    ? summary.lastDecidedBy
    : displayName(readSession()?.user.name ?? '');
  const receipt =
    summary.lastDecidedAt && open.length === 0 ? (
      <span className={styles.receipt}>
        {t('decision.receipt', {
          name: decidedBy,
          time: formatDateTime(summary.lastDecidedAt, region),
        })}
        <Signature />
      </span>
    ) : null;

  const reasonText = (decision: IssueDecision) =>
    t(decision === 'APPROVE' ? 'decision.reasonApprove' : 'decision.reasonReject');

  return (
    <section className={styles.decision} aria-label={t('decision.label')}>
      <div className={styles.auth}>
        {restrictive ? (
          <>
            <span>
              {t('decision.authority', { issue: typeLabel(restrictive.issue.issueType) })}{' '}
              <b>{t(`decision.outcome.${restrictive.decidability.authority.outcome}`)}</b>
            </span>
            <div
              className={styles.meter}
              role="meter"
              aria-label={t(`decision.authorityMeter.${restrictive.decidability.authority.outcome}`)}
              aria-valuemin={0}
              aria-valuemax={1}
              aria-valuenow={restrictive.decidability.authority.outcome === 'ALLOW' ? 1 : 0}
              aria-valuetext={t(
                `decision.authorityMeter.${restrictive.decidability.authority.outcome}`,
              )}
            >
              <div
                className={styles.meterFill}
                data-outcome={restrictive.decidability.authority.outcome}
              />
            </div>
          </>
        ) : (
          (receipt ?? <span>{t('decision.readOnly')}</span>)
        )}
      </div>

      {open.length > 0 ? (
        <div className={styles.actions}>
          <Button
            variant="secondary"
            isLoading={mutation.isPending}
            {...(!canDecide ? { disabledReason } : {})}
            onPress={() => {
              begin('REJECT');
            }}
          >
            {t('decision.reject')}
          </Button>
          <Button
            variant="primary"
            isLoading={mutation.isPending}
            {...(!canDecide ? { disabledReason } : {})}
            onPress={() => {
              begin('APPROVE');
            }}
          >
            {t('decision.approve')}
          </Button>
        </div>
      ) : null}

      {!canDecide && open.length > 0 ? (
        <div className={styles.blocked}>
          <Banner variant="warning" live="none" title={t('decision.cannotDecide')}>
            <ul>
              {referral.decidability.reasons.map((code) => (
                <li key={code}>{t(`decision.reasons.${code}`)}</li>
              ))}
            </ul>
          </Banner>
        </div>
      ) : null}

      {mode !== null && canDecide ? (
        <form
          className={styles.reasonBox}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            submit(mode);
          }}
        >
          <TextField
            label={reasonText(mode)}
            multiline
            minRows={2}
            isRequired
            autoFocus
            value={reason}
            onChange={setReason}
            onSubmit={() => {
              submit(mode);
            }}
            helperText={t('decision.reasonHelp')}
            errorMessage={tried && reason.trim() === '' ? t('decision.reasonRequired') : undefined}
          />
          <p className={styles.caption}>{t('decision.scope', { count: open.length })}</p>
          <div className={styles.reasonActions}>
            <Button
              type="submit"
              variant="primary"
              size="lg"
              shortcut="Mod+Enter"
              isLoading={mutation.isPending}
            >
              {t(mode === 'APPROVE' ? 'decision.confirmApprove' : 'decision.confirmReject')}
            </Button>
            <Button variant="ghost" size="lg" isDisabled={mutation.isPending} onPress={cancel}>
              {t('decision.cancel')}
            </Button>
          </div>
        </form>
      ) : null}

      {problem ? (
        <div className={styles.blocked}>
          <Banner
            variant="danger"
            live="alert"
            title={t('errors.title')}
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
            {problem.code || problem.traceId ? (
              <details className={styles.technical}>
                <summary>{t('errors.technical')}</summary>
                {problem.code ? <p className={styles.mono}>{problem.code}</p> : null}
                {problem.traceId ? <p>{t('errors.trace', { id: problem.traceId })}</p> : null}
              </details>
            ) : null}
          </Banner>
        </div>
      ) : null}
    </section>
  );
}
