import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Button,
  Select,
  Switch,
  TextField,
  identifierColumn,
  statusColumn,
  textColumn,
  toast,
  type DataColumn,
} from '../../../design-system';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { createDsar, requestTypes, useDsarList, type DsarCase, type RequestType } from './api';
import styles from './Dsar.module.css';
import { ProblemBanner } from './ProblemBanner';
import { CaseStatusPill } from './StatusPills';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** DSAR console home: open a data-subject request and see every case with its deadline. */
export function DsarListPage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const navigate = useNavigate();
  const client = useQueryClient();
  const { keyFor, release } = useIdempotencyKey();
  const query = useDsarList();
  const [subject, setSubject] = useState('');
  const [type, setType] = useState<RequestType>('ACCESS');
  const [legalHold, setLegalHold] = useState(false);
  const [notes, setNotes] = useState('');
  const [tried, setTried] = useState(false);

  const create = useMutation({
    mutationFn: ({ body, key }: { body: Parameters<typeof createDsar>[0]; key: string }) =>
      createDsar(body, key),
  });

  const submit = () => {
    setTried(true);
    if (!uuid.test(subject.trim())) return;
    const body = {
      subjectPartyId: subject.trim(),
      requestType: type,
      legalHold,
      ...(notes.trim() ? { notes: notes.trim() } : {}),
    };
    create.mutate(
      { body, key: keyFor(body) },
      {
        onSuccess: ({ request }) => {
          release();
          toast.success({ title: t('dsar.created'), description: request.dsarId });
          void client.invalidateQueries({ queryKey: ['cmp', 'dsar'] });
          void navigate(`/compliance/dsar/${request.dsarId}`);
        },
      },
    );
  };

  const columns = useMemo<DataColumn<DsarCase>[]>(
    () => [
      identifierColumn<DsarCase>('id', t('dsar.list.columns.id'), (c) => c.request.dsarId, {
        size: 280,
      }),
      textColumn<DsarCase>(
        'type',
        t('dsar.list.columns.type'),
        (c) => t(`dsar.type.${c.request.requestType}`),
        { size: 170 },
      ),
      identifierColumn<DsarCase>(
        'subject',
        t('dsar.list.columns.subject'),
        (c) => c.request.subjectPartyId,
        { size: 280 },
      ),
      statusColumn<DsarCase>(
        'status',
        t('dsar.list.columns.status'),
        (c) => c.request.status,
        (c) => <CaseStatusPill status={c.request.status} />,
        { size: 170 },
      ),
      textColumn<DsarCase>(
        'deadline',
        t('dsar.list.columns.deadline'),
        (c) => fmt.date(c.request.deadline),
        { size: 130 },
      ),
      textColumn<DsarCase>(
        'blockers',
        t('dsar.list.columns.blockers'),
        (c) => String(c.request.blockers?.length ?? 0),
        { size: 100 },
      ),
    ],
    [t, fmt],
  );

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('dsar.overline')}
        title={t('dsar.title')}
        description={t('dsar.lead')}
      />
      <Section title={t('dsar.new.title')}>
        <form
          className={styles.inline}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <TextField
            label={t('dsar.new.subject')}
            value={subject}
            onChange={setSubject}
            helperText={t('dsar.new.subjectHelp')}
            errorMessage={
              tried && !uuid.test(subject.trim()) ? t('dsar.new.subjectInvalid') : undefined
            }
          />
          <Select
            label={t('dsar.new.type')}
            options={requestTypes.map((r) => ({ id: r, label: t(`dsar.type.${r}`) }))}
            value={type}
            onChange={(value) => {
              if (value) setType(value as RequestType);
            }}
          />
          <TextField label={t('dsar.new.notes')} value={notes} onChange={setNotes} />
          <Switch isSelected={legalHold} onChange={setLegalHold}>
            {t('dsar.new.legalHold')}
          </Switch>
          <Button type="submit" variant="commit" isLoading={create.isPending}>
            {t('dsar.new.submit')}
          </Button>
        </form>
        {create.isError ? <ProblemBanner error={create.error} /> : null}
      </Section>
      <Section title={t('dsar.list.title')}>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<DsarCase>
              aria-label={t('dsar.list.title')}
              columns={columns}
              data={page.items}
              getRowId={(c) => c.request.dsarId}
              getRowLabel={(c) => c.request.dsarId}
              onOpen={(c) => {
                void navigate(`/compliance/dsar/${c.request.dsarId}`);
              }}
              emptyState={<p className="ds-caption">{t('dsar.list.empty')}</p>}
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
