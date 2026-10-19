import { useTranslation } from 'react-i18next';

import { isApiError } from '../../../api/client';
import { useIdempotencyKey } from '../../../api/idempotency';
import type { ClaimView } from '../../../api/types';
import { Banner, Button, KeyValueList, StatusPill, announce } from '../../../design-system';
import { Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  isFsDisabled,
  useEvaluateFs,
  useFsCases,
  useOpenOwnSettlement,
  useSubmitFs,
  type FsCaseView,
  type FsEligibility,
} from './api';

const stateSemantic: Record<string, 'info' | 'success' | 'warning' | 'disabled'> = {
  ELIGIBILITY_PENDING: 'info',
  ELIGIBLE: 'success',
  NOT_ELIGIBLE: 'warning',
  SUBMITTED: 'info',
  ACCEPTED: 'success',
  DISPUTED: 'warning',
  REJECTED: 'warning',
  SETTLED: 'success',
  RECONCILED: 'disabled',
};

function EligibilityBlock({ eligibility }: { eligibility: FsEligibility }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const ok = eligibility.result === 'ELIGIBLE';
  return (
    <div className={styles.stack}>
      <p>
        <StatusPill
          semantic={ok ? 'success' : 'warning'}
          text={
            eligibility.result === 'NOT_APPLICABLE'
              ? t('fs.notApplicable')
              : ok
                ? t('fs.eligible')
                : t('fs.notEligible')
          }
        />
      </p>
      {eligibility.reasons.length > 0 ? (
        <ul aria-label={t('fs.reasons')}>
          {eligibility.reasons.map((r) => (
            <li key={r}>{t(`fs.reason.${r}`, { defaultValue: r })}</li>
          ))}
        </ul>
      ) : null}
      {eligibility.clearingValue ? (
        <p className="ds-caption">
          {t('fs.clearingBasis', {
            value: fmt.money(eligibility.clearingValue),
            basis: t(`fs.basis.${eligibility.clearingValueBasis ?? 'ACTUAL'}`),
          })}
        </p>
      ) : null}
      {eligibility.provisional ? (
        <p className="ds-caption">
          {t('fs.provisional', { status: eligibility.legalStatus, rule: eligibility.ruleId })}
        </p>
      ) : null}
    </div>
  );
}

function CaseCard({ claimId, fsCase }: { claimId: string; fsCase: FsCaseView }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const open = useOpenOwnSettlement(claimId);
  const submit = useSubmitFs(claimId);
  const openKey = useIdempotencyKey();
  const submitKey = useIdempotencyKey();
  const own = fsCase.role === 'OWN_INSURER';

  const canOpen = own && fsCase.state === 'ELIGIBLE' && !fsCase.ownSettlementExposureId;
  const canSubmit = own && fsCase.state === 'ELIGIBLE' && !!fsCase.ownSettlementExposureId;
  const body = { fsCaseId: fsCase.fsCaseId, version: fsCase.recordVersion };

  const items = [
    { id: 'cp', label: t('fs.counterparty'), value: fsCase.counterpartyCode },
    {
      id: 'dir',
      label: own ? t('fs.recoverable') : t('fs.payable'),
      value: fsCase.clearingValue ? fmt.money(fsCase.clearingValue) : '—',
    },
    ...(own && fsCase.expectedGainLoss
      ? [{ id: 'gl', label: t('fs.expectedGainLoss'), value: fmt.money(fsCase.expectedGainLoss) }]
      : []),
    {
      id: 'ref',
      label: own ? t('fs.clearingRef') : t('fs.notificationRef'),
      value: fsCase.clearingReference ?? t('fs.notSubmitted'),
    },
    ...(fsCase.fsStatementId
      ? [
          {
            id: 'stmt',
            label: t('fs.statementLine'),
            value: fsCase.statementLineRef ?? t('fs.inStatement'),
          },
        ]
      : []),
  ];

  return (
    <div className={styles.stack} data-testid={`fs-case-${fsCase.fsCaseId}`}>
      <p>
        <StatusPill semantic="info" text={t(`fs.role.${fsCase.role}`)} />{' '}
        <StatusPill
          semantic={stateSemantic[fsCase.state] ?? 'info'}
          text={t(`fs.state.${fsCase.state}`)}
        />
      </p>
      {fsCase.eligibility ? <EligibilityBlock eligibility={fsCase.eligibility} /> : null}
      <KeyValueList aria-label={t('fs.title')} items={items} />
      {!own ? <p className="ds-caption">{t('fs.atFaultHint')}</p> : null}
      {canOpen ? (
        <div>
          <Button
            isLoading={open.isPending}
            onPress={() => {
              open.mutate(
                { ...body, key: openKey.keyFor(body) },
                {
                  onSuccess: () => {
                    openKey.release();
                    announce(t('fs.opened'));
                  },
                },
              );
            }}
          >
            {t('fs.openOwn')}
          </Button>
        </div>
      ) : null}
      {canSubmit ? (
        <div>
          <Button
            isLoading={submit.isPending}
            onPress={() => {
              submit.mutate(
                { ...body, key: submitKey.keyFor(body) },
                {
                  onSuccess: () => {
                    submitKey.release();
                    announce(t('fs.submitted'));
                  },
                },
              );
            }}
          >
            {t('fs.submit')}
          </Button>
        </div>
      ) : null}
      {open.isError ? <ProblemBanner error={open.error} /> : null}
      {submit.isError ? <ProblemBanner error={submit.error} /> : null}
    </div>
  );
}

