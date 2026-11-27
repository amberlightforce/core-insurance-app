import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  Banner,
  EmptyState,
  TextField,
  dateColumn,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useMandates, type MigratedMandate } from './api';

/** SEPA mandates: read-only list of the mandates the migration imported (IBAN masked). */
export function MandatesPage() {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const query = useMandates(search.trim());

  const columns = useMemo<DataColumn<MigratedMandate>[]>(
    () => [
      identifierColumn<MigratedMandate>(
        'ref',
        t('mandates.columns.reference'),
        (r) => r.mandateReference,
      ),
      textColumn<MigratedMandate>('iban', t('mandates.columns.iban'), (r) => r.ibanMasked ?? '—'),
      dateColumn<MigratedMandate>('signed', t('mandates.columns.signed'), (r) => r.signatureDate),
      textColumn<MigratedMandate>('status', t('mandates.columns.status'), (r) =>
        t(`mandates.status.${r.status}`, { defaultValue: r.status }),
      ),
      textColumn<MigratedMandate>('origin', t('mandates.columns.origin'), (r) =>
        t(`mandates.origin.${r.origin}`, { defaultValue: r.origin }),
      ),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('mandates.title')}
        description={t('mandates.subtitle')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <Banner variant="info" live="none" title={t('mandates.scopeNote')} />
      <Section title={t('mandates.listTitle')} family="plum" count={query.data?.items.length}>
        <div className={styles.grid}>
          <TextField label={t('mandates.search')} value={search} onChange={setSearch} />
        </div>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<MigratedMandate>
              aria-label={t('mandates.listTitle')}
              columns={columns}
              data={page.items}
              getRowId={(r) => r.mandateId}
              onOpen={(r) => {
                void navigate(`/billing/accounts/${r.billingAccountId}`);
              }}
              emptyState={
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('mandates.emptyTitle')}
                  description={t('mandates.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
