import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Select, TextField } from '../../../design-system';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import staff from '../../staff/staff.module.css';
import { sourceTypes, useFiscalBreaks, useFiscalDocuments } from './api';
import styles from './Fiscal.module.css';
import { BulkResubmitDialog } from './BulkResubmitDialog';
import { FiscalDocumentsTable } from './FiscalDocumentsTable';

const ALL = 'ALL';

/** Fiscal documents of the legal entity: registered, pending and rejected, plus the reconciliation breaks. */
export function FiscalListPage() {
  const { t } = useTranslation('compliance');
  const [sourceType, setSourceType] = useState(ALL);
  const [sourceId, setSourceId] = useState('');
  const documents = useFiscalDocuments({
    sourceType: sourceType === ALL ? '' : sourceType,
    sourceId: sourceId.trim(),
  });
  const breaks = useFiscalBreaks();
  const [bulk, setBulk] = useState(false);

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('fiscal.overline')}
        title={t('fiscal.title')}
        description={t('fiscal.lead')}
      />
      <Section
        title={t('fiscal.breaks.title')}
        family="danger"
        {...(breaks.data ? { count: breaks.data.length } : {})}
        actions={
          <Button
            variant="secondary"
            isDisabled={!breaks.data?.some((b) => b.document.status === 'REJECTED')}
            onPress={() => {
              setBulk(true);
            }}
          >
            {t('fiscal.bulk.open')}
          </Button>
        }
      >
        <p className={staff.muted}>{t('fiscal.breaks.body')}</p>
        <QueryView query={breaks}>
          {(rows) => (
            <FiscalDocumentsTable
              rows={rows}
              label={t('fiscal.breaks.title')}
              empty={t('fiscal.breaks.empty')}
              showRejections
            />
          )}
        </QueryView>
      </Section>
      <Section
        title={t('fiscal.list.title')}
        family="brand"
        {...(documents.data ? { count: documents.data.length } : {})}
      >
        <div className={styles.filters}>
          <Select
            label={t('fiscal.filter.sourceType')}
            options={[
              { id: ALL, label: t('fiscal.filter.all') },
              ...sourceTypes.map((s) => ({ id: s, label: s })),
            ]}
            value={sourceType}
            onChange={(value) => {
              if (value) setSourceType(value);
            }}
          />
          <TextField label={t('fiscal.filter.sourceId')} value={sourceId} onChange={setSourceId} />
        </div>
        <QueryView query={documents}>
          {(rows) => (
            <FiscalDocumentsTable
              rows={rows}
              label={t('fiscal.list.title')}
              empty={t('fiscal.list.empty')}
            />
          )}
        </QueryView>
      </Section>
      {bulk && breaks.data ? (
        <BulkResubmitDialog
          rows={breaks.data}
          onClose={() => {
            setBulk(false);
          }}
        />
      ) : null}
    </div>
  );
}
