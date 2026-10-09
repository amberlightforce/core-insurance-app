import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { PolicyGetResponse } from '../../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  Dialog,
  EmptyState,
  KeyValueList,
  SegmentedControl,
  Select,
  TextField,
  announce,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { rememberRecent } from '../../staff/recent';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { TermStatusPill } from '../TermStatusPill';
import {
  bindServicingJob,
  createCancellation,
  getServicingJob,
  quoteServicingJob,
  type CancellationKind,
  type JobBind,
  type ServicingPreview,
} from './api';
import { changeInstant } from './logic';
import { ServicingPreviewView } from './ServicingPreviewView';
import { ServicingProblem } from './ServicingProblem';
import { useServicingPolicy } from './useServicingPolicy';
import { athensToday } from '../../quote/time';

/** Cancellation sources the slice accepts (D-SL3-11: only the policyholder's request). */
const sources = ['Policyholder'] as const;
/** Reason codes of the illustrative reason list (the configured list is not served by an endpoint yet). */
const reasonCodes = [
  'CUSTOMER_REQUEST',
  'VEHICLE_SOLD',
  'VEHICLE_SCRAPPED',
  'INSURER_SWITCH',
  'OTHER',
] as const;

type Busy = null | 'preview' | 'bind';

/** Cancellation (SCR-POL-14 subset): a record page in the claim-file pattern, with the refund breakdown and the confirm. */
export function CancellationPage() {
  const { policyId = '' } = useParams();
  const policy = useServicingPolicy(policyId);
  return (
    <div className={styles.page}>
      <QueryView query={policy}>
        {(data) => <CancellationForm key={policyId} data={data} />}
      </QueryView>
    </div>
  );
}