/**
 * «Φιλικός διακανονισμός» card of the claim file (REQ-CLM-155..159): eligibility, the own-insurer case (opening,
 * recoverable, submission status) and the at-fault leg (notification, payable). Hidden when the capability is off
 * or the signed-in role may not read FS cases.
 * TODO(hardening): the statements screen (SL4-CLM-FS-STATEMENT) and a notification intake form are not built here.
 */
export function FsCasePanel({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const claimId = claim.summary.claimId;
  const query = useFsCases(claimId);
  const evaluate = useEvaluateFs(claimId);
  const { keyFor, release } = useIdempotencyKey();

  if (claim.summary.lineOfBusiness !== 'MOTOR' || claim.jurisdiction !== 'GR') return null;
  if (query.isPending) return null;
  if (query.isError) {
    if (isFsDisabled(query.error) || (isApiError(query.error) && query.error.status === 403)) {
      return null;
    }
    return (
      <Section title={t('fs.title')}>
        <p className={styles.muted}>{t('fs.loadFailed')}</p>
      </Section>
    );
  }
  const cases = query.data.items;
  const hasOwn = cases.some((c) => c.role === 'OWN_INSURER');
  const open = claim.summary.status === 'OPEN';

  return (
    <Section title={t('fs.title')} count={cases.length}>
      <div className={styles.stack}>
        {cases.length === 0 ? <p className={styles.muted}>{t('fs.empty')}</p> : null}
        {cases.map((c) => (
          <CaseCard key={c.fsCaseId} claimId={claimId} fsCase={c} />
        ))}
        {!hasOwn && open ? (
          <div>
            <Button
              variant="secondary"
              isLoading={evaluate.isPending}
              onPress={() => {
                evaluate.mutate(
                  { key: keyFor({ claimId }) },
                  {
                    onSuccess: () => {
                      release();
                      announce(t('fs.evaluated'));
                    },
                  },
                );
              }}
            >
              {t('fs.evaluate')}
            </Button>
          </div>
        ) : null}
        {evaluate.isError ? (
          isFsDisabled(evaluate.error) ? (
            <Banner variant="info" live="none" title={t('fs.disabled')} />
          ) : (
            <ProblemBanner error={evaluate.error} />
          )
        ) : null}
      </div>
    </Section>
  );
}
