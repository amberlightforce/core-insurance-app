import { today as calendarToday, parseDate } from '@internationalized/date';
import type { TFunction } from 'i18next';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';

import type {
  CatalogueCoverage,
  CatalogueGetResponse,
  ProductVersionResolveResponse,
  Question,
  QuestionSetEvaluateResponse,
} from '../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  CurrencyField,
  DatePicker,
  IdentifierField,
  KeyValueList,
  Radio,
  RadioGroup,
  Select,
  TextField,
} from '../../design-system';
import { useLocalised } from './useLocalised';
import { PartySearchPanel } from '../party/PartySearchPanel';
import { QueryView } from '../staff/QueryView';
import { Section } from '../staff/PageHeader';
import styles from '../staff/staff.module.css';
import { useParty } from '../party/api';
import {
  fuelTypes,
  knockOuts,
  questionState,
  validateDriver,
  validateVehicle,
  type Draft,
  type FieldIssue,
  type PartyRef,
  type VehicleForm,
} from './state';
import type { UseQueryResult } from '@tanstack/react-query';

type SetDraft = (update: (draft: Draft) => Draft) => void;

const thisYear = () => new Date().getFullYear();

function issueText(t: TFunction<'quote'>, issue: FieldIssue | undefined): string | undefined {
  return issue ? t(`issues.${issue}`) : undefined;
}

/* Step 1: policyholder, product, start date ------------------------------------------------------------- */

export function PolicyholderStep({
  draft,
  setDraft,
  product,
  initialPartyId,
}: {
  draft: Draft;
  setDraft: SetDraft;
  product: UseQueryResult<ProductVersionResolveResponse>;
  initialPartyId: string | null;
}) {
  const { t } = useTranslation('quote');
  const [changing, setChanging] = useState(false);
  const preselected = useParty(initialPartyId ?? '');
  const today = calendarToday('Europe/Athens');

  // A party opened from its view page arrives by id; its (masked) display name fills the summary.
  const preselect = initialPartyId !== null && draft.policyholder === null && !changing;
  const preselectedParty = preselected.data?.party;
  useEffect(() => {
    if (!preselect || !preselectedParty) return;
    const native =
      preselectedParty.names.find((n) => n.form === 'NATIVE') ?? preselectedParty.names[0];
    const label =
      native?.organisationName ??
      [native?.familyName, native?.givenNames].filter(Boolean).join(' ');
    setDraft((d) => ({
      ...d,
      policyholder: {
        partyId: preselectedParty.partyId,
        label,
        partyNumber: preselectedParty.partyNumber,
      },
    }));
  }, [preselect, preselectedParty, setDraft]);

  return (
    <div className={styles.stack}>
      <Section title={t('policyholder.party')} headingLevel={3}>
        {draft.policyholder ? (
          <div className={styles.row}>
            <KeyValueList
              aria-label={t('policyholder.party')}
              items={[
                { id: 'name', label: t('policyholder.name'), value: draft.policyholder.label },
                ...(draft.policyholder.partyNumber
                  ? [
                      {
                        id: 'number',
                        label: t('policyholder.number'),
                        value: draft.policyholder.partyNumber,
                        kind: 'mono' as const,
                      },
                    ]
                  : []),
              ]}
            />
            <Button
              variant="secondary"
              onPress={() => {
                setChanging(true);
                setDraft((d) => ({ ...d, policyholder: null }));
              }}
            >
              {t('policyholder.change')}
            </Button>
          </div>
        ) : preselect && preselected.isPending ? (
          <p className={styles.muted} role="status">
            {t('policyholder.loadingParty')}
          </p>
        ) : (
          <PartySearchPanel
            label={t('policyholder.search')}
            onOpen={(item) => {
              setDraft((d) => ({
                ...d,
                policyholder: {
                  partyId: item.partyId,
                  label: item.displayName,
                  partyNumber: item.partyNumber,
                },
              }));
            }}
          />
        )}
      </Section>
      <Section title={t('policyholder.productSection')} headingLevel={3}>
        <QueryView query={product}>
          {(resolved) => (
            <KeyValueList
              aria-label={t('policyholder.productSection')}
              items={[
                {
                  id: 'product',
                  label: t('policyholder.product'),
                  value: t('policyholder.productName'),
                },
                {
                  id: 'code',
                  label: t('policyholder.productCode'),
                  value: 'MOTOR-GR',
                  kind: 'mono',
                },
                { id: 'version', label: t('policyholder.version'), value: resolved.version },
                {
                  id: 'hash',
                  label: t('policyholder.hash'),
                  value: resolved.artefactHash.slice(0, 12),
                  kind: 'mono',
                },
              ]}
            />
          )}
        </QueryView>
      </Section>
      <Section title={t('policyholder.termSection')} headingLevel={3}>
        <DatePicker
          label={t('policyholder.startDate')}
          description={t('policyholder.startDateHelp')}
          isRequired
          minValue={today}
          value={safeDate(draft.startDate)}
          onChange={(value) => {
            if (value) setDraft((d) => ({ ...d, startDate: value.toString() }));
          }}
        />
        <p className={styles.muted}>{t('policyholder.plan')}</p>
      </Section>
    </div>
  );
}

