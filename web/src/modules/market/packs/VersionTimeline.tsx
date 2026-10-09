import { useTranslation } from 'react-i18next';

import type {
  PackActivationView,
  PackActiveVersionView,
  PackVersionView,
} from '../../../api/types';
import { StatusPill } from '../../../design-system';
import { useFormat } from '../../staff/useFormat';
import { versionLifecycle, type EntityLifecycle } from './lifecycle';
import styles from './Packs.module.css';
import { HashText } from './parts';

function LifecycleLine({ lifecycle }: { lifecycle: EntityLifecycle }) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const entity = lifecycle.legalEntity;
  if (lifecycle.state === 'ACTIVE') {
    return (
      <div>
        <StatusPill
          semantic="success"
          text={t('packs.timeline.active', { entity })}
          announceChanges={false}
        />
        <dl className={styles.facts}>
          <div>
            <dt>{t('packs.timeline.activeSince')}</dt>
            <dd>{fmt.dateTime(lifecycle.since)}</dd>
          </div>
          <div>
            <dt>{t('packs.timeline.configurationHash')}</dt>
            <dd>
              <HashText value={lifecycle.hash} label={t('packs.timeline.configurationHash')} />
            </dd>
          </div>
        </dl>
      </div>
    );
  }
  if (lifecycle.state === 'ROLLED_BACK') {
    return (
      <div>
        <StatusPill
          semantic="warning"
          text={t('packs.timeline.rolledBack', { entity })}
          announceChanges={false}
        />
        <dl className={styles.facts}>
          <div>
            <dt>{t('packs.timeline.window')}</dt>
            <dd>
              {lifecycle.from ? fmt.dateTime(lifecycle.from) : '—'} →{' '}
              {lifecycle.to ? fmt.dateTime(lifecycle.to) : '—'}
            </dd>
          </div>
          <div>
            <dt>{t('packs.timeline.hashesIssued')}</dt>
            <dd>
              {lifecycle.hash ? (
                <HashText value={lifecycle.hash} label={t('packs.timeline.hashesIssued')} />
              ) : (
                t('packs.timeline.noHash')
              )}
            </dd>
          </div>
        </dl>
        <p className="ds-caption">{t('packs.timeline.rolledBackNote')}</p>
      </div>
    );
  }
  return (
    <StatusPill
      semantic="read-only"
      text={t('packs.timeline.superseded', { entity })}
      announceChanges={false}
    />
  );
}

/** Versions, newest first: the pack-level status, the digest and each legal entity's lifecycle state. */
export function VersionTimeline({
  versions,
  active,
  history,
}: {
  versions: readonly PackVersionView[];
  active: readonly PackActiveVersionView[];
  history: readonly PackActivationView[];
}) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const ordered = [...versions].sort((a, b) =>
    (b.publishedAt ?? '').localeCompare(a.publishedAt ?? ''),
  );
  return (
    <ol className={styles.timeline} aria-label={t('packs.timeline.title')}>
      {ordered.map((v) => {
        const lifecycle = versionLifecycle(v.version, active, history);
        const rolledBack = lifecycle.some((l) => l.state === 'ROLLED_BACK');
        const isActive = lifecycle.some((l) => l.state === 'ACTIVE');
        return (
          <li
            key={v.version}
            className={styles.version}
            data-state={rolledBack ? 'ROLLED_BACK' : isActive ? 'ACTIVE' : 'PLAIN'}
            data-testid={`version-${v.version}`}
          >
            <div className={styles.versionHead}>
              <span className={`ds-mono ${styles.versionName}`}>{v.version}</span>
              <StatusPill
                semantic={v.status === 'PUBLISHED' ? 'info' : 'read-only'}
                text={t(`packs.versionStatus.${v.status}`)}
                size="sm"
                announceChanges={false}
              />
              <span className="ds-caption">
                {v.publishedAt
                  ? t('packs.timeline.publishedAt', { date: fmt.dateTime(v.publishedAt) })
                  : t('packs.timeline.notPublished')}
              </span>
            </div>
            <dl className={styles.facts}>
              <div>
                <dt>{t('packs.timeline.digest')}</dt>
                <dd>
                  <HashText value={v.contentDigest} label={t('packs.timeline.digest')} />
                </dd>
              </div>
            </dl>
            {lifecycle.map((l) => (
              <LifecycleLine key={l.legalEntity} lifecycle={l} />
            ))}
          </li>
        );
      })}
    </ol>
  );
}
