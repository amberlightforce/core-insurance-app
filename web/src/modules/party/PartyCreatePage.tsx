import { parseDate, today } from '@internationalized/date';
import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import type { PartyCreateRequest, PartyCreateResponse } from '../../api/types';
import {
  Banner,
  Button,
  DatePicker,
  ErrorSummary,
  FormGrid,
  FormGridItem,
  FormSection,
  IdentifierField,
  TextField,
  announce,
  type ErrorSummaryItem,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import styles from '../staff/staff.module.css';
import { createParty } from './api';
import { emptyPerson, toCreateRequest, type PersonFormValues } from './createRequest';
import { fieldErrors, personSchema } from './personSchema';

const idOf = (name: keyof PersonFormValues) => `person-${name}`;

/**
 * Create a person (W2-PTY slice). Greek-first: names, ΑΦΜ (mod-11 checked on the client with the rule the API
 * applies), birth date, one Greek legal/mailing address and the contact points. The Idempotency-Key is one
 * UUID per submission and is reused when the same payload is retried.
 */
export function PartyCreatePage() {
  const { t } = useTranslation('party');
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const [values, setValues] = useState<PersonFormValues>(emptyPerson);
  const [attempts, setAttempts] = useState(0);
  const [created, setCreated] = useState<PartyCreateResponse | null>(null);
  const todayDate = today('Europe/Athens');
  const todayIso = todayDate.toString();

  const schema = useMemo(
    () =>
      personSchema(
        {
          required: t('create.errors.required'),
          birthFuture: t('create.errors.birthFuture'),
          afm: t('create.errors.afm'),
          postcode: t('create.errors.postcode'),
          email: t('create.errors.email'),
          contact: t('create.errors.contact'),
        },
        todayIso,
      ),
    [t, todayIso],
  );
  // After the first submit attempt the form re-validates on every change.
  const parsed = schema.safeParse(values);
  const errors: Record<string, string> =
    attempts > 0 && !parsed.success ? fieldErrors(parsed.error) : {};
  const summary: ErrorSummaryItem[] = Object.entries(errors).map(([name, message]) => ({
    fieldId: idOf(name as keyof PersonFormValues),
    message,
  }));

  const mutation = useMutation({
    mutationFn: (request: PartyCreateRequest) => createParty(request, keyFor(request)),
    onSuccess: (response) => {
      release();
      announce(t('create.created', { number: response.party.partyNumber }));
      if (response.duplicateSuggestions.length === 0) {
        void navigate(`/parties/${response.party.partyId}`);
      } else {
        setCreated(response);
      }
    },
  });

  const bind = (name: keyof PersonFormValues) => ({
    id: idOf(name),
    value: values[name],
    onChange: (value: string) => {
      setValues((v) => ({ ...v, [name]: value }));
    },
    errorMessage: errors[name],
  });

  if (created) {
    return (
      <div className={styles.page}>
        <PageHeader title={t('create.title')} overline={t('overline')} />
        <Banner
          variant="success"
          title={t('create.created', { number: created.party.partyNumber })}
          actions={
            <Button
              variant="primary"
              onPress={() => {
                void navigate(`/parties/${created.party.partyId}`);
              }}
            >
              {t('create.openNew')}
            </Button>
          }
        />
        <Banner
          variant="warning"
          title={t('create.duplicates', { count: created.duplicateSuggestions.length })}
        >
          <ul className={styles.problemList}>
            {created.duplicateSuggestions.map((d) => (
              <li key={d.partyId}>
                <LinkButton
                  to={`/parties/${d.partyId}`}
                >{`${d.partyNumber} · ${d.displayName}`}</LinkButton>
              </li>
            ))}
          </ul>
        </Banner>
      </div>
    );
  }

  return (
    <div className={styles.page}>
      <PageHeader
        title={t('create.title')}
        overline={t('overline')}
        subtitle={t('create.subtitle')}
      />
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          setAttempts((n) => n + 1);
          if (parsed.success) mutation.mutate(toCreateRequest(values));
        }}
        className={styles.stack}
        aria-label={t('create.title')}
      >
        <ErrorSummary errors={summary} focusKey={attempts} />
        {mutation.isError ? (
          <ProblemBanner error={mutation.error} title={t('create.failed')} />
        ) : null}
        <FormSection title={t('create.sections.person')}>
          <FormGrid>
            <FormGridItem width="md">
              <TextField
                {...bind('givenNames')}
                label={t('create.fields.givenNames')}
                isRequired
                autoComplete="off"
              />
            </FormGridItem>
            <FormGridItem width="md">
              <TextField
                {...bind('familyName')}
                label={t('create.fields.familyName')}
                isRequired
                autoComplete="off"
              />
            </FormGridItem>
            <FormGridItem width="md">
              <TextField
                {...bind('fatherName')}
                label={t('create.fields.fatherName')}
                autoComplete="off"
              />
            </FormGridItem>
            <FormGridItem width="sm" newRow>
              <DatePicker
                label={t('create.fields.birthDate')}
                isRequired
                maxValue={todayDate}
                value={values.birthDate ? parseDate(values.birthDate) : null}
                onChange={(value) => {
                  setValues((v) => ({ ...v, birthDate: value ? value.toString() : '' }));
                }}
                {...(errors.birthDate ? { errorMessage: errors.birthDate } : {})}
              />
            </FormGridItem>
            <FormGridItem width="sm">
              <IdentifierField
                kind="afm"
                {...bind('afm')}
                label={t('create.fields.afm')}
                helperText={t('create.fields.afmHelp')}
              />
            </FormGridItem>
          </FormGrid>
        </FormSection>
        <FormSection
          title={t('create.sections.address')}
          description={t('create.sections.addressHelp')}
        >
          <FormGrid>
            <FormGridItem width="lg">
              <TextField
                {...bind('street')}
                label={t('create.fields.street')}
                isRequired
                autoComplete="off"
              />
            </FormGridItem>
            <FormGridItem width="xs">
              <TextField {...bind('number')} label={t('create.fields.number')} autoComplete="off" />
            </FormGridItem>
            <FormGridItem width="sm" newRow>
              <TextField
                {...bind('postcode')}
                label={t('create.fields.postcode')}
                isRequired
                inputMode="numeric"
                autoComplete="off"
              />
            </FormGridItem>
            <FormGridItem width="md">
              <TextField
                {...bind('locality')}
                label={t('create.fields.locality')}
                isRequired
                autoComplete="off"
              />
            </FormGridItem>
          </FormGrid>
        </FormSection>
        <FormSection
          title={t('create.sections.contacts')}
          description={t('create.sections.contactsHelp')}
        >
          <FormGrid>
            <FormGridItem width="md">
              <TextField
                {...bind('email')}
                type="email"
                label={t('create.fields.email')}
                autoComplete="off"
              />
            </FormGridItem>
            <FormGridItem width="md">
              <TextField
                {...bind('mobile')}
                type="tel"
                label={t('create.fields.mobile')}
                autoComplete="off"
              />
            </FormGridItem>
          </FormGrid>
        </FormSection>
        <div className={styles.actions}>
          <Button type="submit" variant="primary" isLoading={mutation.isPending}>
            {t('create.submit')}
          </Button>
          <Button
            variant="secondary"
            onPress={() => {
              void navigate('/parties');
            }}
          >
            {t('create.cancel')}
          </Button>
        </div>
      </form>
    </div>
  );
}
