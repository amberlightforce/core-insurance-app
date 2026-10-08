import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import type { JobBindResponse, JobQuoteResponse } from '../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  Dialog,
  EmptyState,
  KeyValueList,
  Radio,
  RadioGroup,
  Wizard,
  announce,
  type StepItem,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader } from '../staff/PageHeader';
import { rememberRecent } from '../staff/recent';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import {
  bindJob,
  createSubmission,
  getJob,
  quoteJob,
  updateDraft,
  useCatalogue,
  useProductVersion,
  useQuestionEvaluation,
  useQuestionSet,
} from './api';
import { BindReferrals } from './BindReferrals';
import { focusFieldByLabel, InputProblemBanner } from './InputProblemBanner';
import type { InputField } from './inputProblems';
import { QuoteResult, QuoteWarnings } from './QuoteResult';
import {
  answersInstruction,
  coverageInstruction,
  coversNeedingVehicleValue,
  driverInstruction,
  emptyDraft,
  fingerprint,
  invalidAnswers,
  knockOuts,
  missingCoverTerms,
  questionState,
  ratingWarnings,
  slice,
  stepIds,
  unansweredRequired,
  validateDriver,
  validateVehicle,
  vehicleInstruction,
  vehicleValueIssue,
  type Draft,
  type JobRef,
  type StepId,
} from './state';
import { CoversStep, DriverStep, PolicyholderStep, QuestionsStep, VehicleStep } from './steps';
import { addDays, athensToday, effectiveInstant } from './time';
import { useLocalised } from './useLocalised';
import { useRefreshIssues } from './uwIssues';

type Busy = null | 'submission' | 'quote' | 'bind';

interface QuoteState {
  response: JobQuoteResponse;
  /** Fingerprint of the inputs the price was calculated for. */
  fingerprint: string;
}

/**
 * Quote wizard (W4-CHN-01 slice, staff channel): policyholder and product, vehicle, driver, covers, the PFC
 * question set, the premium with underwriting outcome, and the explicit bind. The server draft is written when
 * the premium is calculated (submission when leaving the first step), every command with its own Idempotency-Key
 * that is reused on retry.
 */
