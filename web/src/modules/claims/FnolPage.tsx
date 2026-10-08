import { parseDate } from '@internationalized/date';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import type {
  DuplicateDecision,
  FnolIssue,
  FnolSubmitRequest,
  FnolSubmitResponse,
  FnolValidateResponse,
  PolicySearchItem,
} from '../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  DatePicker,
  ErrorSummary,
  KeyValueList,
  Radio,
  RadioGroup,
  Select,
  TextField,
  announce,
  type ErrorSummaryItem,
} from '../../design-system';
import { TermStatusPill } from '../policy/TermStatusPill';
import { athensToday } from '../quote/time';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { problemOf } from '../staff/problem';
import { rememberRecent } from '../staff/recent';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { searchPolicies, submitFnol, usePolicyCoverages, validateFnol } from './api';
import { duplicateReasons, lossCauses, receiptMedia } from './codes';
import { athensInstant, timePattern } from './time';

/** Server field name → the form control it belongs to. */
const fieldTargets: Record<string, string> = {
  policyId: 'fnol-policy',
  policyNumber: 'fnol-policy',
  lossAt: 'fnol-lossDate',
  noticeOn: 'fnol-noticeOn',
  lossCause: 'fnol-lossCause',
  lossLocation: 'fnol-location',
  description: 'fnol-description',
  receiptMedium: 'fnol-receiptMedium',
  duplicateDecision: 'fnol-duplicate',
};

