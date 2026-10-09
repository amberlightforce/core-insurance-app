import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { PackActivationView } from '../../../api/types';
import { Banner, Button, KeyValueList, TextField, announce } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useCaller } from './access';
import { useDecideActivation, usePackActivation, useRefreshPacks } from './api';
import { packErrorText } from './errors';
import { isPending } from './lifecycle';
import { ActivationStatusPill, HashText } from './parts';

/** The checker's decision: a reason is required for either outcome (mkt.PackActivation.decide). */
function DecisionForm({ activation }: { activation: PackActivationView }) {
  const { t } = useTranslation('market');
  const { keyFor, release } = useIdempotencyKey();
  const refresh = useRefreshPacks();
  const mutation = useDecideActivation();
  const [reason, setReason] = useState('');
  const [intent, setIntent] = useState<'APPROVE' | 'REJECT' | null>(null);
  const [tried, setTried] = useState(false);
  const missing = reason.trim() === '';

  const decide = (decision: 'APPROVE' | 'REJECT') => {
    setIntent(decision);
    setTried(true);
    if (missing) return;
    const body = { activationId: activation.activationId, decision, reason: reason.trim() };
    mutation.mutate(
      { body, key: keyFor(body) },
      {
        onSuccess: () => {
          release();
          announce(decision === 'APPROVE' ? t('packs.decide.approved') : t('packs.decide.rejected'));
          void refresh();
        },
      },
    );
  };

  if (mutation.isSuccess) {
    const approved = mutation.data.activation.status !== 'REJECTED';
    return (
      <Banner
        variant={approved ? 'success' : 'info'}
        live="status"
        title={approved ? t('packs.decide.approved') : t('packs.decide.rejected')}
      >
        {t('packs.decide.doneBody')}
      </Banner>
    );
  }

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('packs.decide.title')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <TextField
        label={t('packs.decide.reason')}
        multiline
        maxLength={128}
        showCount
        isRequired
        helperText={t('packs.decide.reasonHelp')}
        value={reason}
        onChange={setReason}
        errorMessage={tried && missing ? t('packs.decide.reasonRequired') : undefined}
      />
      {mutation.isError ? (
        <Banner variant="danger" live="alert" title={t('packs.decide.failed')}>
          {packErrorText(t, mutation.error)}
        </Banner>
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending && intent === 'APPROVE'}
          isDisabled={mutation.isPending}
          onPress={() => {
            decide('APPROVE');
          }}
        >
          {t('packs.decide.approve')}
        </Button>
        <Button
          variant="danger"
          isLoading={mutation.isPending && intent === 'REJECT'}
          isDisabled={mutation.isPending}
          onPress={() => {
            decide('REJECT');
          }}
        >
          {t('packs.decide.reject')}
        </Button>
      </div>
    </form>
  );
}

function ActivationDetail({ activation, packId }: { activation: PackActivationView; packId: string }) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const caller = useCaller();
  const pending = isPending(activation);
  // The maker sees the request read-only. The server also refuses the maker's principal (PITFALLS 3-6).
  const isMaker =
    caller.id !== '' && (activation.requestedBy === caller.id || activation.requestedBy === caller.name);
  const kind = t(`packs.kind.${activation.kind}`);

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('packs.overline')}
        title={t('packs.activation.title', { kind, pack: activation.pack })}
        subtitle={<ActivationStatusPill status={activation.status} />}
        facts={[
          {
            id: 'move',
            label: t('packs.activation.move'),
            value: (
              <span className="ds-mono">
                {activation.from ?? t('packs.dialog.none')} → {activation.to}
              </span>
            ),
          },
          { id: 'entity', label: t('packs.history.entity'), value: activation.legalEntity },
        ]}
        actions={
          <LinkButton variant="secondary" to={`/admin/packs/${packId}`}>
            {t('packs.activation.back')}
          </LinkButton>
        }
      />
      <Section title={t('packs.activation.summary')}>
        <KeyValueList
          aria-label={t('packs.activation.summary')}
          items={[
            { id: 'kind', label: t('packs.history.kind'), value: kind },
            { id: 'reason', label: t('packs.history.reason'), value: activation.reason },
            { id: 'maker', label: t('packs.history.requestedBy'), value: activation.requestedBy },
            {
              id: 'checker',
              label: t('packs.history.decidedBy'),
              value: activation.decidedBy ?? t('packs.history.notDecided'),
            },
            {
              id: 'at',
              label: t('packs.history.time'),
              value: activation.activatedAt ? fmt.dateTime(activation.activatedAt) : null,
            },
            {
              id: 'hash',
              label: t('packs.history.resultingHash'),
              value: activation.resultingHash ? (
                <HashText value={activation.resultingHash} label={t('packs.history.resultingHash')} />
              ) : null,
            },
          ]}
        />
      </Section>
      {pending ? (
        <Section title={t('packs.decide.title')}>
          {isMaker ? (
            <Banner variant="info" live="none" title={t('packs.decide.waitingTitle')}>
              {t('packs.decide.waitingBody')}
            </Banner>
          ) : !caller.canDecide ? (
            <Banner variant="info" live="none" title={t('packs.decide.notCheckerTitle')}>
              {t('packs.decide.notCheckerBody')}
            </Banner>
          ) : (
            <DecisionForm activation={activation} />
          )}
        </Section>
      ) : (
        <Banner variant="info" live="none" title={t('packs.decide.notPendingTitle')}>
          {t('packs.decide.notPendingBody')}
        </Banner>
      )}
    </div>
  );
}

/** One activation request: summary, and for a pending one the checker's approve / reject (maker: read-only). */
export function ActivationPage() {
  const { t } = useTranslation('market');
  const { packId = '', activationId = '' } = useParams();
  const query = usePackActivation(activationId);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('packs.activation.notFound')}>
        {(activation) => <ActivationDetail activation={activation} packId={packId} />}
      </QueryView>
    </div>
  );
}
