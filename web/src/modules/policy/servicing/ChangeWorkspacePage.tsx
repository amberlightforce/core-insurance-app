import { today as calendarToday, parseDate } from '@internationalized/date';
import { useCallback, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { isApiError } from '../../../api/client';
import { useIdempotencyKey } from '../../../api/idempotency';
import type { PolicyGetResponse } from '../../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  DatePicker,
  Dialog,
  EmptyState,
  KeyValueList,
  TextField,
  Wizard,
  announce,
  textColumn,
  type DataColumn,
  type StepItem,
} from '../../../design-system';
import { UnderwritingOutcome } from '../../quote/QuoteResult';
import { vehicleValueCovers, vehicleValueIssue, type VehicleForm } from '../../quote/state';
import { athensToday } from '../../quote/time';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { rememberRecent } from '../../staff/recent';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useServicingPolicy } from './useServicingPolicy';
import {
  bindServicingJob,
  createPolicyChange,
  getServicingJob,
  quoteServicingJob,
  updateServicingDraft,
  type JobBind,
  type JobQuote,
} from './api';
import {
  athensDateOf,
  changeInstant,
  formOf,
  lastDayOfTerm,
  vehicleChangeInstruction,
  vehicleDiff,
  type DiffRow,
} from './logic';
import { validateServicingVehicle } from './logic';
import { ServicingPreviewView } from './ServicingPreviewView';
import { ServicingProblem } from './ServicingProblem';
import { VehicleEditor, type VehicleMode } from './VehicleEditor';

const stepIds = ['when', 'vehicle', 'preview', 'confirm'] as const;
type StepId = (typeof stepIds)[number];

type Busy = null | 'start' | 'quote' | 'bind';

interface JobRef {
  jobId: string;
  versionNo: number;
  draftVersion: number;
  effectiveDate: string;
}

function safeDate(value: string) {
  try {
    return value ? parseDate(value) : null;
  } catch {
    return null;
  }
}

/** Mid-term change workspace (SCR-POL-13 subset): the quote wizard's step pattern around a PolicyChange job. */
export function ChangeWorkspacePage() {
  const { policyId = '' } = useParams();
  const policy = useServicingPolicy(policyId);
  return (
    <div className={styles.page}>
      <QueryView query={policy}>{(data) => <ChangeWizard key={policyId} data={data} />}</QueryView>
    </div>
  );
}