export function QuoteWizardPage() {
  const { t } = useTranslation('quote');
  const localised = useLocalised();
  const fmt = useFormat();
  const [search] = useSearchParams();
  const initialPartyId = search.get('partyId');

  const [draft, setDraftState] = useState<Draft>(() => emptyDraft(addDays(athensToday(), 1)));
  const setDraft = useCallback((update: (d: Draft) => Draft) => {
    setDraftState(update);
  }, []);
  const [stepId, setStepId] = useState<StepId>('policyholder');
  const [visited, setVisited] = useState<ReadonlySet<StepId>>(new Set(['policyholder']));
  const [job, setJob] = useState<JobRef | null>(null);
  const [quote, setQuote] = useState<QuoteState | null>(null);
  const [syncedAt, setSyncedAt] = useState<{ fingerprint: string; at: Date } | null>(null);
  const [busy, setBusy] = useState<Busy>(null);
  const [error, setError] = useState<{ error: unknown; title: string } | null>(null);
  const [bound, setBound] = useState<JobBindResponse | null>(null);
  const [gateFailure, setGateFailure] = useState<JobBindResponse | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmed, setConfirmed] = useState(false);

  const submissionKey = useIdempotencyKey();
  const vehicleKey = useIdempotencyKey();
  const riskKey = useIdempotencyKey();
  const quoteKey = useIdempotencyKey();
  const bindKey = useIdempotencyKey();
  const refreshIssues = useRefreshIssues();
  /** The effectiveAt instant is fixed per submission so that a retry sends an identical payload (same key). */
  const instant = useRef<{ for: string; value: string } | null>(null);

  const product = useProductVersion(draft.startDate);
  const hash = product.data?.artefactHash;
  const catalogue = useCatalogue(hash);
  const questionSet = useQuestionSet(hash);
  const questions = useMemo(
    () => questionSet.data?.questionSet.questions ?? [],
    [questionSet.data],
  );
  const coverages = useMemo(() => catalogue.data?.coverages ?? [], [catalogue.data]);
  const visibleAnswers = useMemo(() => {
    const out: Record<string, string> = {};
    for (const q of questions) {
      const value = draft.answers[q.code];
      if (questionState(q, draft.answers).visible && value) out[q.code] = value;
    }
    return out;
  }, [questions, draft.answers]);
  const evaluation = useQuestionEvaluation(
    hash,
    visibleAnswers,
    stepId === 'questions' && questions.length > 0,
  );

  const productIsIllustrative =
    questions.some((q) => q.illustrative === true) ||
    coverages.some((c) =>
      c.terms.some((term) => term.options?.some((o) => o.illustrative === true)),
    );
  const coverNames = new Map(coverages.map((c) => [c.code, localised(c.name)] as const));

  const currentFingerprint = fingerprint(draft);
  const quoteIsFresh = quote !== null && quote.fingerprint === currentFingerprint;
  const thisYear = new Date().getFullYear();

  const valueNeeded = coversNeedingVehicleValue(coverages, draft.covers).length > 0;
  const valueMissing = vehicleValueIssue(draft.vehicle.value, valueNeeded) !== undefined;
  const [pendingFocus, setPendingFocus] = useState<{ step: StepId; label: string } | null>(null);
  /** Labels of the PFC questions that feed the rating (vehicle use, claims), for the input-problem banner. */
  const questionLabels = useMemo(() => {
    const out: Partial<Record<InputField, string>> = {};
    const byField: [InputField, string][] = [
      ['usage', 'vehicle.usage'],
      ['claims', 'driver.claimsLast5Years'],
    ];
    for (const [field, mapsTo] of byField) {
      const q = questions.find((x) => x.mapsToField === mapsTo);
      if (q) out[field] = localised(q.text);
    }
    return out;
  }, [questions, localised]);

  const valid: Record<StepId, boolean> = {
    policyholder:
      draft.policyholder !== null && draft.startDate >= athensToday() && product.isSuccess,
    vehicle: Object.keys(validateVehicle(draft.vehicle, thisYear)).length === 0 && !valueMissing,
    driver: Object.keys(validateDriver(draft.driver, thisYear)).length === 0,
    covers:
      catalogue.isSuccess &&
      missingCoverTerms(coverages, draft.covers).length === 0 &&
      !valueMissing,
    questions:
      questionSet.isSuccess &&
      unansweredRequired(questions, draft.answers).length === 0 &&
      Object.keys(invalidAnswers(questions, draft.answers)).length === 0 &&
      knockOuts(questions, draft.answers).length === 0,
    premium: quoteIsFresh,
    bind: bound !== null,
  };

  const steps: StepItem[] = stepIds.map((id) => ({
    id,
    label: t(`steps.${id}`),
    state:
      busy === 'submission' && id === 'policyholder'
        ? 'loading'
        : valid[id] && (visited.has(id) || id === 'premium')
          ? 'complete'
          : 'upcoming',
  }));

  const nextReason = valid[stepId]
    ? undefined
    : t(
        stepId === 'covers' &&
          valueMissing &&
          missingCoverTerms(coverages, draft.covers).length === 0
          ? 'reasons.vehicleValue'
          : `reasons.${stepId}`,
      );

  const showError = (title: string) => (cause: unknown) => {
    setError({ error: cause, title });
  };

  const createdFor = draft.policyholder ? `${draft.policyholder.partyId}|${draft.startDate}` : '';

  async function ensureSubmission(): Promise<JobRef> {
    if (job?.createdFor === createdFor) return job;
    if (!draft.policyholder) throw new Error('No policyholder');
    if (instant.current?.for !== createdFor) {
      instant.current = { for: createdFor, value: effectiveInstant(draft.startDate) };
    }
    const request = {
      policyholderPartyId: draft.policyholder.partyId,
      product: slice.product,
      channel: slice.channel,
      effectiveAt: instant.current.value,
      quoteType: 'FULL' as const,
    };
    const response = await createSubmission(request, submissionKey.keyFor(request));
    submissionKey.release();
    const ref: JobRef = {
      jobId: response.jobId,
      versionNo: response.versionNo,
      draftVersion: 0,
      createdFor,
    };
    setJob(ref);
    setQuote(null);
    setBound(null);
    setGateFailure(null);
    return ref;
  }

  /** Writes the risk to the server draft: the vehicle first (it yields the locator), then driver, covers, answers. */
  async function syncDraft(start: JobRef): Promise<JobRef> {
    const first = {
      jobId: start.jobId,
      versionNo: start.versionNo,
      expectedDraftVersion: start.draftVersion,
      instructions: [vehicleInstruction(draft, questions, start.vehicleLocator, 'PERSON')],
    };
    const afterVehicle = await updateDraft(first, vehicleKey.keyFor(first));
    vehicleKey.release();
    const vehicleLocator = afterVehicle.riskTree.vehicles[0]?.locator;
    if (!vehicleLocator) throw new Error('The API returned no vehicle locator.');
    const driver = driverInstruction(draft, questions, vehicleLocator, start.driverLocator);
    const second = {
      jobId: start.jobId,
      versionNo: afterVehicle.versionNo,
      expectedDraftVersion: afterVehicle.draftVersion,
      instructions: [
        ...(driver ? [driver] : []),
        coverageInstruction(coverages, draft.covers, vehicleLocator),
        answersInstruction(questions, draft.answers),
      ],
    };
    const afterRisk = await updateDraft(second, riskKey.keyFor(second));
    riskKey.release();
    const synced: JobRef = {
      ...start,
      versionNo: afterRisk.versionNo,
      draftVersion: afterRisk.draftVersion,
      vehicleLocator,
      ...(afterRisk.riskTree.drivers[0]?.locator
        ? { driverLocator: afterRisk.riskTree.drivers[0].locator }
        : {}),
    };
    setJob(synced);
    setSyncedAt({ fingerprint: currentFingerprint, at: new Date() });
    return synced;
  }

  /** After POL-ERR-STALE the server draft moved on: read its versions so that a retry sends the right ones. */
  async function resync(ref: JobRef) {
    try {
      const { job: server } = await getJob(ref.jobId);
      const version = server.versions.find((v) => v.versionNo === server.currentVersionNo);
      if (version) {
        setJob({ ...ref, versionNo: version.versionNo, draftVersion: version.draftVersion });
      }
    } catch {
      // The retry button stays; the next failure shows its own problem.
    }
  }

  const saveDraft = async () => {
    setError(null);
    let ref: JobRef | null = job;
    try {
      ref = await ensureSubmission();
      await syncDraft(ref);
      announce(t('draft.saved'));
    } catch (cause) {
      showError(t('draft.failed'))(cause);
      if (isApiError(cause) && cause.code === 'POL-ERR-STALE' && ref) await resync(ref);
    }
  };

  const calculate = async () => {
    setError(null);
    setBusy('quote');
    let ref: JobRef | null = job;
    try {
      ref = await ensureSubmission();
      const synced = await syncDraft(ref);
      const request = { jobId: synced.jobId, versionNo: synced.versionNo };
      const response = await quoteJob(request, quoteKey.keyFor(request));
      quoteKey.release();
      setJob({ ...synced, versionNo: response.versionNo });
      setQuote({ response, fingerprint: currentFingerprint });
      setBound(null);
      setGateFailure(null);
      announce(t('premium.calculated', { total: fmt.money(response.total) }));
    } catch (cause) {
      showError(t('premium.failed'))(cause);
      if (isApiError(cause) && cause.code === 'POL-ERR-STALE' && ref) await resync(ref);
    } finally {
      setBusy(null);
    }
  };

  const bind = async () => {
    if (!quote || !job) return;
    setError(null);
    setBusy('bind');
    try {
      const request = {
        jobId: quote.response.jobId,
        versionNo: quote.response.versionNo,
        paymentPlanOption: slice.paymentPlan,
        confirmation: true,
      };
      const response = await bindJob(request, bindKey.keyFor(request));
      bindKey.release();
      setConfirmOpen(false);
      setConfirmed(false);
      if (response.policyId && response.gateResults.every((g) => g.passed)) {
        setBound(response);
        setGateFailure(null);
        if (response.policyNumber)
          rememberRecent('policy', { id: response.policyId, label: response.policyNumber });
        announce(t('bind.bound', { number: response.policyNumber ?? '' }));
      } else {
        setGateFailure(response);
        // The bind re-evaluated the underwriting rules: show the issues as they are now.
        void refreshIssues();
      }
    } catch (cause) {
      setConfirmOpen(false);
      setConfirmed(false);
      showError(t('bind.failed'))(cause);
    } finally {
      setBusy(null);
    }
  };

  const navigateTo = async (id: StepId) => {
    setError(null);
    const leavingFirst = stepId === 'policyholder' && stepIds.indexOf(id) > 0;
    if (leavingFirst && job?.createdFor !== createdFor) {
      if (!valid.policyholder) return;
      setBusy('submission');
      try {
        await ensureSubmission();
      } catch (cause) {
        showError(t('submission.failed'))(cause);
        return;
      } finally {
        setBusy(null);
      }
    }
    setStepId(id);
    setVisited((s) => new Set(s).add(id));
  };

  /** «Go to <step>» of an input-problem banner: jump there, then focus the field once the step has rendered. */
  const goToField = (step: StepId, label: string | undefined) => {
    void navigateTo(step).then(() => {
      if (label) setPendingFocus({ step, label });
    });
  };
  useEffect(() => {
    if (!pendingFocus || pendingFocus.step !== stepId) return;
    const timer = setTimeout(() => {
      focusFieldByLabel(pendingFocus.label);
      setPendingFocus(null);
    }, 50);
    return () => {
      clearTimeout(timer);
    };
  }, [pendingFocus, stepId]);

  const commitReason = bound
    ? t('bind.reasons.alreadyBound')
    : !quote
      ? t('bind.reasons.noQuote')
      : !quoteIsFresh
        ? t('bind.reasons.stale')
        : quote.response.decision === 'DECLINE'
          ? t('bind.reasons.declined')
          : quote.response.decision === 'REFER' || !quote.response.bindable
            ? t('bind.reasons.referred')
            : undefined;

  const summary = (
    <KeyValueList
      aria-label={t('summary.title')}
      items={[
        { id: 'holder', label: t('summary.policyholder'), value: draft.policyholder?.label },
        {
          id: 'start',
          label: t('summary.start'),
          value: draft.startDate ? fmt.date(draft.startDate) : null,
        },
        {
          id: 'vehicle',
          label: t('summary.vehicle'),
          value: draft.vehicle.plate || null,
          kind: 'mono',
        },
        {
          id: 'total',
          label: t('summary.total'),
          value: quoteIsFresh ? fmt.money(quote.response.total) : t('summary.notCalculated'),
          kind: quoteIsFresh ? 'money' : 'text',
        },
        ...(quoteIsFresh
          ? [
              {
                id: 'decision',
                label: t('summary.decision'),
                value: t(`uw.decision.${quote.response.decision}`),
              },
            ]
          : []),
      ]}
    />
  );

  return (
    <div className={styles.page}>
      <PageHeader overline={t('overline')} title={t('title')} />
      <Wizard
        title={t('stepOf', {
          step: stepIds.indexOf(stepId) + 1,
          total: stepIds.length,
        })}
        steps={steps}
        currentId={stepId}
        onNavigate={(id) => {
          void navigateTo(id as StepId);
        }}
        bodyWidth={stepId === 'premium' || stepId === 'bind' ? 'wide' : 'form'}
        summary={summary}
        {...(nextReason ? { nextDisabledReason: nextReason } : {})}
        commit={{
          label: t('bind.commit'),
          onCommit: () => {
            setConfirmOpen(true);
          },
          isLoading: busy === 'bind',
          ...(commitReason ? { disabledReason: commitReason } : {}),
        }}
        onSaveDraft={() => {
          void saveDraft();
        }}
        isDirty={syncedAt !== null && syncedAt.fingerprint !== currentFingerprint}
        lastSavedAt={syncedAt?.at ?? null}
      >
        <div className={styles.stack}>
          {error ? (
            <InputProblemBanner
              error={error.error}
              title={error.title}
              questionLabels={questionLabels}
              coverNames={coverNames}
              onGo={goToField}
            />
          ) : null}
          {stepId === 'policyholder' ? (
            <PolicyholderStep
              draft={draft}
              setDraft={setDraft}
              product={product}
              initialPartyId={initialPartyId}
            />
          ) : null}
          {stepId === 'vehicle' ? (
            <VehicleStep draft={draft} setDraft={setDraft} valueNeeded={valueNeeded} />
          ) : null}
          {stepId === 'driver' ? <DriverStep draft={draft} setDraft={setDraft} /> : null}
          {stepId === 'covers' ? (
            <CoversStep draft={draft} setDraft={setDraft} catalogue={catalogue} />
          ) : null}
          {stepId === 'questions' ? (
            <QuestionsStep
              draft={draft}
              setDraft={setDraft}
              questionSet={questionSet}
              evaluation={evaluation.data}
            />
          ) : null}
          {stepId === 'premium' ? (
            <>
              <div className={styles.actions}>
                <Button
                  variant="primary"
                  isLoading={busy === 'quote'}
                  onPress={() => void calculate()}
                >
                  {quote ? t('premium.recalculate') : t('premium.calculate')}
                </Button>
              </div>
              {quote && !quoteIsFresh ? (
                <Banner variant="warning" live="status" title={t('premium.staleTitle')}>
                  {t('premium.staleBody')}
                </Banner>
              ) : null}
              {quote ? (
                <QuoteResult
                  quote={quote.response}
                  coverNames={coverNames}
                  productIsIllustrative={productIsIllustrative}
                />
              ) : (
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('premium.emptyTitle')}
                  description={t('premium.emptyBody')}
                />
              )}
            </>
          ) : null}
          {stepId === 'bind' ? (
            <BindStep
              draft={draft}
              quote={quoteIsFresh ? quote.response : null}
              productIsIllustrative={productIsIllustrative}
              bound={bound}
              gateFailure={gateFailure}
            />
          ) : null}
        </div>
        <Dialog
          title={t('bind.dialog.title')}
          tone="brand"
          isOpen={confirmOpen}
          isBusy={busy === 'bind'}
          onOpenChange={(open) => {
            setConfirmOpen(open);
            if (!open) setConfirmed(false);
          }}
          closeOnAction={false}
          primaryAction={{
            label: t('bind.dialog.confirm'),
            variant: 'commit',
            ...(confirmed ? {} : { disabledReason: t('bind.dialog.confirmRequired') }),
            onAction: () => {
              if (confirmed) void bind();
            },
          }}
        >
          <p>
            {t('bind.dialog.body', {
              total: quote ? fmt.money(quote.response.total) : '',
              plan: t('bind.planAnnual'),
            })}
          </p>
          <Checkbox isSelected={confirmed} onChange={setConfirmed}>
            {t('bind.dialog.check')}
          </Checkbox>
        </Dialog>
      </Wizard>
    </div>
  );
}

