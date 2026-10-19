import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Button, TextField, toast } from '../../../design-system';
import { QueryView } from '../../staff/QueryView';
import { useFormat } from '../../staff/useFormat';
import { useDisclosures, useRecordDisclosure, type DisclosureEntry } from './api';
import styles from './Dsar.module.css';
import { ProblemBanner } from './ProblemBanner';

/** The disclosure log of the data subject of a case (Art 15(1)(c) recipients), with a form to record a disclosure. */
export function DisclosurePanel({ dsarId, partyId }: { dsarId: string; partyId: string }) {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const query = useDisclosures(partyId);
  const record = useRecordDisclosure();
  const [recipient, setRecipient] = useState('');
  const [purpose, setPurpose] = useState('');
  const [legalBasis, setLegalBasis] = useState('');
  const [categories, setCategories] = useState('');
  const [tried, setTried] = useState(false);

  const submit = () => {
    setTried(true);
    if (!recipient.trim() || !purpose.trim() || !legalBasis.trim()) return;
    const input = {
      recipient: recipient.trim(),
      purpose: purpose.trim(),
      legalBasis: legalBasis.trim(),
      categories: categories
        .split(',')
        .map((c) => c.trim())
        .filter(Boolean),
      dsarId,
      partyId,
    };
    record.mutate(
      { input, key: keyFor(input) },
      {
        onSuccess: () => {
          release();
          setRecipient('');
          setPurpose('');
          setLegalBasis('');
          setCategories('');
          setTried(false);
          toast.success({ title: t('dsar.disclosure.recorded'), description: input.recipient });
        },
      },
    );
  };
  const required = (value: string) =>
    tried && !value.trim() ? t('dsar.disclosure.required') : undefined;

  return (
    <div className={styles.stack}>
      <QueryView query={query}>
        {(page) => {
          const entries: DisclosureEntry[] = page.items.flatMap((i) => i.entries);
          return entries.length === 0 ? (
            <p className="ds-caption">{t('dsar.disclosure.none')}</p>
          ) : (
            <div className={styles.scroll}>
              <table className={styles.grid} aria-label={t('dsar.disclosure.title')}>
                <thead>
                  <tr>
                    <th scope="col">{t('dsar.disclosure.recipient')}</th>
                    <th scope="col">{t('dsar.disclosure.purpose')}</th>
                    <th scope="col">{t('dsar.disclosure.legalBasis')}</th>
                    <th scope="col">{t('dsar.disclosure.categories')}</th>
                    <th scope="col">{t('dsar.disclosure.recordedAt')}</th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((e) => (
                    <tr key={e.entryId}>
                      <td>{e.recipient}</td>
                      <td>{e.purpose}</td>
                      <td>{e.legalBasis}</td>
                      <td>{e.categories.join(', ') || '—'}</td>
                      <td>{fmt.dateTime(e.recordedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          );
        }}
      </QueryView>
      <form
        className={styles.inline}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <TextField
          label={t('dsar.disclosure.recipient')}
          value={recipient}
          onChange={setRecipient}
          errorMessage={required(recipient)}
        />
        <TextField
          label={t('dsar.disclosure.purpose')}
          value={purpose}
          onChange={setPurpose}
          errorMessage={required(purpose)}
        />
        <TextField
          label={t('dsar.disclosure.legalBasis')}
          value={legalBasis}
          onChange={setLegalBasis}
          errorMessage={required(legalBasis)}
        />
        <TextField
          label={t('dsar.disclosure.categories')}
          value={categories}
          onChange={setCategories}
          helperText={t('dsar.disclosure.categoriesHelp')}
        />
        <Button type="submit" variant="secondary" isLoading={record.isPending}>
          {t('dsar.disclosure.record')}
        </Button>
      </form>
      {record.isError ? <ProblemBanner error={record.error} /> : null}
    </div>
  );
}
