import { parseDate, today } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  CurrencyField,
  DatePicker,
  ErrorSummary,
  Select,
  TextField,
  announce,
  type ErrorSummaryItem,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useSubmitManualJournal } from './api';
import { lineTotals } from './journalMath';
import type { ManualJournalSubmitRequest } from './types';

interface DraftLine {
  key: number;
  account: string;
  side: 'DEBIT' | 'CREDIT';
  amount: string | null;
}

const currency = 'EUR';

/**
 * New manual journal (fin.ManualJournal.submit). The maker proposes balanced lines with an evidence reference; the
 * journal posts only when a second user approves it (four-eyes). Unbalanced or evidence-less input is stopped here
 * and again by the server (FIN-ERR-UNBALANCED, FIN-ERR-EVIDENCE-REQUIRED).
 */
export function ManualJournalFormPage() {
  const { t } = useTranslation('finance');
  const fmt = useFormat();
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const [description, setDescription] = useState('');
  const [book, setBook] = useState('');
  const [date, setDate] = useState(today('Europe/Athens').toString());
  const [evidence, setEvidence] = useState('');
  const [nextKey, setNextKey] = useState(3);
  const [lines, setLines] = useState<DraftLine[]>([
    { key: 1, account: '', side: 'DEBIT', amount: null },
    { key: 2, account: '', side: 'CREDIT', amount: null },
  ]);
  const [submitted, setSubmitted] = useState(0);

  const mutation = useSubmitManualJournal((response) => {
    release();
    announce(t('ops.manual.form.submitted'));
    void navigate(`/finance/manual-journals/${encodeURIComponent(response.manualJournal.id)}`);
  });

  const update = (key: number, patch: Partial<DraftLine>) => {
    setLines((current) => current.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  };
  const totals = lineTotals(lines.map((l) => ({ side: l.side, amount: l.amount ?? '' })));

  const errors: Record<string, string> = {};
  if (description.trim() === '') errors['mj-description'] = t('ops.manual.form.errors.description');
  if (book.trim() === '') errors['mj-book'] = t('ops.manual.form.errors.book');
  if (evidence.trim() === '') errors['mj-evidence'] = t('ops.manual.form.errors.evidence');
  lines.forEach((l, index) => {
    if (l.account.trim() === '')
      errors[`mj-account-${String(l.key)}`] = t('ops.manual.form.errors.account', { n: index + 1 });
    if (l.amount === null || !/^\d+(\.\d{1,2})?$/.test(l.amount) || Number(l.amount) <= 0)
      errors[`mj-amount-${String(l.key)}`] = t('ops.manual.form.errors.amount', { n: index + 1 });
  });
  if (!totals.balanced) errors['mj-balance'] = t('ops.manual.form.errors.unbalanced');
  const summary: ErrorSummaryItem[] = Object.entries(errors).map(([fieldId, message]) => ({
    fieldId,
    message,
  }));
  const shown = submitted > 0 ? errors : {};

  const submit = () => {
    setSubmitted((n) => n + 1);
    if (Object.keys(errors).length > 0) return;
    const request: ManualJournalSubmitRequest = {
      description: description.trim(),
      book: book.trim(),
      accountingDate: date,
      currency,
      lines: lines.map((l) => ({
        account: l.account.trim(),
        side: l.side,
        amount: l.amount ?? '0.00',
      })),
      evidence: { reference: evidence.trim() },
    };
    mutation.mutate({ request, key: keyFor(request) });
  };

  return (
    <div className={styles.page}>
      <PageHeader
        title={t('ops.manual.form.title')}
        overline={t('overline')}
        actions={
          <LinkButton variant="secondary" to="/finance/manual-journals">
            {t('ops.manual.backToList')}
          </LinkButton>
        }
      />
      <form
        noValidate
        className={styles.stack}
        aria-label={t('ops.manual.form.title')}
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <Banner variant="info" live="none" title={t('ops.manual.form.fourEyes')}>
          {t('ops.manual.form.fourEyesBody')}
        </Banner>
        <ErrorSummary
          errors={summary.length > 0 && submitted > 0 ? summary : []}
          focusKey={submitted}
        />
        <Section title={t('ops.manual.form.header')}>
          <div className={styles.grid}>
            <TextField
              id="mj-description"
              label={t('ops.manual.description')}
              isRequired
              maxLength={200}
              value={description}
              onChange={setDescription}
              errorMessage={shown['mj-description']}
            />
            <TextField
              id="mj-book"
              label={t('ops.manual.book')}
              isRequired
              mono
              maxLength={32}
              value={book}
              onChange={setBook}
              errorMessage={shown['mj-book']}
            />
            <DatePicker
              label={t('ops.manual.accountingDate')}
              isRequired
              value={parseDate(date)}
              onChange={(value) => {
                if (value) setDate(value.toString());
              }}
            />
            <TextField
              id="mj-evidence"
              label={t('ops.manual.evidence')}
              helperText={t('ops.manual.form.evidenceHelp')}
              isRequired
              maxLength={200}
              value={evidence}
              onChange={setEvidence}
              errorMessage={shown['mj-evidence']}
            />
          </div>
        </Section>
        <Section
          title={t('ops.manual.form.lines')}
          actions={
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                setLines((current) => [
                  ...current,
                  { key: nextKey, account: '', side: 'DEBIT', amount: null },
                ]);
                setNextKey((n) => n + 1);
              }}
            >
              {t('ops.manual.form.addLine')}
            </Button>
          }
        >
          <div className={styles.stack}>
            {lines.map((l, index) => {
              const amountError = shown[`mj-amount-${String(l.key)}`];
              return (
                <div
                  key={l.key}
                  className={styles.row}
                  role="group"
                  aria-label={t('ops.manual.form.lineN', { n: index + 1 })}
                >
                  <TextField
                    id={`mj-account-${String(l.key)}`}
                    label={t('ops.manual.form.account')}
                    isRequired
                    mono
                    maxLength={32}
                    value={l.account}
                    onChange={(value) => {
                      update(l.key, { account: value });
                    }}
                    errorMessage={shown[`mj-account-${String(l.key)}`]}
                  />
                  <Select
                    label={t('ops.manual.form.side')}
                    id={`mj-side-${String(l.key)}`}
                    options={[
                      { id: 'DEBIT', label: t('ops.manual.form.debit') },
                      { id: 'CREDIT', label: t('ops.manual.form.credit') },
                    ]}
                    value={l.side}
                    onChange={(value) => {
                      if (value === 'DEBIT' || value === 'CREDIT') update(l.key, { side: value });
                    }}
                  />
                  <CurrencyField
                    label={t('ops.manual.amount')}
                    isRequired
                    name={`mj-amount-${String(l.key)}`}
                    value={l.amount}
                    onChange={(value) => {
                      update(l.key, { amount: value });
                    }}
                    {...(amountError ? { errorMessage: amountError } : {})}
                  />
                  {lines.length > 2 ? (
                    <Button
                      variant="ghost"
                      size="sm"
                      onPress={() => {
                        setLines((current) => current.filter((x) => x.key !== l.key));
                      }}
                    >
                      {t('ops.manual.form.removeLine', { n: index + 1 })}
                    </Button>
                  ) : null}
                </div>
              );
            })}
          </div>
          <p id="mj-balance" className={styles.muted}>
            {t('ops.manual.form.totals', {
              debit: fmt.money({ amount: totals.debit, currency }),
              credit: fmt.money({ amount: totals.credit, currency }),
            })}
          </p>
          {totals.balanced ? (
            <Banner variant="success" live="none" title={t('ops.manual.form.balanced')} />
          ) : (
            <Banner variant="warning" live="none" title={t('ops.manual.form.unbalanced')} />
          )}
        </Section>
        {mutation.isError ? (
          <ProblemBanner error={mutation.error} title={t('ops.manual.form.failed')} />
        ) : null}
        <div className={styles.actions}>
          <Button type="submit" variant="primary" isLoading={mutation.isPending}>
            {t('ops.manual.form.submit')}
          </Button>
        </div>
      </form>
    </div>
  );
}