function ChangeWizard({ data }: { data: PolicyGetResponse }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const { policy, term } = data;
  const original = data.riskTree?.vehicles[0];
  const originalForm = useMemo(() => formOf(original), [original]);
  const changeable =
    term !== undefined && (term.state === 'IN_FORCE' || term.state === 'SCHEDULED');

  const today = athensToday();
  const [stepId, setStepId] = useState<StepId>('when');
  const [visited, setVisited] = useState<ReadonlySet<StepId>>(new Set(['when']));
  const [effectiveDate, setEffectiveDate] = useState(today);
  const [description, setDescription] = useState('');
  const [mode, setMode] = useState<VehicleMode>('edit');
  const [form, setFormState] = useState<VehicleForm>(originalForm);
  const [job, setJob] = useState<JobRef | null>(null);
  const [quote, setQuote] = useState<{ response: JobQuote; fingerprint: string } | null>(null);
  const [busy, setBusy] = useState<Busy>(null);
  const [error, setError] = useState<{ error: unknown; title: string } | null>(null);
  const [bound, setBound] = useState<JobBind | null>(null);
  const [gateFailure, setGateFailure] = useState<JobBind | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmed, setConfirmed] = useState(false);

  const createKey = useIdempotencyKey();
  const vehicleKey = useIdempotencyKey();
  const coverKey = useIdempotencyKey();
  const quoteKey = useIdempotencyKey();
  const bindKey = useIdempotencyKey();
  /** The effectiveAt instant is fixed per start so that a retry sends an identical payload (same key). */
  const instant = useRef<{ for: string; value: string } | null>(null);

  const setForm = useCallback((update: (f: VehicleForm) => VehicleForm) => {
    setFormState(update);
  }, []);
  const changeMode = useCallback(
    (next: VehicleMode) => {
      setMode(next);
      setFormState(next === 'edit' ? originalForm : formOf(undefined));
      setQuote(null);
    },
    [originalForm],
  );

  const termEnd = term?.period.to;
  const lastDay = termEnd ? lastDayOfTerm(termEnd) : undefined;
  const termStartDate = term ? athensDateOf(term.period.from) : undefined;
  const earliest = termStartDate && termStartDate > today ? termStartDate : today;

  const valueNeeded = (data.riskTree?.coverages ?? []).some(
    (c) => c.selected && vehicleValueCovers.includes(c.coverageCode),
  );
  const thisYear = new Date().getFullYear();
  const diff = vehicleDiff(originalForm, form);
  const vehicleErrors = validateServicingVehicle(form, thisYear, mode);
  const valueMissing = vehicleValueIssue(form.value, valueNeeded) !== undefined;
  const vehicleValid = Object.keys(vehicleErrors).length === 0 && !valueMissing;
  const hasChange = mode === 'edit' ? diff.length > 0 : form.plate.trim() !== originalForm.plate;

  const dateValid = effectiveDate >= earliest && (!lastDay || effectiveDate <= lastDay);
  const fingerprint = JSON.stringify({ mode, form, effectiveDate });
  const quoteIsFresh = quote !== null && quote.fingerprint === fingerprint;

  const valid: Record<StepId, boolean> = {
    when: changeable && dateValid,
    vehicle: vehicleValid && hasChange,
    preview: quoteIsFresh,
    confirm: bound !== null,
  };
  const steps: StepItem[] = stepIds.map((id) => ({
    id,
    label: t(`servicing.change.steps.${id}`),
    state:
      busy === 'start' && id === 'when'
        ? 'loading'
        : valid[id] && (visited.has(id) || id === 'preview')
          ? 'complete'
          : 'upcoming',
  }));
  const nextReason = valid[stepId]
    ? undefined
    : stepId === 'vehicle' && vehicleValid && !hasChange
      ? t('servicing.change.reasons.noChange')
      : stepId === 'vehicle' && valueMissing && Object.keys(vehicleErrors).length === 0
        ? t('servicing.change.reasons.vehicleValue')
        : t(`servicing.change.reasons.${stepId}`);

  const showError = (title: string) => (cause: unknown) => {
    setError({ error: cause, title });
  };

  /** Starts the PolicyChange job on leaving the first step; the effective date is then fixed. */
  async function ensureJob(): Promise<JobRef> {
    if (job) return job;
    if (instant.current?.for !== effectiveDate) {
      instant.current = { for: effectiveDate, value: changeInstant(effectiveDate) };
    }
    const request = {
      policyId: policy.policyId,
      effectiveAt: instant.current.value,
      ...(description.trim() ? { description: description.trim() } : {}),
    };
    const created = await createPolicyChange(request, createKey.keyFor(request));
    createKey.release();
    const { job: server } = await getServicingJob(created.jobId);
    const version = server.versions.find((v) => v.versionNo === server.currentVersionNo);
    const ref: JobRef = {
      jobId: created.jobId,
      versionNo: server.currentVersionNo,
      draftVersion: version?.draftVersion ?? 0,
      effectiveDate,
    };
    setJob(ref);
    return ref;
  }

  /** Writes the vehicle edit (or the replacement) to the draft; a replacement keeps the policy's covers on the new vehicle. */
  async function syncDraft(start: JobRef): Promise<JobRef> {
    const oldLocator = original?.locator;
    const instructions =
      mode === 'edit'
        ? [vehicleChangeInstruction(form, original, true)]
        : [
            vehicleChangeInstruction(form, original, false),
            ...(oldLocator ? [{ op: 'REMOVE_VEHICLE' as const, locator: oldLocator }] : []),
          ];
    const first = {
      jobId: start.jobId,
      versionNo: start.versionNo,
      expectedDraftVersion: start.draftVersion,
      instructions,
    };
    let after = await updateServicingDraft(first, vehicleKey.keyFor(first));
    vehicleKey.release();
    if (mode === 'replace' && oldLocator) {
      const replacement = after.riskTree.vehicles.find(
        (v) => v.locator && v.locator !== oldLocator,
      );
      const stale = after.riskTree.coverages.some((c) => c.elementLocator === oldLocator);
      const newLocator = replacement?.locator;
      if (newLocator && stale) {
        const second = {
          jobId: start.jobId,
          versionNo: after.versionNo,
          expectedDraftVersion: after.draftVersion,
          instructions: [
            {
              op: 'SET_COVERAGES' as const,
              coverages: after.riskTree.coverages.map((c) =>
                c.elementLocator === oldLocator ? { ...c, elementLocator: newLocator } : c,
              ),
            },
          ],
        };
        after = await updateServicingDraft(second, coverKey.keyFor(second));
        coverKey.release();
      }
    }
    const synced: JobRef = {
      ...start,
      versionNo: after.versionNo,
      draftVersion: after.draftVersion,
    };
    setJob(synced);
    return synced;
  }

  /** After POL-ERR-STALE the server draft moved on: read its versions so that a retry sends the right ones. */
  async function resync(ref: JobRef) {
    try {
      const { job: server } = await getServicingJob(ref.jobId);
      const version = server.versions.find((v) => v.versionNo === server.currentVersionNo);
      if (version) {
        setJob({ ...ref, versionNo: version.versionNo, draftVersion: version.draftVersion });
      }
    } catch {
      // The retry button stays; the next failure shows its own problem.
    }
  }

  const calculate = async () => {
    setError(null);
    setBusy('quote');
    let ref: JobRef | null = job;
    try {
      ref = await ensureJob();
      const synced = await syncDraft(ref);
      const request = { jobId: synced.jobId, versionNo: synced.versionNo };
      const response = await quoteServicingJob(request, quoteKey.keyFor(request));
      quoteKey.release();
      setJob({ ...synced, versionNo: response.versionNo });
      setQuote({ response, fingerprint });
      setBound(null);
      setGateFailure(null);
      announce(t('servicing.change.preview.calculated'));
    } catch (cause) {
      showError(t('servicing.change.preview.failed'))(cause);
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
        paymentPlanOption: term?.paymentPlanRef ?? 'ANNUAL',
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
        announce(t('servicing.change.bound.title'));
      } else {
        setGateFailure(response);
      }
    } catch (cause) {
      setConfirmOpen(false);
      setConfirmed(false);
      showError(t('servicing.change.confirm.failed'))(cause);
    } finally {
      setBusy(null);
    }
  };

  const navigateTo = async (id: StepId) => {
    setError(null);
    const leavingFirst = stepId === 'when' && stepIds.indexOf(id) > 0;
    if (leavingFirst && !job) {
      if (!valid.when) return;
      setBusy('start');
      try {
        await ensureJob();
      } catch (cause) {
        showError(t('servicing.change.when.failed'))(cause);
        return;
      } finally {
        setBusy(null);
      }
    }
    setStepId(id);
    setVisited((s) => new Set(s).add(id));
  };

  const commitReason = bound
    ? t('servicing.change.reasons.alreadyBound')
    : !quote
      ? t('servicing.change.reasons.noQuote')
      : !quoteIsFresh
        ? t('servicing.change.reasons.stale')
        : quote.response.decision === 'DECLINE'
          ? t('servicing.change.reasons.declined')
          : quote.response.decision === 'REFER' || !quote.response.bindable
            ? t('servicing.change.reasons.referred')
            : undefined;

  const preview = quote?.response.servicingPreview;
  const summary = (
    <KeyValueList
      aria-label={t('servicing.change.summary.title')}
      items={[
        {
          id: 'policy',
          label: t('servicing.change.summary.policy'),
          value: policy.policyNumber,
          kind: 'mono',
        },
        {
          id: 'effective',
          label: t('servicing.change.summary.effective'),
          value: fmt.date(effectiveDate),
        },
        {
          id: 'vehicle',
          label: t('servicing.change.summary.vehicle'),
          value: form.plate || null,
          kind: 'mono',
        },
        {
          id: 'effect',
          label: t('servicing.change.summary.effect'),
          value:
            quoteIsFresh && preview
              ? fmt.money(preview.totalChange)
              : t('servicing.change.summary.notCalculated'),
          kind: quoteIsFresh && preview ? 'money' : 'text',
        },
      ]}
    />
  );

  const header = (
    <PageHeader
      overline={t('servicing.change.overline')}
      title={t('servicing.change.title')}
      recordId={policy.policyNumber}
      actions={
        <LinkButton variant="secondary" to={`/policies/${policy.policyId}`}>
          {t('servicing.back')}
        </LinkButton>
      }
    />
  );

  if (!changeable) {
    return (
      <>
        {header}
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('servicing.change.notChangeable.title')}
          description={t('servicing.change.notChangeable.body')}
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
      <Wizard
        title={t('servicing.change.stepOf', {
          step: stepIds.indexOf(stepId) + 1,
          total: stepIds.length,
        })}
        steps={steps}
        currentId={stepId}
        onNavigate={(id) => {
          void navigateTo(id as StepId);
        }}
        bodyWidth={stepId === 'preview' || stepId === 'confirm' ? 'wide' : 'form'}
        summary={summary}
        {...(nextReason ? { nextDisabledReason: nextReason } : {})}
        commit={{
          label: t('servicing.change.confirm.commit'),
          onCommit: () => {
            setConfirmOpen(true);
          },
          isLoading: busy === 'bind',
          ...(commitReason ? { disabledReason: commitReason } : {}),
        }}
      >
        <div className={styles.stack}>
          {error ? (
            <ServicingProblem
              error={error.error}
              title={error.title}
              onGoToVehicle={() => {
                void navigateTo('vehicle');
              }}
            />
          ) : null}
          {stepId === 'when' ? (
            <WhenStep
              effectiveDate={effectiveDate}
              onDate={setEffectiveDate}
              description={description}
              onDescription={setDescription}
              earliest={earliest}
              lastDay={lastDay}
              locked={job !== null}
              dateValid={dateValid}
            />
          ) : null}
          {stepId === 'vehicle' ? (
            <VehicleEditor
              mode={mode}
              onModeChange={changeMode}
              form={form}
              onChange={setForm}
              valueNeeded={valueNeeded}
            />
          ) : null}
          {stepId === 'preview' ? (
            <>
              <div className={styles.actions}>
                <Button
                  variant="primary"
                  isLoading={busy === 'quote'}
                  onPress={() => void calculate()}
                >
                  {quote
                    ? t('servicing.change.preview.recalculate')
                    : t('servicing.change.preview.calculate')}
                </Button>
              </div>
              {quote && !quoteIsFresh ? (
                <Banner
                  variant="warning"
                  live="status"
                  title={t('servicing.change.preview.staleTitle')}
                >
                  {t('servicing.change.preview.staleBody')}
                </Banner>
              ) : null}
              {quote ? (
                <>
                  <UnderwritingOutcome quote={quote.response} />
                  <DiffSection mode={mode} before={originalForm} after={form} diff={diff} />
                  {preview ? (
                    <ServicingPreviewView preview={preview} artefactHash={term.artefactHash} />
                  ) : (
                    <Banner
                      variant="warning"
                      live="status"
                      title={t('servicing.change.preview.noPreview')}
                    >
                      {t('servicing.change.preview.noPreviewBody')}
                    </Banner>
                  )}
                </>
              ) : (
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('servicing.change.preview.emptyTitle')}
                  description={t('servicing.change.preview.emptyBody')}
                />
              )}
            </>
          ) : null}
          {stepId === 'confirm' ? (
            <ConfirmStep
              artefactHash={term.artefactHash}
              policyId={policy.policyId}
              quote={quoteIsFresh ? quote.response : null}
              effectiveDate={effectiveDate}
              bound={bound}
              gateFailure={gateFailure}
            />
          ) : null}
        </div>
        <Dialog
          title={t('servicing.change.confirm.dialog.title')}
          tone="brand"
          isOpen={confirmOpen}
          isBusy={busy === 'bind'}
          onOpenChange={(open) => {
            setConfirmOpen(open);
            if (!open) setConfirmed(false);
          }}
          closeOnAction={false}
          primaryAction={{
            label: t('servicing.change.confirm.dialog.confirm'),
            variant: 'commit',
            ...(confirmed
              ? {}
              : { disabledReason: t('servicing.change.confirm.dialog.confirmRequired') }),
            onAction: () => {
              if (confirmed) void bind();
            },
          }}
        >
          <p>
            {t('servicing.change.confirm.dialog.body', {
              date: fmt.date(effectiveDate),
              effect: preview ? fmt.money(preview.totalChange) : '',
            })}
          </p>
          <Checkbox isSelected={confirmed} onChange={setConfirmed}>
            {t('servicing.change.confirm.dialog.check')}
          </Checkbox>
        </Dialog>
      </Wizard>
    </>
  );
}

