import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { ConfigChangePreviewRun, ConfigChangeRequestView } from '../../../api/types';
import { Banner, Button, KeyValueList, TextField, announce } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { packErrorText } from '../packs/errors';
import { HashText } from '../packs/parts';
import { useConfigCaller } from './access';
import { useConfigAction, useConfigChange, useConfigPreview, type ConfigAction } from './api';
import cfg from './Config.module.css';
import { ConfigStatusPill, LegalStatusPill } from './parts';

const open = (r: ConfigChangeRequestView) =>
  r.status === 'DRAFT' || r.status === 'PENDING_APPROVAL' || r.status === 'SCHEDULED';

/** Key by key: the value asked for, and what the newest state resolves to now and after applying it. */
function ChangeItems({
  request,
  preview,
}: {
  request: ConfigChangeRequestView;
  preview: ConfigChangePreviewRun | undefined;
}) {
  const { t } = useTranslation('market');
  const none = t('config.detail.noValue');
  return (
    <ul className={cfg.items} aria-label={t('config.detail.items')}>
      {request.items.map((item) => {
        const diff = preview?.items.find((p) => p.key === item.key);
        return (
          <li key={item.key} className={cfg.item} data-changed={diff?.changed ? 'true' : 'false'}>
            <div className={cfg.itemHead}>
              <span className={cfg.key}>{item.key}</span>
              <LegalStatusPill status={item.legalStatus} />
            </div>
            <dl className={cfg.diff}>
              {diff ? (
                <>
                  <div>
                    <dt>{t('config.detail.before')}</dt>
                    <dd className={diff.before !== diff.after ? cfg.before : undefined}>
                      {diff.before ?? none}
                    </dd>
                  </div>
                  <div>
                    <dt>{t('config.detail.after')}</dt>
                    <dd className={diff.before !== diff.after ? cfg.after : undefined}>
                      {diff.after ?? item.value}
                    </dd>
                  </div>
                </>
              ) : (
                <div>
                  <dt>{t('config.detail.value')}</dt>
                  <dd>{item.value}</dd>
                </div>
              )}
              <div>
                <dt>{t('config.detail.source')}</dt>
                <dd>{item.sourceRef}</dd>
              </div>
              <div>
                <dt>{t('config.detail.validity')}</dt>
                <dd>
                  {item.validFrom ?? t('config.detail.always')} →{' '}
                  {item.validTo ?? t('config.detail.open')}
                </dd>
              </div>
              {item.note ? (
                <div>
                  <dt>{t('config.detail.note')}</dt>
                  <dd>{item.note}</dd>
                </div>
              ) : null}
            </dl>
          </li>
        );
      })}
    </ul>
  );
}

/** The decision area: submit / withdraw / rebase (maker) or approve / reject (checker, never the maker). */
function ActionPanel({ request, stale }: { request: ConfigChangeRequestView; stale: boolean }) {
  const { t } = useTranslation('market');
  const caller = useConfigCaller();
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useConfigAction();
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState<ConfigAction | null>(null);
  const [done, setDone] = useState<ConfigAction | null>(null);

  const isMaker = caller.actorKey !== '' && request.createdBy === caller.actorKey;
  const makerActions = caller.canMake && isMaker;
  const checkerActions = caller.canDecide && !isMaker && request.status === 'PENDING_APPROVAL';

  const run = (action: ConfigAction, needsReason: boolean) => {
    setTried(action);
    const text = reason.trim();
    if (needsReason && text === '') return;
    const body = { requestId: request.id, ...(text ? { reason: text } : {}) };
    mutation.mutate(
      { action, body, key: keyFor({ action, body }) },
      {
        onSuccess: () => {
          release();
          setDone(action);
          setReason('');
          announce(t(`config.action.done.${action}`));
        },
      },
    );
  };

  if (done) {
    return (
      <Banner variant="success" live="status" title={t(`config.action.done.${done}`)}>
        {done === 'rebase' && mutation.data?.conflicts?.length
          ? t('config.action.rebaseConflicts', { count: mutation.data.conflicts.length })
          : t('config.action.doneBody')}
      </Banner>
    );
  }

  const canAnything =
    request.status === 'DRAFT'
      ? makerActions
      : request.status === 'PENDING_APPROVAL'
        ? makerActions || checkerActions
        : request.status === 'SCHEDULED'
          ? makerActions
          : false;

  if (!open(request)) {
    return (
      <Banner variant="info" live="none" title={t('config.action.closedTitle')}>
        {t('config.action.closedBody')}
      </Banner>
    );
  }
  if (!canAnything) {
    const checkerIsMaker = request.status === 'PENDING_APPROVAL' && caller.canDecide && isMaker;
    return (
      <Banner
        variant="info"
        live="none"
        title={t(checkerIsMaker ? 'config.action.makerTitle' : 'config.action.readOnlyTitle')}
      >
        {t(checkerIsMaker ? 'config.action.makerBody' : 'config.action.readOnlyBody')}
      </Banner>
    );
  }

  const pending = mutation.isPending;
  const missing = reason.trim() === '';
  const needs = (a: ConfigAction) => tried === a && missing;
  const button = (
    action: ConfigAction,
    variant: 'primary' | 'secondary' | 'danger',
    needsReason: boolean,
    disabledReason?: string,
  ) => (
    <Button
      key={action}
      variant={variant}
      isLoading={pending && tried === action}
      isDisabled={pending}
      {...(disabledReason ? { disabledReason } : {})}
      onPress={() => {
        run(action, needsReason);
      }}
    >
      {t(`config.action.${action}`)}
    </Button>
  );

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('config.action.title')}
      onSubmit={(e) => {
        e.preventDefault();
      }}
    >
      <TextField
        label={t('config.action.reason')}
        multiline
        maxLength={256}
        showCount
        helperText={t('config.action.reasonHelp')}
        value={reason}
        onChange={setReason}
        errorMessage={
          needs('approve') || needs('reject') || needs('withdraw') || needs('rebase')
            ? t('config.action.reasonRequired')
            : undefined
        }
      />
      {mutation.isError ? (
        <Banner variant="danger" live="alert" title={t('config.action.failed')}>
          {packErrorText(t, mutation.error)}
        </Banner>
      ) : null}
      <div className={styles.actions}>
        {makerActions && request.status === 'DRAFT' ? button('submit', 'primary', false) : null}
        {makerActions && stale ? button('rebase', 'primary', true) : null}
        {checkerActions
          ? button('approve', 'primary', true, stale ? t('config.action.approveStale') : undefined)
          : null}
        {checkerActions ? button('reject', 'danger', true) : null}
        {makerActions && request.status !== 'SCHEDULED'
          ? button('withdraw', 'secondary', true)
          : null}
      </div>
    </form>
  );
}

