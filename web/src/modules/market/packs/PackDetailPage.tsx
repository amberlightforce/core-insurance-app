import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';

import type { PackGetResponse } from '../../../api/types';
import { Banner, Button, EmptyState } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useCaller } from './access';
import { ActivationDialog } from './ActivationDialog';
import { ActivationRow } from './ActivationRow';
import { usePack } from './api';
import type { ActivationKind } from './api';
import { isPending } from './lifecycle';
import packStyles from './Packs.module.css';
import { VersionTimeline } from './VersionTimeline';

function PackDetail({ pack }: { pack: PackGetResponse }) {
  const { t } = useTranslation('market');
  const navigate = useNavigate();
  const caller = useCaller();
  const [dialog, setDialog] = useState<ActivationKind | null>(null);

  const pending = pack.activationHistory.filter(isPending);
  const hasTargets = pack.versions.some((v) => v.status === 'PUBLISHED');
  const canRollBack = pack.activeVersions.length > 0 && pack.versions.length > 1;

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('packs.overline')}
        title={pack.pack}
        subtitle={<span className="ds-caption">{t(`packs.scope.${pack.scope}`)}</span>}
        facts={pack.activeVersions.map((a) => ({
          id: a.legalEntity,
          label: a.legalEntity,
          value: <span className="ds-mono">{a.version}</span>,
        }))}
        actions={
          <>
            {caller.canRequest ? (
              <>
                <Button
                  variant="secondary"
                  {...(!canRollBack
                    ? { disabledReason: t('packs.detail.rollbackUnavailable') }
                    : {})}
                  onPress={() => {
                    setDialog('ROLLBACK');
                  }}
                >
                  {t('packs.detail.rollback')}
                </Button>
                <Button
                  variant="primary"
                  {...(!hasTargets
                    ? { disabledReason: t('packs.detail.activateUnavailable') }
                    : {})}
                  onPress={() => {
                    setDialog('ACTIVATE');
                  }}
                >
                  {t('packs.detail.activate')}
                </Button>
              </>
            ) : null}
            <LinkButton variant="secondary" to="/admin/packs">
              {t('packs.detail.back')}
            </LinkButton>
          </>
        }
      />
      {!caller.canRequest ? (
        <Banner variant="info" live="none" title={t('packs.detail.readOnlyTitle')}>
          {t('packs.detail.readOnlyBody')}
        </Banner>
      ) : null}
      <Section
        title={t('packs.detail.pending')}
        family="plum"
        count={pending.length}
        meta={t('packs.detail.pendingMeta')}
      >
        {pending.length === 0 ? (
          <p className={styles.muted}>{t('packs.detail.noPending')}</p>
        ) : (
          <ul className={packStyles.history}>
            {pending.map((a) => (
              <ActivationRow key={a.activationId} activation={a} packId={pack.packId} />
            ))}
          </ul>
        )}
      </Section>
      <div className={styles.split}>
        <Section title={t('packs.timeline.title')} count={pack.versions.length}>
          {pack.versions.length === 0 ? (
            <EmptyState
              kind="done"
              headingLevel={3}
              headline={t('packs.timeline.emptyTitle')}
              description={t('packs.timeline.emptyBody')}
            />
          ) : (
            <VersionTimeline
              versions={pack.versions}
              active={pack.activeVersions}
              history={pack.activationHistory}
            />
          )}
        </Section>
        <Section title={t('packs.history.title')} count={pack.activationHistory.length}>
          {pack.activationHistory.length === 0 ? (
            <EmptyState
              kind="done"
              headingLevel={3}
              headline={t('packs.history.emptyTitle')}
              description={t('packs.history.emptyBody')}
            />
          ) : (
            <ul className={packStyles.history} aria-label={t('packs.history.title')}>
              {pack.activationHistory.map((a) => (
                <ActivationRow key={a.activationId} activation={a} packId={pack.packId} />
              ))}
            </ul>
          )}
        </Section>
      </div>
      {dialog ? (
        <ActivationDialog
          pack={pack}
          kind={dialog}
          isOpen
          onClose={() => {
            setDialog(null);
          }}
          onSubmitted={(activationId) => {
            void navigate(`/admin/packs/${pack.packId}/activations/${activationId}`);
          }}
        />
      ) : null}
    </div>
  );
}

/** One pack: versions, the active version per entity, pending requests and the activation history. */
export function PackDetailPage() {
  const { t } = useTranslation('market');
  const { packId = '' } = useParams();
  const query = usePack(packId);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('packs.detail.notFound')}>
        {(pack) => <PackDetail pack={pack} />}
      </QueryView>
    </div>
  );
}