function WhenStep({
  effectiveDate,
  onDate,
  description,
  onDescription,
  earliest,
  lastDay,
  locked,
  dateValid,
}: {
  effectiveDate: string;
  onDate: (date: string) => void;
  description: string;
  onDescription: (text: string) => void;
  earliest: string;
  lastDay: string | undefined;
  locked: boolean;
  dateValid: boolean;
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const min = safeDate(earliest) ?? calendarToday('Europe/Athens');
  const max = lastDay ? safeDate(lastDay) : null;
  return (
    <Section title={t('servicing.change.when.title')} headingLevel={3}>
      <p className={styles.muted}>
        {lastDay
          ? t('servicing.change.when.range', { from: fmt.date(earliest), to: fmt.date(lastDay) })
          : t('servicing.change.when.rangeOpen', { from: fmt.date(earliest) })}
      </p>
      <DatePicker
        label={t('servicing.change.when.date')}
        description={t(locked ? 'servicing.change.when.locked' : 'servicing.change.when.dateHelp')}
        isRequired
        isReadOnly={locked}
        minValue={min}
        {...(max ? { maxValue: max } : {})}
        value={safeDate(effectiveDate)}
        onChange={(value) => {
          if (value) onDate(value.toString());
        }}
        {...(!dateValid ? { errorMessage: t('servicing.change.reasons.when') } : {})}
      />
      <TextField
        label={t('servicing.change.when.description')}
        helperText={t('servicing.change.when.descriptionHelp')}
        multiline
        isReadOnly={locked}
        value={description}
        onChange={onDescription}
      />
    </Section>
  );
}

function DiffSection({
  mode,
  before,
  after,
  diff,
}: {
  mode: VehicleMode;
  before: VehicleForm;
  after: VehicleForm;
  diff: DiffRow[];
}) {
  const { t } = useTranslation('policy');
  const q = useTranslation('quote').t;
  const columns = useMemo<DataColumn<DiffRow>[]>(
    () => [
      textColumn<DiffRow>(
        'field',
        t('servicing.change.diff.field'),
        (r) => q(`vehicle.${fieldKey[r.field]}`),
        {
          size: 240,
        },
      ),
      textColumn<DiffRow>('before', t('servicing.change.diff.before'), (r) => r.before || '—'),
      textColumn<DiffRow>('after', t('servicing.change.diff.after'), (r) => r.after || '—'),
    ],
    [t, q],
  );
  return (
    <Section title={t('servicing.change.diff.title')} headingLevel={3}>
      {mode === 'replace' ? (
        <p>{t('servicing.change.diff.replaced', { from: before.plate, to: after.plate })}</p>
      ) : null}
      {diff.length > 0 ? (
        <SimpleTable<DiffRow>
          aria-label={t('servicing.change.diff.table')}
          columns={columns}
          data={diff}
          getRowId={(r) => r.field}
        />
      ) : (
        <p className={styles.muted}>{t('servicing.change.diff.none')}</p>
      )}
    </Section>
  );
}

/** Maps a form field to the label key under `quote:vehicle`. */
const fieldKey: Record<DiffRow['field'], string> = {
  plate: 'plate',
  vin: 'vin',
  make: 'make',
  model: 'model',
  firstRegistrationYear: 'year',
  engineCapacityCc: 'engine',
  powerKw: 'power',
  fuelType: 'fuel',
  value: 'value',
  garagingPostcode: 'garaging',
};

function ConfirmStep({
  policyId,
  artefactHash,
  quote,
  effectiveDate,
  bound,
  gateFailure,
}: {
  policyId: string;
  artefactHash: string;
  quote: JobQuote | null;
  effectiveDate: string;
  bound: JobBind | null;
  gateFailure: JobBind | null;
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  if (bound) {
    return (
      <div className={styles.stack}>
        <Banner variant="success" live="status" title={t('servicing.change.bound.title')}>
          {t('servicing.change.bound.body', { date: fmt.date(effectiveDate) })}
        </Banner>
        {bound.servicingPreview ? (
          <ServicingPreviewView preview={bound.servicingPreview} artefactHash={artefactHash} />
        ) : null}
        <div className={styles.actions}>
          <LinkButton variant="primary" to={`/policies/${policyId}`}>
            {t('servicing.openPolicy')}
          </LinkButton>
        </div>
      </div>
    );
  }
  if (!quote) {
    return (
      <EmptyState
        kind="first-use"
        headingLevel={3}
        headline={t('servicing.change.confirm.noQuoteTitle')}
        description={t('servicing.change.confirm.noQuoteBody')}
      />
    );
  }
  return (
    <div className={styles.stack}>
      {gateFailure ? (
        <Banner variant="danger" live="alert" title={t('servicing.change.confirm.gateFailedTitle')}>
          <p>{t('servicing.change.confirm.gateFailedBody')}</p>
          <ul className={styles.problemList} aria-label={t('servicing.change.confirm.gates')}>
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
      {quote.servicingPreview ? (
        <ServicingPreviewView preview={quote.servicingPreview} artefactHash={artefactHash} />
      ) : null}
    </div>
  );
}
