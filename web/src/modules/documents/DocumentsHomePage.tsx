import { parseDate } from '@internationalized/date';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  Button,
  DatePicker,
  EmptyState,
  Select,
  Switch,
  TextField,
  dateColumn,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { useDevSession } from '../../dev-auth/devAuth';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { CompletenessPanel } from './CompletenessPanel';
import { DocumentMarkers, DocumentStatusPill } from './DocumentMarkers';
import { ObjectLookup } from './ObjectLookup';
import {
  documentMedia,
  documentStatuses,
  maskId,
  subjectModules,
  useDocumentSearch,
  useDocumentTypes,
  type DocumentSearchFilters,
  type DocumentView,
} from './api';
import local from './documents.module.css';

const ALL = 'ALL';
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

interface Draft {
  q: string;
  type: string;
  status: string;
  medium: string;
  from: string;
  to: string;
  module: string;
  subjectType: string;
  subjectId: string;
  recipient: string;
}

const emptyDraft: Draft = {
  q: '',
  type: ALL,
  status: ALL,
  medium: ALL,
  from: '',
  to: '',
  module: ALL,
  subjectType: '',
  subjectId: '',
  recipient: '',
};

function toFilters(d: Draft, includeHidden: boolean): DocumentSearchFilters {
  const f: DocumentSearchFilters = {};
  if (d.q.trim()) f.q = d.q.trim();
  if (d.type !== ALL) f.templateKey = d.type;
  if (d.status !== ALL) f.status = d.status;
  if (d.medium !== ALL) f.medium = d.medium;
  if (d.from) f.createdFrom = d.from;
  if (d.to) f.createdTo = d.to;
  if (d.module !== ALL) f.subjectModule = d.module;
  if (d.subjectType.trim()) f.subjectType = d.subjectType.trim();
  if (d.subjectId.trim()) f.subjectId = d.subjectId.trim();
  if (d.recipient.trim()) f.recipientPartyId = d.recipient.trim();
  if (includeHidden) f.includeHidden = true;
  return f;
}

function dateValue(iso: string) {
  try {
    return iso ? parseDate(iso) : null;
  } catch {
    return null;
  }
}

/**
 * Document library (BO-DOC-12): every document of the legal entity, newest first, filterable, with keyset
 * «load more». A row opens the document. The by-object lookup of NX-UI-14a stays below the table.
 */