function safeDate(value: string) {
  try {
    return value ? parseDate(value) : null;
  } catch {
    return null;
  }
}

/* Step 2: vehicle ------------------------------------------------------------------------------------- */

export function VehicleStep({ draft, setDraft }: { draft: Draft; setDraft: SetDraft }) {
  const { t } = useTranslation('quote');
  const [touched, setTouched] = useState<ReadonlySet<keyof VehicleForm>>(new Set());
  const issues = validateVehicle(draft.vehicle, thisYear());
  const set = (field: keyof VehicleForm) => (value: string | null) => {
    setDraft((d) => ({ ...d, vehicle: { ...d.vehicle, [field]: value ?? '' } }));
  };
  const touch = (field: keyof VehicleForm) => () => {
    setTouched((s) => new Set(s).add(field));
  };
  const err = (field: keyof VehicleForm) =>
    touched.has(field) ? issueText(t, issues[field]) : undefined;
  const v = draft.vehicle;
  return (
    <div className={styles.stack}>
      <Section title={t('vehicle.identity')} headingLevel={3}>
        <div className={styles.grid}>
          <IdentifierField
            kind="plate"
            label={t('vehicle.plate')}
            isRequired
            value={v.plate}
            onChange={set('plate')}
            onBlur={touch('plate')}
            errorMessage={err('plate')}
          />
          <IdentifierField
            kind="vin"
            label={t('vehicle.vin')}
            value={v.vin}
            onChange={set('vin')}
          />
          <TextField
            label={t('vehicle.make')}
            isRequired
            value={v.make}
            onChange={set('make')}
            onBlur={touch('make')}
            errorMessage={err('make')}
          />
          <TextField
            label={t('vehicle.model')}
            isRequired
            value={v.model}
            onChange={set('model')}
            onBlur={touch('model')}
            errorMessage={err('model')}
          />
        </div>
      </Section>
      <Section title={t('vehicle.technical')} headingLevel={3}>
        <div className={styles.grid}>
          <TextField
            label={t('vehicle.year')}
            isRequired
            inputMode="numeric"
            value={v.firstRegistrationYear}
            onChange={set('firstRegistrationYear')}
            onBlur={touch('firstRegistrationYear')}
            errorMessage={err('firstRegistrationYear')}
          />
          <TextField
            label={t('vehicle.engine')}
            isRequired
            inputMode="numeric"
            suffix="cc"
            value={v.engineCapacityCc}
            onChange={set('engineCapacityCc')}
            onBlur={touch('engineCapacityCc')}
            errorMessage={err('engineCapacityCc')}
          />
          <TextField
            label={t('vehicle.power')}
            inputMode="numeric"
            suffix="kW"
            value={v.powerKw}
            onChange={set('powerKw')}
            onBlur={touch('powerKw')}
            errorMessage={err('powerKw')}
          />
          <Select
            label={t('vehicle.fuel')}
            options={fuelTypes.map((f) => ({ id: f, label: t(`vehicle.fuelTypes.${f}`) }))}
            value={v.fuelType || null}
            onChange={set('fuelType')}
          />
          <CurrencyField
            label={t('vehicle.value')}
            value={v.value || null}
            onChange={(value) => {
              set('value')(value);
            }}
          />
          <TextField
            label={t('vehicle.garaging')}
            isRequired
            inputMode="numeric"
            value={v.garagingPostcode}
            onChange={set('garagingPostcode')}
            onBlur={touch('garagingPostcode')}
            errorMessage={err('garagingPostcode')}
          />
        </div>
      </Section>
    </div>
  );
}

