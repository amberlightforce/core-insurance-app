import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  StatusPill,
  TextField,
  announce,
  identifierColumn,
  statusColumn,
  textColumn,
  toast,
  type DataColumn,
} from '../../../design-system';
import { ProblemBanner } from '../dsar/ProblemBanner';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import styles from '../fiscal/Fiscal.module.css';
import {
  actionsFor,
  featureIdPattern,
  useAiAction,
  useAiSystems,
  useRegisterAiSystem,
  type AiAction,
  type AiSystem,
} from './api';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const semantic: Record<AiSystem['status'], Semantic> = {
  DRAFT: 'disabled',
  IN_REVIEW: 'pending-approval',
  APPROVED: 'success',
  SUSPENDED: 'warning',
  RETIRED: 'disabled',
};

interface Pending {
  featureId: string;
  action: AiAction;
}

/** The AI-system register: what is registered, its AI Act class and review status, and the lifecycle moves. */
export function AiRegisterPage() {
  const { t } = useTranslation('compliance');
  const query = useAiSystems();
  const register = useRegisterAiSystem();
  const move = useAiAction();
  const registerKey = useIdempotencyKey();
  const moveKey = useIdempotencyKey();
  const [featureId, setFeatureId] = useState('');
  const [name, setName] = useState('');
  const [aiActClass, setAiActClass] = useState('');
  const [tried, setTried] = useState(false);
  const [pending, setPending] = useState<Pending | null>(null);
  const [reason, setReason] = useState('');

  const submitRegister = () => {
    setTried(true);
    if (!featureIdPattern.test(featureId.trim()) || name.trim() === '') return;
    const body = {
      featureId: featureId.trim(),
      name: name.trim(),
      ...(aiActClass.trim() ? { aiActClass: aiActClass.trim() } : {}),
    };
    register.mutate(
      { body, key: registerKey.keyFor(body) },
      {
        onSuccess: (created) => {
          registerKey.release();
          toast.success({ title: t('ai.registered'), description: created.featureId });
          setFeatureId('');
          setName('');
          setAiActClass('');
          setTried(false);
        },
      },
    );
  };

  const runMove = () => {
    if (!pending) return;
    const input = { ...pending, reason: reason.trim() };
    move.mutate(
      { ...input, key: moveKey.keyFor(input) },
      {
        onSuccess: (updated) => {
          moveKey.release();
          announce(t(`ai.done.${pending.action}`));
          toast.success({ title: t(`ai.done.${pending.action}`), description: updated.featureId });
          setPending(null);
          setReason('');
        },
      },
    );
  };

  const columns = useMemo<DataColumn<AiSystem>[]>(
    () => [
      identifierColumn<AiSystem>('id', t('ai.columns.id'), (a) => a.featureId, { size: 150 }),
      textColumn<AiSystem>('name', t('ai.columns.name'), (a) => a.name, { size: 280 }),
      textColumn<AiSystem>('class', t('ai.columns.class'), (a) => a.aiActClass ?? '—', {
        size: 200,
      }),
      statusColumn<AiSystem>(
        'status',
        t('ai.columns.status'),
        (a) => a.status,
        (a) => <StatusPill semantic={semantic[a.status]} text={t(`ai.status.${a.status}`)} />,
        { size: 150 },
      ),
      textColumn<AiSystem>('submitted', t('ai.columns.submittedBy'), (a) => a.submittedBy ?? '—', {
        size: 170,
      }),
      textColumn<AiSystem>('approved', t('ai.columns.approvedBy'), (a) => a.approvedBy ?? '—', {
        size: 170,
      }),
      {
        id: 'actions',
        header: t('ai.columns.actions'),
        size: 320,
        enableSorting: false,
        meta: { label: t('ai.columns.actions') },
        cell: ({ row }) => (
          <div className={styles.actions}>
            {actionsFor(row.original.status).map((action) => (
              <Button
                key={action}
                variant={action === 'retire' ? 'danger-ghost' : 'ghost'}
                size="sm"
                onPress={() => {
                  setPending({ featureId: row.original.featureId, action });
                  setReason('');
                  move.reset();
                }}
              >
                {t(`ai.action.${action}`)}
              </Button>
            ))}
          </div>
        ),
      },
    ],
    [t, move],
  );

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('ai.overline')}
        title={t('ai.title')}
        description={t('ai.lead')}
      />
      <Section title={t('ai.register.title')}>
        <form
          className={styles.filters}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            submitRegister();
          }}
        >
          <TextField
            label={t('ai.register.featureId')}
            value={featureId}
            onChange={setFeatureId}
            helperText={t('ai.register.featureIdHelp')}
            errorMessage={
              tried && !featureIdPattern.test(featureId.trim())
                ? t('ai.register.featureIdInvalid')
                : undefined
            }
          />
          <TextField
            label={t('ai.register.name')}
            value={name}
            onChange={setName}
            errorMessage={tried && name.trim() === '' ? t('ai.register.nameRequired') : undefined}
          />
          <TextField label={t('ai.register.class')} value={aiActClass} onChange={setAiActClass} />
          <Button type="submit" variant="commit" isLoading={register.isPending}>
            {t('ai.register.submit')}
          </Button>
        </form>
        {register.isError ? <ProblemBanner error={register.error} /> : null}
      </Section>
      {pending ? (
        <Section title={t(`ai.action.${pending.action}`)}>
          <form
            className={styles.form}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              runMove();
            }}
          >
            <Banner variant="info" live="none" title={pending.featureId}>
              {t(`ai.confirm.${pending.action}`)}
            </Banner>
            <TextField
              label={t('ai.reason')}
              multiline
              minRows={2}
              autoFocus
              value={reason}
              onChange={setReason}
            />
            <div className={styles.actions}>
              <Button type="submit" variant="commit" isLoading={move.isPending}>
                {t(`ai.action.${pending.action}`)}
              </Button>
              <Button
                variant="secondary"
                onPress={() => {
                  setPending(null);
                  move.reset();
                }}
              >
                {t('ai.cancel')}
              </Button>
            </div>
            {move.isError ? <ProblemBanner error={move.error} /> : null}
          </form>
        </Section>
      ) : null}
      <Section
        title={t('ai.list.title')}
        {...(query.data ? { count: query.data.items.length } : {})}
      >
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<AiSystem>
              aria-label={t('ai.list.title')}
              columns={columns}
              data={page.items}
              getRowId={(a) => a.featureId}
              getRowLabel={(a) => a.featureId}
              emptyState={<p className="ds-caption">{t('ai.list.empty')}</p>}
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