function CancellationForm({ data }: { data: PolicyGetResponse }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const { policy, term } = data;
  const status = policy.status ?? term?.state;
  const cancellable =
    term !== undefined && (term.state === 'IN_FORCE' || term.state === 'SCHEDULED');
  const scheduled = term?.state === 'SCHEDULED';

  const [source, setSource] = useState<string>(sources[0]);
  const [reason, setReason] = useState<string | null>(null);
  const [kind, setKind] = useState<CancellationKind>(scheduled ? 'FLAT' : 'STANDARD');
  const [requestRef, setRequestRef] = useState('');
  const [job, setJob] = useState<{ jobId: string; versionNo: number } | null>(null);
  const [preview, setPreview] = useState<ServicingPreview | null>(null);
  const [busy, setBusy] = useState<Busy>(null);
  const [error, setError] = useState<{ error: unknown; title: string } | null>(null);
  const [bound, setBound] = useState<JobBind | null>(null);
  const [gateFailure, setGateFailure] = useState<JobBind | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [touched, setTouched] = useState(false);

  const createKey = useIdempotencyKey();
  const quoteKey = useIdempotencyKey();
  const bindKey = useIdempotencyKey();
  /** «Now» is fixed per request so that a retry sends an identical payload (same key). */
  const instant = useRef<string | null>(null);

  const locked = job !== null;
  const reasonMissing = reason === null;

  const previewRefund = async () => {
    setTouched(true);
    if (reasonMissing || !term) return;
    setError(null);
    setBusy('preview');
    try {
      instant.current ??= changeInstant(athensToday());
      const request = {
        policyId: policy.policyId,
        source,
        reasonCode: reason,
        effectiveAt: instant.current,
        kind,
        ...(requestRef.trim() ? { requestRef: requestRef.trim() } : {}),
      };
      const created = await createCancellation(request, createKey.keyFor(request));
      createKey.release();
      // The job is Draft until it is quoted: the quote freezes the version the bind confirms.
      const { job: server } = await getServicingJob(created.jobId);
      const quoteRequest = { jobId: created.jobId, versionNo: server.currentVersionNo };
      const quoted = await quoteServicingJob(quoteRequest, quoteKey.keyFor(quoteRequest));
      quoteKey.release();
      setJob({ jobId: created.jobId, versionNo: quoted.versionNo });
      setPreview(quoted.servicingPreview ?? created.servicingPreview);
      announce(t('servicing.cancel.preview.ready'));
    } catch (cause) {
      setError({ error: cause, title: t('servicing.cancel.preview.failed') });
    } finally {
      setBusy(null);
    }
  };

  const bind = async () => {
    if (!job || !term) return;
    setError(null);
    setBusy('bind');
    try {
      const request = {
        jobId: job.jobId,
        versionNo: job.versionNo,
        paymentPlanOption: term.paymentPlanRef,
        confirmation: true,
      };
      const response = await bindServicingJob(request, bindKey.keyFor(request));
      bindKey.release();
      setConfirmOpen(false);
      setConfirmed(false);
      if (response.gateResults.every((g) => g.passed)) {
        setBound(response);
        setGateFailure(null);
        rememberRecent('policy', policy.policyId);
        announce(t('servicing.cancel.bound.title'));
      } else {
        setGateFailure(response);
      }
    } catch (cause) {
      setConfirmOpen(false);
      setConfirmed(false);
      setError({ error: cause, title: t('servicing.cancel.confirm.failed') });
    } finally {
      setBusy(null);
    }
  };

  const header = (
    <PageHeader
      overline={[t('servicing.cancel.overline'), policy.productCode].join(' · ')}
      title={t('servicing.cancel.title')}
      recordId={policy.policyNumber}
      subtitle={status ? <TermStatusPill state={status} /> : undefined}
      facts={
        term
          ? [
              { id: 'from', label: t('term.from'), value: fmt.date(term.period.from) },
              {
                id: 'to',
                label: t('term.to'),
                value: term.period.to ? fmt.date(term.period.to) : t('term.open'),
              },
            ]
          : []
      }
      actions={
        <LinkButton variant="secondary" to={`/policies/${policy.policyId}`}>
          {t('servicing.back')}
        </LinkButton>
      }
    />
  );

  if (!cancellable) {
    return (
      <>
        {header}
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('servicing.cancel.notCancellable.title')}
          description={t('servicing.cancel.notCancellable.body')}
          action={
            <LinkButton variant="primary" to={`/policies/${policy.policyId}`}>
              {t('servicing.back')}
            </LinkButton>
          }
        />
      </>
    );
  }

  return (
    <>
      {header}
      {error ? <ServicingProblem error={error.error} title={error.title} /> : null}
      {bound ? (
        <>
          <Banner variant="success" live="status" title={t('servicing.cancel.bound.title')}>
            {t('servicing.cancel.bound.body')}
          </Banner>
          {bound.servicingPreview ? (
            <ServicingPreviewView
              preview={bound.servicingPreview}
              artefactHash={term.artefactHash}
            />
          ) : null}
          <div className={styles.actions}>
            <LinkButton variant="primary" to={`/policies/${policy.policyId}`}>
              {t('servicing.openPolicy')}
            </LinkButton>
          </div>
        </>
      ) : (
        <>
          <div className={styles.grid}>
            <Section title={t('servicing.cancel.request.title')}>
              <div className={styles.stack}>
                <Select
                  label={t('servicing.cancel.request.source')}
                  isRequired
                  isDisabled={locked}
                  options={sources.map((s) => ({
                    id: s,
                    label: t(`servicing.cancel.sources.${s}`),
                  }))}
                  value={source}
                  onChange={(value) => {
                    if (value) setSource(value);
                  }}
                  helperText={t('servicing.cancel.request.sourceHelp')}
                />
                <Select
                  label={t('servicing.cancel.request.reason')}
                  isRequired
                  isDisabled={locked}
                  options={reasonCodes.map((c) => ({
                    id: c,
                    label: t(`servicing.cancel.reasons.${c}`),
                  }))}
                  value={reason}
                  onChange={setReason}
                  helperText={t('servicing.cancel.request.reasonHelp')}
                  {...(touched && reasonMissing
                    ? { errorMessage: t('servicing.cancel.request.reasonRequired') }
                    : {})}
                />
                <SegmentedControl
                  label={t('servicing.cancel.request.kind')}
                  isDisabled={locked}
                  value={kind}
                  onChange={(value) => {
                    setKind(value as CancellationKind);
                  }}
                  options={[
                    {
                      id: 'STANDARD',
                      label: t('servicing.cancel.kinds.STANDARD'),
                      ...(scheduled
                        ? { disabledReason: t('servicing.cancel.kinds.standardScheduled') }
                        : {}),
                    },
                    {
                      id: 'FLAT',
                      label: t('servicing.cancel.kinds.FLAT'),
                      ...(!scheduled
                        ? { disabledReason: t('servicing.cancel.kinds.flatInForce') }
                        : {}),
                    },
                  ]}
                />
                <p className={styles.muted}>
                  {t(
                    kind === 'FLAT'
                      ? 'servicing.cancel.request.flatHelp'
                      : 'servicing.cancel.request.nowHelp',
                  )}
                </p>
                <TextField
                  label={t('servicing.cancel.request.requestRef')}
                  helperText={t('servicing.cancel.request.requestRefHelp')}
                  isReadOnly={locked}
                  value={requestRef}
                  onChange={setRequestRef}
                />
                <div className={styles.actions}>
                  <Button
                    variant="primary"
                    isLoading={busy === 'preview'}
                    isDisabled={locked}
                    onPress={() => void previewRefund()}
                  >
                    {t('servicing.cancel.request.preview')}
                  </Button>
                </div>
              </div>
            </Section>
            <Section title={t('servicing.cancel.policy.title')}>
              <KeyValueList
                aria-label={t('servicing.cancel.policy.title')}
                items={[
                  {
                    id: 'number',
                    label: t('servicing.cancel.policy.number'),
                    value: policy.policyNumber,
                    kind: 'mono',
                  },
                  {
                    id: 'plate',
                    label: t('risk.plate'),
                    value: data.riskTree?.vehicles[0]?.plate ?? null,
                    kind: 'mono',
                  },
                  {
                    id: 'product',
                    label: t('term.product'),
                    value: `${policy.productCode} ${term.productVersion}`.trim(),
                    kind: 'mono',
                  },
                  {
                    id: 'plan',
                    label: t('term.plan'),
                    value: t(`servicing.paymentPlan.${term.paymentPlanRef}`, {
                      defaultValue: term.paymentPlanRef,
                    }),
                  },
                ]}
              />
            </Section>
          </div>
          {preview ? (
            <>
              {gateFailure ? (
                <Banner
                  variant="danger"
                  live="alert"
                  title={t('servicing.change.confirm.gateFailedTitle')}
                >
                  <p>{t('servicing.change.confirm.gateFailedBody')}</p>
                  <ul className={styles.problemList}>
                    {gateFailure.gateResults.map((g) => (
                      <li key={g.gate}>
                        <strong>{t(`servicing.gate.${g.gate}`, { defaultValue: g.gate })}</strong>
                        {': '}
                        {g.passed ? t('servicing.gatePassed') : t('servicing.gateFailed')}
                      </li>
                    ))}
                  </ul>
                </Banner>
              ) : null}
              <ServicingPreviewView preview={preview} artefactHash={term.artefactHash} />
              <div className={styles.actions}>
                <Button
                  variant="danger"
                  isLoading={busy === 'bind'}
                  onPress={() => {
                    setConfirmOpen(true);
                  }}
                >
                  {t('servicing.cancel.confirm.commit')}
                </Button>
              </div>
            </>
          ) : (
            <p className={styles.muted}>{t('servicing.cancel.preview.emptyBody')}</p>
          )}
        </>
      )}
      <Dialog
        title={t('servicing.cancel.confirm.dialog.title')}
        tone="danger"
        isDestructive
        isOpen={confirmOpen}
        isBusy={busy === 'bind'}
        onOpenChange={(open) => {
          setConfirmOpen(open);
          if (!open) setConfirmed(false);
        }}
        closeOnAction={false}
        primaryAction={{
          label: t('servicing.cancel.confirm.dialog.confirm'),
          variant: 'danger',
          ...(confirmed
            ? {}
            : { disabledReason: t('servicing.cancel.confirm.dialog.confirmRequired') }),
          onAction: () => {
            if (confirmed) void bind();
          },
        }}
      >
        <p>
          {t('servicing.cancel.confirm.dialog.body', {
            refund: preview ? fmt.money(preview.refundDue) : '',
          })}
        </p>
        <Checkbox isSelected={confirmed} onChange={setConfirmed}>
          {t('servicing.cancel.confirm.dialog.check')}
        </Checkbox>
      </Dialog>
    </>
  );
}