/* Step 3: driver --------------------------------------------------------------------------------------- */

export function DriverStep({ draft, setDraft }: { draft: Draft; setDraft: SetDraft }) {
  const { t } = useTranslation('quote');
  const [touched, setTouched] = useState(false);
  const issues = validateDriver(draft.driver, thisYear());
  const driver = draft.driver;
  return (
    <div className={styles.stack}>
      <Section title={t('driver.who')} headingLevel={3}>
        <Checkbox
          isSelected={driver.sameAsPolicyholder}
          onChange={(selected) => {
            setDraft((d) => ({
              ...d,
              driver: {
                ...d.driver,
                sameAsPolicyholder: selected,
                party: selected ? null : d.driver.party,
              },
            }));
          }}
        >
          {t('driver.same', { name: draft.policyholder?.label ?? '' })}
        </Checkbox>
        {driver.sameAsPolicyholder ? null : driver.party ? (
          <div className={styles.row}>
            <KeyValueList
              aria-label={t('driver.who')}
              items={[{ id: 'driver', label: t('driver.name'), value: driver.party.label }]}
            />
            <Button
              variant="secondary"
              onPress={() => {
                setDraft((d) => ({ ...d, driver: { ...d.driver, party: null } }));
              }}
            >
              {t('policyholder.change')}
            </Button>
          </div>
        ) : (
          <PartySearchPanel
            label={t('driver.search')}
            onOpen={(item) => {
              const party: PartyRef = {
                partyId: item.partyId,
                label: item.displayName,
                partyNumber: item.partyNumber,
              };
              setDraft((d) => ({ ...d, driver: { ...d.driver, party } }));
            }}
          />
        )}
        <p className={styles.muted}>{t('driver.birthDateNote')}</p>
      </Section>
      <Section title={t('driver.licence')} headingLevel={3}>
        <TextField
          label={t('driver.yearFirstLicensed')}
          isRequired
          inputMode="numeric"
          value={driver.yearFirstLicensed}
          onChange={(value) => {
            setDraft((d) => ({ ...d, driver: { ...d.driver, yearFirstLicensed: value } }));
          }}
          onBlur={() => {
            setTouched(true);
          }}
          errorMessage={touched ? issueText(t, issues.yearFirstLicensed) : undefined}
        />
      </Section>
    </div>
  );
}

/* Step 4: covers --------------------------------------------------------------------------------------- */

function CoverCard({
  cover,
  draft,
  setDraft,
}: {
  cover: CatalogueCoverage;
  draft: Draft;
  setDraft: SetDraft;
}) {
  const { t } = useTranslation('quote');
  const localised = useLocalised();
  const required = cover.existence === 'REQUIRED';
  const form = draft.covers[cover.code];
  const selected = required || form?.selected === true;
  const setTerm = (term: string, value: string | null) => {
    setDraft((d) => ({
      ...d,
      covers: {
        ...d.covers,
        [cover.code]: {
          selected: d.covers[cover.code]?.selected ?? false,
          terms: { ...d.covers[cover.code]?.terms, [term]: value ?? '' },
        },
      },
    }));
  };
  return (
    <Section title={localised(cover.name)} headingLevel={3}>
      <Checkbox
        isSelected={selected}
        isDisabled={required}
        description={
          required
            ? t('covers.required')
            : cover.existence === 'SUGGESTED'
              ? t('covers.suggested')
              : t('covers.optional')
        }
        onChange={(isSelected) => {
          setDraft((d) => ({
            ...d,
            covers: {
              ...d.covers,
              [cover.code]: { selected: isSelected, terms: d.covers[cover.code]?.terms ?? {} },
            },
          }));
        }}
      >
        {t('covers.include', { cover: localised(cover.name) })}
      </Checkbox>
      {selected
        ? cover.terms.map((term) => {
            const label = localised(term.name);
            if (term.kind === 'OPTION_LIST' && term.options) {
              const only = term.options.length === 1 ? term.options[0] : undefined;
              return (
                <Select
                  key={term.code}
                  label={label}
                  isRequired={term.required}
                  options={term.options.map((o) => ({
                    id: o.code,
                    label:
                      localised(o.name) + (o.illustrative ? ` (${t('covers.illustrative')})` : ''),
                  }))}
                  value={form?.terms[term.code] ?? only?.code ?? null}
                  onChange={(value) => {
                    setTerm(term.code, value);
                  }}
                  {...(only ? { isReadOnly: true } : {})}
                />
              );
            }
            return (
              <CurrencyField
                key={term.code}
                label={label}
                isRequired={term.required}
                value={form?.terms[term.code] ?? null}
                onChange={(value) => {
                  setTerm(term.code, value);
                }}
              />
            );
          })
        : null}
    </Section>
  );
}

