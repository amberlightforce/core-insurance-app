import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';

import {
  EmptyState,
  StatusPill,
  dateColumn,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { documentSemantic, useSubjectDocuments, type DocumentView } from './api';

/** Documents about one object (doc.Document.listForObject). */
export function SubjectDocumentsPage() {
  const { t } = useTranslation('documents');
  const navigate = useNavigate();
  const { module = '', type = '', id = '' } = useParams();
  const objectRef = `${module}/${type}/${id}`;
  const query = useSubjectDocuments(objectRef);

  const columns = useMemo<DataColumn<DocumentView>[]>(
    () => [
      identifierColumn<DocumentView>('number', t('columns.number'), (d) => d.documentNumber),
      textColumn<DocumentView>('type', t('columns.type'), (d) => d.documentType),
      statusColumn<DocumentView>(
        'status',
        t('columns.status'),
        (d) => d.status,
        (d) => (
          <StatusPill
            semantic={documentSemantic(d.status)}
            subLabel={d.status}
            announceChanges={false}
          />
        ),
      ),
      textColumn<DocumentView>('medium', t('columns.medium'), (d) => d.medium),
      textColumn<DocumentView>('language', t('columns.language'), (d) => d.language.toUpperCase()),
      dateColumn<DocumentView>('created', t('columns.created'), (d) => d.createdAt),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        title={t('subject.title')}
        overline={t('overline')}
        recordId={objectRef}
        actions={
          <LinkButton variant="secondary" to="/documents">
            {t('another')}
          </LinkButton>
        }
      />
      <Section title={t('subject.documents')} count={query.data?.items.length}>
        <QueryView query={query} notFoundMessage={t('subject.notFound')}>
          {(page) => (
            <SimpleTable<DocumentView>
              aria-label={t('subject.documents')}
              columns={columns}
              data={page.items}
              getRowId={(d) => d.documentId}
              getRowLabel={(d) => d.documentNumber}
              onOpen={(d) => {
                void navigate(`/documents/${d.documentId}`);
              }}
              emptyState={
                <EmptyState
                  kind="first-use"
                  illustration={<></>}
                  headingLevel={3}
                  headline={t('subject.emptyTitle')}
                  description={t('subject.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