export function DocumentsHomePage() {
  const { t } = useTranslation('documents');
  const navigate = useNavigate();
  const roles = useDevSession()?.user.roles ?? [];
  const isAdmin = roles.includes('Platform.Admin');
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [applied, setApplied] = useState<Draft>(emptyDraft);
  const [showHidden, setShowHidden] = useState(false);
  const [tried, setTried] = useState(false);
  // Keyset paging: pages already loaded, plus the cursor of the page being read.
  const [previous, setPrevious] = useState<DocumentView[]>([]);
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const filters = useMemo(() => toFilters(applied, showHidden), [applied, showHidden]);
  const query = useDocumentSearch(filters, cursor);
  const types = useDocumentTypes();

  const recipientValid = draft.recipient.trim() === '' || uuid.test(draft.recipient.trim());
  const rows = useMemo(() => [...previous, ...(query.data?.items ?? [])], [previous, query.data]);
  const nextCursor = query.data?.nextCursor ?? null;

  const resetPaging = () => {
    setPrevious([]);
    setCursor(undefined);
  };

  const typeOptions = useMemo(
    () => [
      { id: ALL, label: t('filters.all') },
      ...(types.data?.items ?? []).map((x) => ({ id: x.documentType, label: x.documentType })),
    ],
    [types.data, t],
  );
  const statusOptions = useMemo(
    () => [
      { id: ALL, label: t('filters.all') },
      ...documentStatuses.map((s) => ({ id: s, label: t(`statuses.${s}`) })),
    ],
    [t],
  );
  const mediumOptions = useMemo(
    () => [
      { id: ALL, label: t('filters.all') },
      ...documentMedia.map((m) => ({ id: m, label: t(`media.${m}`) })),
    ],
    [t],
  );
  const moduleOptions = useMemo(
    () => [
      { id: ALL, label: t('filters.all') },
      ...subjectModules.map((m) => ({ id: m, label: t(`modules.${m}`) })),
    ],
    [t],
  );

  const columns = useMemo<DataColumn<DocumentView>[]>(
    () => [
      identifierColumn<DocumentView>('number', t('columns.number'), (d) => d.documentNumber),
      textColumn<DocumentView>('type', t('columns.type'), (d) => d.documentType),
      textColumn<DocumentView>('recipient', t('columns.recipient'), (d) =>
        maskId(d.recipientPartyId),
      ),
      statusColumn<DocumentView>(
        'status',
        t('columns.status'),
        (d) => d.status,
        (d) => (
          <span className={local.markers}>
            <DocumentStatusPill status={d.status} />
            <DocumentMarkers document={d} bare />
          </span>
        ),
      ),
      textColumn<DocumentView>('medium', t('columns.medium'), (d) =>
        t(`media.${d.medium}`, { defaultValue: d.medium }),
      ),
      dateColumn<DocumentView>('created', t('columns.created'), (d) => d.createdAt),
    ],
    [t],
  );

  // While a further page loads, the rows already read stay on screen.
  const view = previous.length > 0 ? { ...query, isPending: false } : query;

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('home.title')}
        overline={t('overline')}
        description={t('home.lead')}
      />
      <Section
        family="brand"
        title={t('library.title')}
        count={rows.length > 0 ? rows.length : undefined}
      >
        <form
          noValidate
          aria-label={t('filters.title')}
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (!recipientValid) return;
            resetPaging();
            setApplied(draft);
          }}
        >
          <div className={local.filters}>
            <TextField
              label={t('filters.q')}
              helperText={t('filters.qHelp')}
              value={draft.q}
              onChange={(v) => {
                setDraft((d) => ({ ...d, q: v }));
              }}
            />
            <Select
              label={t('filters.type')}
              options={typeOptions}
              value={draft.type}
              onChange={(v) => {
                if (v) setDraft((d) => ({ ...d, type: v }));
              }}
            />
            <Select
              label={t('filters.status')}
              options={statusOptions}
              value={draft.status}
              onChange={(v) => {
                if (v) setDraft((d) => ({ ...d, status: v }));
              }}
            />
            <Select
              label={t('filters.medium')}
              options={mediumOptions}
              value={draft.medium}
              onChange={(v) => {
                if (v) setDraft((d) => ({ ...d, medium: v }));
              }}
            />
            <DatePicker
              label={t('filters.from')}
              value={dateValue(draft.from)}
              onChange={(v) => {
                setDraft((d) => ({ ...d, from: v ? v.toString() : '' }));
              }}
            />
            <DatePicker
              label={t('filters.to')}
              value={dateValue(draft.to)}
              onChange={(v) => {
                setDraft((d) => ({ ...d, to: v ? v.toString() : '' }));
              }}
            />
            <Select
              label={t('filters.module')}
              options={moduleOptions}
              value={draft.module}
              onChange={(v) => {
                if (v) setDraft((d) => ({ ...d, module: v }));
              }}
            />
            <TextField
              label={t('filters.subjectType')}
              value={draft.subjectType}
              onChange={(v) => {
                setDraft((d) => ({ ...d, subjectType: v }));
              }}
            />
            <TextField
              label={t('filters.subjectId')}
              mono
              value={draft.subjectId}
              onChange={(v) => {
                setDraft((d) => ({ ...d, subjectId: v }));
              }}
            />
            <TextField
              label={t('filters.recipient')}
              mono
              value={draft.recipient}
              onChange={(v) => {
                setDraft((d) => ({ ...d, recipient: v }));
              }}
              errorMessage={tried && !recipientValid ? t('filters.recipientInvalid') : undefined}
            />
          </div>
          <div className={local.filterActions}>
            <Switch
              isSelected={showHidden}
              onChange={(v) => {
                resetPaging();
                setShowHidden(v);
              }}
            >
              {t('filters.showHidden')}
            </Switch>
            <div className={styles.row}>
              <Button
                variant="secondary"
                onPress={() => {
                  setDraft(emptyDraft);
                  setApplied(emptyDraft);
                  setTried(false);
                  resetPaging();
                }}
              >
                {t('filters.reset')}
              </Button>
              <Button type="submit" variant="primary">
                {t('filters.apply')}
              </Button>
            </div>
          </div>
        </form>
        {previous.length > 0 && query.isError ? (
          <ProblemBanner error={query.error} title={t('library.loadFailed')} />
        ) : null}
        <QueryView query={{ ...view, isError: view.isError && previous.length === 0 }}>
          {() => (
            <>
              <SimpleTable<DocumentView>
                aria-label={t('library.title')}
                columns={columns}
                data={rows}
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
                    headline={t('library.emptyTitle')}
                    description={t('library.emptyBody')}
                  />
                }
              />
              {nextCursor ? (
                <div className={local.loadMore}>
                  <Button
                    variant="secondary"
                    isLoading={query.isFetching}
                    onPress={() => {
                      setPrevious(rows);
                      setCursor(nextCursor);
                    }}
                  >
                    {t('library.loadMore')}
                  </Button>
                </div>
              ) : null}
            </>
          )}
        </QueryView>
      </Section>
      {isAdmin ? <CompletenessPanel /> : null}
      <Section family="plum" title={t('lookup.title')}>
        <p className={styles.muted}>{t('lookup.lead')}</p>
        <ObjectLookup />
      </Section>
    </div>
  );
}