function ConfigDetail({ request }: { request: ConfigChangeRequestView }) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const caller = useConfigCaller();
  const preview = useConfigPreview(
    request.id,
    open(request) && caller.canView,
    request.recordVersion,
  );
  const run = preview.data;
  const stale = run?.stale ?? false;
  const isMakerDraft =
    request.status === 'DRAFT' && caller.canMake && request.createdBy === caller.actorKey;

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('config.overline')}
        title={t('config.detail.title')}
        recordId={request.id.slice(-8)}
        subtitle={<ConfigStatusPill status={request.status} />}
        facts={[
          { id: 'country', label: t('config.detail.country'), value: request.country },
          { id: 'maker', label: t('config.columns.maker'), value: request.createdBy },
          {
            id: 'created',
            label: t('config.columns.created'),
            value: fmt.dateTime(request.createdAt),
          },
        ]}
        actions={
          <>
            {isMakerDraft ? (
              <LinkButton variant="secondary" to={`/admin/config/${request.id}/edit`}>
                {t('config.detail.edit')}
              </LinkButton>
            ) : null}
            <LinkButton variant="secondary" to="/admin/config">
              {t('config.detail.back')}
            </LinkButton>
          </>
        }
      />
      {stale && run ? (
        <Banner variant="warning" live="none" title={t('config.detail.staleTitle')}>
          {t('config.detail.staleBody')}
          <ul className={cfg.conflicts}>
            {run.conflicts.map((c) => (
              <li key={c.key}>
                <span className="ds-mono">{c.key}</span>:{' '}
                {c.baseValue ?? t('config.detail.noValue')} →{' '}
                {c.currentValue ?? t('config.detail.noValue')}
              </li>
            ))}
          </ul>
        </Banner>
      ) : null}
      <Section title={t('config.detail.items')} family="brand" count={request.items.length}>
        <ChangeItems request={request} preview={run} />
        {preview.isError ? (
          <Banner variant="danger" live="none" title={t('config.detail.previewFailed')}>
            {packErrorText(t, preview.error)}
          </Banner>
        ) : null}
      </Section>
      <Section title={t('config.detail.summary')} family="info">
        <KeyValueList
          aria-label={t('config.detail.summary')}
          items={[
            { id: 'reason', label: t('config.columns.reason'), value: request.reason },
            {
              id: 'cause',
              label: t('config.detail.cause'),
              value: t(`config.cause.${request.cause}`),
            },
            {
              id: 'effective',
              label: t('config.detail.effectiveAt'),
              value: request.effectiveAt ? fmt.dateTime(request.effectiveAt) : null,
            },
            { id: 'checker', label: t('config.detail.decidedBy'), value: request.decidedBy },
            {
              id: 'decision',
              label: t('config.detail.decisionReason'),
              value: request.decisionReason,
            },
            {
              id: 'base',
              label: t('config.detail.baseHash'),
              value: <HashText value={request.baseHash} label={t('config.detail.baseHash')} />,
            },
            {
              id: 'result',
              label: t('config.detail.resultingHash'),
              value: request.resultingHash ? (
                <HashText value={request.resultingHash} label={t('config.detail.resultingHash')} />
              ) : null,
            },
            {
              id: 'applied',
              label: t('config.detail.appliedAt'),
              value: request.appliedAt ? fmt.dateTime(request.appliedAt) : null,
            },
          ]}
        />
      </Section>
      <Section title={t('config.action.title')} family="warning">
        <ActionPanel request={request} stale={stale} />
      </Section>
    </div>
  );
}

/** One change request: items with before/after, the impact preview, and the maker / checker actions. */
export function ConfigDetailPage() {
  const { t } = useTranslation('market');
  const { id = '' } = useParams();
  const query = useConfigChange(id);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('config.detail.notFound')}>
        {(request) => <ConfigDetail request={request} />}
      </QueryView>
    </div>
  );
}
