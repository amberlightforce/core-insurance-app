import { useTranslation } from 'react-i18next';

import type { PackListItem } from '../../../api/types';
import { EmptyState, StatusPill } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { usePacks } from './api';
import packStyles from './Packs.module.css';

function PackCard({ pack }: { pack: PackListItem }) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const published = pack.versions.filter((v) => v.status === 'PUBLISHED').length;
  return (
    <Section
      title={pack.pack}
      meta={t(`packs.scope.${pack.scope}`)}
      actions={
        <LinkButton variant="secondary" to={`/admin/packs/${pack.packId}`}>
          {t('packs.registry.open', { pack: pack.pack })}
        </LinkButton>
      }
    >
      <div className={packStyles.packCard}>
        <p className={styles.muted}>
          {t('packs.registry.versions', { count: pack.versions.length, published })}
        </p>
        {pack.activeVersions.length === 0 ? (
          <p className={styles.muted}>{t('packs.registry.neverActivated')}</p>
        ) : (
          <ul className={packStyles.entityList} aria-label={t('packs.registry.activePerEntity')}>
            {pack.activeVersions.map((a) => (
              <li key={a.legalEntity} className={packStyles.entity}>
                <span className="ds-mono">{a.legalEntity}</span>
                <StatusPill
                  semantic="success"
                  text={t('packs.registry.activeVersion', { version: a.version })}
                  announceChanges={false}
                />
                <span className={styles.muted}>
                  {t('packs.registry.since', { date: fmt.dateTime(a.activeSince) })}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Section>
  );
}

/** SCR-MKT-03 (subset): the market packs with the active version per legal entity. */
export function PackRegistryPage() {
  const { t } = useTranslation('market');
  const query = usePacks();
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('packs.overline')}
        title={t('packs.registry.title')}
        description={t('packs.registry.subtitle')}
      />
      <QueryView query={query}>
        {(page) =>
          page.items.length === 0 ? (
            <EmptyState
              kind="done"
              headingLevel={2}
              headline={t('packs.registry.emptyTitle')}
              description={t('packs.registry.emptyBody')}
            />
          ) : (
            <div className={styles.grid}>
              {page.items.map((pack) => (
                <PackCard key={pack.packId} pack={pack} />
              ))}
            </div>
          )
        }
      </QueryView>
    </div>
  );
}