/** The bind gate's reason when open underwriting issues stop it (pol.Job.bind gate UW_ISSUES). */
const uwIssuesOpen = 'UW_ISSUES_OPEN';

function BindStep({
  draft,
  quote,
  productIsIllustrative,
  bound,
  gateFailure,
}: {
  draft: Draft;
  quote: JobQuoteResponse | null;
  productIsIllustrative: boolean;
  bound: JobBindResponse | null;
  gateFailure: JobBindResponse | null;
}) {
  const { t } = useTranslation('quote');
  const fmt = useFormat();
  if (bound) {
    return (
      <div className={styles.stack}>
        <Banner
          variant="success"
          live="status"
          title={t('bind.boundTitle', { number: bound.policyNumber ?? '' })}
        >
          {t('bind.boundBody', {
            state: bound.termState ? t(`bind.termState.${bound.termState}`) : '',
          })}
        </Banner>
        <div className={styles.actions}>
          {bound.policyId ? (
            <LinkButton variant="primary" to={`/policies/${bound.policyId}`}>
              {t('bind.openPolicy')}
            </LinkButton>
          ) : null}
        </div>
      </div>
    );
  }
  if (!quote) {
    return (
      <EmptyState
        kind="first-use"
        headingLevel={3}
        headline={t('bind.noQuoteTitle')}
        description={t('bind.noQuoteBody')}
      />
    );
  }
  return (
    <div className={styles.stack}>
      <QuoteWarnings codes={ratingWarnings(quote, productIsIllustrative)} />
      {gateFailure ? (
        <Banner variant="danger" live="alert" title={t('bind.gateFailedTitle')}>
          <p>{t('bind.gateFailedBody')}</p>
          <ul className={styles.problemList} aria-label={t('bind.gates')}>
            {gateFailure.gateResults.map((g) => (
              <li key={g.gate}>
                <strong>{t(`bind.gate.${g.gate}`, { defaultValue: g.gate })}</strong>
                {': '}
                {g.passed ? t('bind.gatePassed') : t('bind.gateFailed')}
                {g.reason && g.reason !== uwIssuesOpen ? ` (${g.reason})` : ''}
              </li>
            ))}
          </ul>
        </Banner>
      ) : null}
      {gateFailure?.gateResults.some((g) => !g.passed && g.reason === uwIssuesOpen) ? (
        <BindReferrals jobId={quote.jobId} />
      ) : null}
      <KeyValueList
        aria-label={t('bind.summary')}
        items={[
          { id: 'holder', label: t('summary.policyholder'), value: draft.policyholder?.label },
          { id: 'vehicle', label: t('summary.vehicle'), value: draft.vehicle.plate, kind: 'mono' },
          {
            id: 'premium',
            label: t('breakdown.premium'),
            value: fmt.money(quote.premium),
            kind: 'money',
          },
          {
            id: 'taxes',
            label: t('breakdown.taxes'),
            value: fmt.money(quote.taxes),
            kind: 'money',
          },
          {
            id: 'total',
            label: t('breakdown.total'),
            value: fmt.money(quote.total),
            kind: 'money',
          },
          {
            id: 'decision',
            label: t('summary.decision'),
            value: t(`uw.decision.${quote.decision}`),
          },
        ]}
      />
      <RadioGroup label={t('bind.plan')} value={slice.paymentPlan} onChange={() => undefined}>
        <Radio value={slice.paymentPlan} description={t('bind.planAnnualHelp')}>
          {t('bind.planAnnual')}
        </Radio>
      </RadioGroup>
    </div>
  );
}