function targetOf(field: string | null | undefined): string | null {
  if (!field) return null;
  const root = field.split(/[.[]/)[0] ?? field;
  return fieldTargets[root] ?? null;
}

/** First-use form state. The description and location are free text and stay in memory only. */
interface FormState {
  lossDate: string;
  lossTime: string;
  noticeOn: string;
  lossCause: string | null;
  receiptMedium: string | null;
  location: string;
  description: string;
  ownDamage: boolean;
}

function initialForm(): FormState {
  const today = athensToday();
  return {
    lossDate: today,
    lossTime: '',
    noticeOn: today,
    lossCause: null,
    receiptMedium: 'TELEPHONE',
    location: '',
    description: '',
    ownDamage: true,
  };
}

interface Decision {
  action: 'LINK' | 'OVERRIDE';
  linkedClaimId: string | null;
  reasonCode: string | null;
}

function isDecisionReady(decision: Decision): boolean {
  return (
    decision.reasonCode !== null &&
    (decision.action === 'OVERRIDE' || decision.linkedClaimId !== null)
  );
}

/**
 * SCR-CLM-01 subset: first notification of loss by staff. Find the policy by number, enter the loss, validate
 * (clm.Fnol.validate) and submit (clm.Fnol.submit, one Idempotency-Key per submission reused on retry). Probable
 * duplicates need a LINK / OVERRIDE decision with a reason; a policy not in force at the loss date opens the claim
 * with coverage in question. The description and loss location are free text and are never put in a URL.
 */
export function FnolPage() {
  const { t } = useTranslation('claims');
  const navigate = useNavigate();
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();

  const [policyNumber, setPolicyNumber] = useState('');
  const [policy, setPolicy] = useState<PolicySearchItem | null>(null);
  const [candidatesFound, setCandidatesFound] = useState<PolicySearchItem[] | null>(null);
  // The chooser shows after the first check finds duplicates; a missing decision is an error from the next submit on.
  const [decisionShown, setDecisionShown] = useState(false);
  const [decisionTried, setDecisionTried] = useState(false);
  const coverages = usePolicyCoverages(policy?.policyId ?? null);
  const ownDamageCode = coverages.data?.ownDamage ?? null;
  const [form, setForm] = useState<FormState>(initialForm);
  const [decision, setDecision] = useState<Decision>({
    action: 'OVERRIDE',
    linkedClaimId: null,
    reasonCode: null,
  });
  const [validation, setValidation] = useState<FnolValidateResponse | null>(null);
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});
  const [attempts, setAttempts] = useState(0);
  const [created, setCreated] = useState<FnolSubmitResponse | null>(null);

  const patch = (change: Partial<FormState>) => {
    setForm((current) => ({ ...current, ...change }));
    setValidation(null);
    setServerErrors({});
  };

  const find = useMutation({
    mutationFn: (number: string) => searchPolicies(number),
    onSuccess: (page) => {
      const items = page.items as PolicySearchItem[];
      setCandidatesFound(items);
      setPolicy(items.length === 1 ? (items[0] ?? null) : null);
      setValidation(null);
      setServerErrors({});
    },
  });

  const check = useMutation({
    mutationFn: (request: FnolSubmitRequest) => validateFnol(request),
  });
  const submit = useMutation({
    mutationFn: ({ request, key }: { request: FnolSubmitRequest; key: string }) =>
      submitFnol(request, key),
  });

  // Local checks first; the server re-checks everything (the API is the authority, not this form).
  const errors: Record<string, string> = {};
  const lossInstant =
    form.lossDate && timePattern.test(form.lossTime)
      ? athensInstant(form.lossDate, form.lossTime)
      : null;
  if (!policy) errors['fnol-policy'] = t('fnol.errors.policy');
  if (!form.lossDate) errors['fnol-lossDate'] = t('fnol.errors.lossDate');
  else if (form.lossDate > athensToday()) errors['fnol-lossDate'] = t('fnol.errors.lossFuture');
  if (!timePattern.test(form.lossTime)) errors['fnol-lossTime'] = t('fnol.errors.lossTime');
  if (!form.noticeOn) errors['fnol-noticeOn'] = t('fnol.errors.noticeOn');
  else if (form.noticeOn > athensToday()) errors['fnol-noticeOn'] = t('fnol.errors.noticeFuture');
  else if (form.lossDate && form.noticeOn < form.lossDate)
    errors['fnol-noticeOn'] = t('fnol.errors.noticeBeforeLoss');
  if (!form.lossCause) errors['fnol-lossCause'] = t('fnol.errors.lossCause');
  if (form.location.trim() === '') errors['fnol-location'] = t('fnol.errors.location');
  if (form.description.trim() === '') errors['fnol-description'] = t('fnol.errors.description');

  const duplicates = validation?.duplicateCandidates ?? [];
  const needsDecision = duplicates.length > 0;
  const decisionReady = !needsDecision || isDecisionReady(decision);
  const decisionErrors: Record<string, string> = {};
  if (needsDecision && decisionTried && !decisionReady)
    decisionErrors['fnol-duplicate'] = t('fnol.errors.decision');

  const all = { ...errors, ...serverErrors, ...decisionErrors };
  const shown = attempts > 0 ? all : serverErrors;
  const summary: ErrorSummaryItem[] =
    attempts > 0
      ? Object.entries(all)
          .filter(([fieldId]) => fieldId !== 'fnol-general')
          .map(([fieldId, message]) => ({ fieldId, message }))
      : [];

  const buildRequest = (withDecision: boolean): FnolSubmitRequest | null => {
    if (!policy || !lossInstant || !form.lossCause) return null;
    const duplicateDecision: DuplicateDecision | undefined =
      withDecision && decision.reasonCode
        ? {
            action: decision.action,
            reasonCode: decision.reasonCode,
            ...(decision.action === 'LINK' && decision.linkedClaimId
              ? { linkedClaimId: decision.linkedClaimId }
              : {}),
          }
        : undefined;
    return {
      lineOfBusiness: 'MOTOR',
      policyId: policy.policyId,
      policyNumber: policy.policyNumber,
      lossAt: lossInstant,
      noticeOn: form.noticeOn,
      lossCause: form.lossCause,
      lossLocation: form.location.trim(),
      description: form.description.trim(),
      channel: 'STAFF',
      ...(form.receiptMedium ? { receiptMedium: form.receiptMedium } : {}),
      reporter: { partyId: policy.insuredPartyId, relationship: 'INSURED' },
      ...(form.ownDamage && ownDamageCode
        ? { exposures: [{ kind: 'OWN_DAMAGE', coverageCode: ownDamageCode }] }
        : {}),
      ...(duplicateDecision ? { duplicateDecision } : {}),
    };
  };

  const applyIssues = (issues: FnolIssue[]) => {
    const next: Record<string, string> = {};
    for (const issue of issues) {
      const target = targetOf(issue.field) ?? 'fnol-general';
      next[target] ??= issue.message;
    }
    setServerErrors(next);
  };

  const applyProblem = (error: unknown) => {
    const problem = problemOf(error);
    const next: Record<string, string> = {};
    for (const e of problem.errors ?? []) {
      const target = targetOf(e.field);
      if (target) next[target] ??= e.message ?? e.code;
    }
    // LOSS-DATE has no field list when raised for a single field; keep the banner message in that case.
    setServerErrors(next);
  };

  const runCheck = async (): Promise<FnolValidateResponse | null> => {
    const request = buildRequest(true);
    if (!request) return null;
    try {
      const raw = await check.mutateAsync(request);
      // Probable duplicates are answered by the chooser below, not reported as a failed check.
      const issues = raw.issues.filter((i) => i.code !== 'CLM-ERR-DUPLICATE-CANDIDATES');
      const result = {
        ...raw,
        issues,
        valid: raw.valid || (issues.length === 0 && raw.duplicateCandidates.length > 0),
      };
      setValidation(result);
      applyIssues(issues);
      return result;
    } catch (error) {
      applyProblem(error);
      return null;
    }
  };

  /** The loss instant may not be in the future (checked on action, not while rendering). */
  const lossInFuture = (): boolean => {
    if (!lossInstant || new Date(lossInstant).getTime() <= Date.now()) return false;
    setServerErrors({ 'fnol-lossTime': t('fnol.errors.lossFuture') });
    return true;
  };

  const onValidate = async () => {
    setAttempts((n) => n + 1);
    if (Object.keys(errors).length > 0 || lossInFuture()) return;
    const result = await runCheck();
    if (result?.valid) announce(t('fnol.validation.ok'));
  };

  const onSubmit = async () => {
    setAttempts((n) => n + 1);
    if (Object.keys(errors).length > 0 || lossInFuture()) return;
    const result = await runCheck();
    if (!result?.valid) return;
    if (result.duplicateCandidates.length > 0 && !isDecisionReady(decision)) {
      if (decisionShown) setDecisionTried(true);
      setDecisionShown(true);
      return;
    }
    const request = buildRequest(result.duplicateCandidates.length > 0);
    if (!request) return;
    try {
      const response = await submit.mutateAsync({ request, key: keyFor(request) });
      release();
      setCreated(response);
      rememberRecent('claim', { id: response.claimId, label: response.claimNumber });
      announce(t('fnol.created', { number: response.claimNumber }));
    } catch (error) {
      applyProblem(error);
      // A duplicate answer at submit (raced, or the check missed): show the candidates by validating again.
      if (isApiError(error) && error.code === 'CLM-ERR-DUPLICATE-CANDIDATES') {
        try {
          const retry = buildRequest(false);
          if (retry) setValidation(await validateFnol(retry));
        } catch {
          // The submit problem banner already explains; the candidates just stay hidden.
        }
      }
    }
  };

  const busy = check.isPending || submit.isPending;
  const notInForce = validation?.policyInForce === false;

  if (created) {
    return (
      <div className={styles.page}>
        <PageHeader overline={t('overline')} title={t('fnol.title')} />
        <Banner
          variant="success"
          live="status"
          title={t('fnol.created', { number: created.claimNumber })}
        >
          {t('fnol.createdBody', { segment: created.handlingSegment })}
        </Banner>
        {created.claim.coverageInQuestion ? (
          <Banner variant="warning" title={t('fnol.coverageInQuestion.title')}>
            {t('fnol.coverageInQuestion.body')}
          </Banner>
        ) : null}
        <Section title={t('fnol.indications.title')}>
          {created.coverageIndications.length > 0 ? (
            <KeyValueList
              aria-label={t('fnol.indications.title')}
              items={created.coverageIndications.map((c) => ({
                id: c.coverageCode,
                label: c.coverageCode,
                kind: 'text' as const,
                value: t(`indication.${c.indication}`),
              }))}
            />
          ) : (
            <p className={styles.muted}>{t('fnol.indications.none')}</p>
          )}
        </Section>
        <div className={styles.actions}>
          <Button
            variant="primary"
            onPress={() => {
              void navigate(`/claims/${created.claimId}`);
            }}
          >
            {t('fnol.openClaim')}
          </Button>
          <Button
            variant="secondary"
            onPress={() => {
              setCreated(null);
              setForm(initialForm());
              setPolicy(null);
              setCandidatesFound(null);
              setPolicyNumber('');
              setValidation(null);
              setServerErrors({});
              setAttempts(0);
              setDecisionShown(false);
              setDecisionTried(false);
              setDecision({ action: 'OVERRIDE', linkedClaimId: null, reasonCode: null });
            }}
          >
            {t('fnol.another')}
          </Button>
        </div>
      </div>
    );
  }

  const policyErrors = shown['fnol-policy'];

  return (
    <div className={styles.page}>
      <PageHeader overline={t('overline')} title={t('fnol.title')} subtitle={t('fnol.subtitle')} />
      <ErrorSummary errors={summary} focusKey={attempts} />
      <Section title={t('fnol.policy.title')}>
        <form
          noValidate
          className={styles.row}
          onSubmit={(event) => {
            event.preventDefault();
            if (policyNumber.trim()) find.mutate(policyNumber.trim());
          }}
        >
          <TextField
            id="fnol-policy"
            label={t('fnol.policy.number')}
            mono
            isRequired
            value={policyNumber}
            onChange={setPolicyNumber}
            errorMessage={policyErrors}
          />
          <Button type="submit" variant="secondary" isLoading={find.isPending}>
            {t('fnol.policy.find')}
          </Button>
        </form>
        {find.isError ? <ProblemBanner error={find.error} title={t('fnol.policy.failed')} /> : null}
        {candidatesFound?.length === 0 ? (
          <Banner variant="warning" live="status" title={t('fnol.policy.notFoundTitle')}>
            {t('fnol.policy.notFoundBody')}
          </Banner>
        ) : null}
        {candidatesFound && candidatesFound.length > 1 && !policy ? (
          <div className={styles.stack}>
            {candidatesFound.map((p) => (
              <Button
                key={p.policyId}
                variant="secondary"
                onPress={() => {
                  setPolicy(p);
                }}
              >
                {p.policyNumber}
              </Button>
            ))}
          </div>
        ) : null}
        {policy ? (
          <KeyValueList
            aria-label={t('fnol.policy.summary')}
            items={[
              {
                id: 'number',
                label: t('fnol.policy.number'),
                value: policy.policyNumber,
                kind: 'mono',
              },
              {
                id: 'product',
                label: t('fnol.policy.product'),
                value: policy.productCode,
                kind: 'mono',
              },
              {
                id: 'status',
                label: t('fnol.policy.status'),
                value: policy.status ? <TermStatusPill state={policy.status} /> : null,
              },
              {
                id: 'term',
                label: t('fnol.policy.term'),
                value: policy.termPeriod
                  ? `${fmt.date(policy.termPeriod.from)} – ${policy.termPeriod.to ? fmt.date(policy.termPeriod.to) : t('fnol.policy.open')}`
                  : null,
              },
              {
                id: 'open',
                label: t('fnol.policy.view'),
                value: (
                  <LinkButton to={`/policies/${policy.policyId}`}>
                    {t('fnol.policy.openPolicy')}
                  </LinkButton>
                ),
              },
            ]}
          />
        ) : null}
      </Section>

      <Section title={t('fnol.loss.title')}>
        <div className={styles.grid}>
          <DatePicker
            label={t('fnol.loss.date')}
            isRequired
            name="fnol-lossDate"
            value={form.lossDate ? parseDate(form.lossDate) : null}
            onChange={(value) => {
              patch({ lossDate: value ? value.toString() : '' });
            }}
            {...(shown['fnol-lossDate'] ? { errorMessage: shown['fnol-lossDate'] } : {})}
          />
          <TextField
            id="fnol-lossTime"
            label={t('fnol.loss.time')}
            mono
            isRequired
            placeholder="HH:mm"
            helperText={t('fnol.loss.timeHelp')}
            maxLength={5}
            value={form.lossTime}
            onChange={(lossTime) => {
              patch({ lossTime });
            }}
            errorMessage={shown['fnol-lossTime']}
          />
          <DatePicker
            label={t('fnol.loss.noticeOn')}
            isRequired
            name="fnol-noticeOn"
            value={form.noticeOn ? parseDate(form.noticeOn) : null}
            onChange={(value) => {
              patch({ noticeOn: value ? value.toString() : '' });
            }}
            {...(shown['fnol-noticeOn'] ? { errorMessage: shown['fnol-noticeOn'] } : {})}
          />
          <Select
            id="fnol-lossCause"
            label={t('fnol.loss.cause')}
            isRequired
            helperText={t('fnol.loss.causeIllustrative')}
            options={lossCauses.map((c) => ({ id: c, label: t(`codes.lossCause.${c}`) }))}
            value={form.lossCause}
            onChange={(lossCause) => {
              patch({ lossCause });
            }}
            errorMessage={shown['fnol-lossCause']}
          />
          <Select
            id="fnol-receiptMedium"
            label={t('fnol.loss.medium')}
            options={receiptMedia.map((m) => ({ id: m, label: t(`codes.medium.${m}`) }))}
            value={form.receiptMedium}
            onChange={(receiptMedium) => {
              patch({ receiptMedium });
            }}
            errorMessage={shown['fnol-receiptMedium']}
          />
        </div>
        <TextField
          id="fnol-location"
          label={t('fnol.loss.location')}
          isRequired
          maxLength={500}
          value={form.location}
          onChange={(location) => {
            patch({ location });
          }}
          errorMessage={shown['fnol-location']}
        />
        <TextField
          id="fnol-description"
          label={t('fnol.loss.description')}
          isRequired
          multiline
          maxLength={4000}
          helperText={t('fnol.loss.descriptionHelp')}
          value={form.description}
          onChange={(description) => {
            patch({ description });
          }}
          errorMessage={shown['fnol-description']}
        />
        <Checkbox
          isSelected={form.ownDamage && ownDamageCode !== null}
          isDisabled={ownDamageCode === null}
          onChange={(ownDamage) => {
            patch({ ownDamage });
          }}
        >
          {t('fnol.loss.ownDamage')}
        </Checkbox>
      </Section>

      {notInForce ? (
        <Banner variant="warning" live="status" title={t('fnol.coverageInQuestion.title')}>
          {t('fnol.coverageInQuestion.beforeSubmit')}
        </Banner>
      ) : null}
      {validation && validation.valid && !needsDecision ? (
        <Banner variant="success" live="status" title={t('fnol.validation.ok')}>
          {t('fnol.validation.okBody')}
        </Banner>
      ) : null}
      {validation && !validation.valid ? (
        <Banner variant="danger" live="alert" title={t('fnol.validation.failed')}>
          <ul className={styles.problemList}>
            {validation.issues.map((issue) => (
              <li key={`${issue.code}:${issue.field ?? ''}`}>
                {issue.field ? <span className="ds-mono">{issue.field}</span> : null}
                {issue.field ? ': ' : null}
                {issue.message}
              </li>
            ))}
          </ul>
        </Banner>
      ) : null}

      {needsDecision ? (
        <Section title={t('fnol.duplicate.title')}>
          <Banner variant="warning" live="status" title={t('fnol.duplicate.banner')}>
            {t('fnol.duplicate.body')}
          </Banner>
          <ul className={styles.stack} aria-label={t('fnol.duplicate.candidates')}>
            {duplicates.map((d) => (
              <li key={d.claimId} className={styles.tags}>
                <LinkButton to={`/claims/${d.claimId}`}>{d.claimNumber}</LinkButton>
                <span className="ds-caption">
                  {d.reasons.map((r) => t(`duplicate.reason.${r}`)).join(' · ')}
                </span>
              </li>
            ))}
          </ul>
          <RadioGroup
            label={t('fnol.duplicate.choice')}
            value={decision.action}
            onChange={(action) => {
              setDecision((current) => ({
                ...current,
                action: action === 'LINK' ? 'LINK' : 'OVERRIDE',
                linkedClaimId:
                  action === 'LINK'
                    ? (current.linkedClaimId ?? duplicates[0]?.claimId ?? null)
                    : null,
              }));
            }}
          >
            <Radio value="LINK">{t('fnol.duplicate.link')}</Radio>
            <Radio value="OVERRIDE">{t('fnol.duplicate.override')}</Radio>
          </RadioGroup>
          {decision.action === 'LINK' && duplicates.length > 1 ? (
            <Select
              label={t('fnol.duplicate.linkTo')}
              isRequired
              options={duplicates.map((d) => ({ id: d.claimId, label: d.claimNumber }))}
              value={decision.linkedClaimId}
              onChange={(linkedClaimId) => {
                setDecision((current) => ({ ...current, linkedClaimId }));
              }}
            />
          ) : null}
          <Select
            id="fnol-duplicate"
            label={t('fnol.duplicate.reason')}
            isRequired
            helperText={t('fnol.duplicate.reasonIllustrative')}
            options={duplicateReasons.map((r) => ({
              id: r,
              label: t(`codes.duplicateReason.${r}`),
            }))}
            value={decision.reasonCode}
            onChange={(reasonCode) => {
              setDecision((current) => ({ ...current, reasonCode }));
            }}
            errorMessage={shown['fnol-duplicate']}
          />
        </Section>
      ) : null}

      {check.isError ? (
        <ProblemBanner error={check.error} title={t('fnol.validation.error')} />
      ) : null}
      {submit.isError ? (
        <ProblemBanner error={submit.error} title={t('fnol.submitFailed')} />
      ) : null}

      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={submit.isPending}
          isDisabled={busy}
          onPress={() => void onSubmit()}
        >
          {t('fnol.submit')}
        </Button>
        <Button
          variant="secondary"
          isLoading={check.isPending}
          isDisabled={busy}
          onPress={() => void onValidate()}
        >
          {t('fnol.validate')}
        </Button>
      </div>
    </div>
  );
}