export function CoversStep({
  draft,
  setDraft,
  catalogue,
}: {
  draft: Draft;
  setDraft: SetDraft;
  catalogue: UseQueryResult<CatalogueGetResponse>;
}) {
  return (
    <QueryView query={catalogue}>
      {(data) => (
        <div className={styles.stack}>
          {(data.coverages ?? []).map((cover) => (
            <CoverCard key={cover.code} cover={cover} draft={draft} setDraft={setDraft} />
          ))}
        </div>
      )}
    </QueryView>
  );
}

/* Step 5: question set -------------------------------------------------------------------------------- */

function QuestionField({
  question,
  draft,
  setDraft,
}: {
  question: Question;
  draft: Draft;
  setDraft: SetDraft;
}) {
  const { t } = useTranslation('quote');
  const localised = useLocalised();
  const value = draft.answers[question.code] ?? '';
  const { required } = questionState(question, draft.answers);
  const set = (next: string) => {
    setDraft((d) => ({ ...d, answers: { ...d.answers, [question.code]: next } }));
  };
  const label = localised(question.text);
  if (question.answerType === 'CHOICE' && question.answers) {
    return (
      <RadioGroup label={label} value={value || null} onChange={set} isRequired={required}>
        {question.answers.map((a) => (
          <Radio
            key={a.code}
            value={a.code}
            {...(a.outcome === 'KNOCK_OUT'
              ? { description: t('questions.knockOutAnswer') }
              : a.outcome === 'REFERRAL'
                ? { description: t('questions.referralAnswer') }
                : {})}
          >
            {localised(a.label)}
          </Radio>
        ))}
      </RadioGroup>
    );
  }
  return (
    <TextField
      label={label}
      isRequired={required}
      value={value}
      onChange={set}
      inputMode={
        question.answerType === 'INTEGER' || question.answerType === 'DECIMAL' ? 'numeric' : 'text'
      }
    />
  );
}

export function QuestionsStep({
  draft,
  setDraft,
  questionSet,
  evaluation,
}: {
  draft: Draft;
  setDraft: SetDraft;
  questionSet: UseQueryResult<{ questionSet: { questions: Question[] } }>;
  evaluation: QuestionSetEvaluateResponse | undefined;
}) {
  const { t } = useTranslation('quote');
  return (
    <QueryView query={questionSet}>
      {(data) => {
        const questions = [...data.questionSet.questions].sort(
          (a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0),
        );
        const blocked = knockOuts(questions, draft.answers);
        const referred = evaluation?.referrals ?? [];
        return (
          <div className={styles.stack}>
            {blocked.length > 0 ? (
              <Banner variant="danger" live="alert" title={t('questions.knockOutTitle')}>
                {t('questions.knockOutBody')}
              </Banner>
            ) : null}
            {blocked.length === 0 && referred.length > 0 ? (
              <Banner variant="warning" live="status" title={t('questions.referralTitle')}>
                {t('questions.referralBody')}
              </Banner>
            ) : null}
            {questions
              .filter((q) => questionState(q, draft.answers).visible)
              .map((q) => (
                <QuestionField key={q.code} question={q} draft={draft} setDraft={setDraft} />
              ))}
          </div>
        );
      }}
    </QueryView>
  );
}
