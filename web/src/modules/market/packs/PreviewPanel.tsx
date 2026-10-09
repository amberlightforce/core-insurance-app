import { useTranslation } from 'react-i18next';

import type { PackActivationKeyDiff, PackActivationPreview } from '../../../api/types';
import { StatusPill } from '../../../design-system';
import { useFormat } from '../../staff/useFormat';
import styles from './Packs.module.css';
import { HashText } from './parts';

function ChangePill({ change }: { change: PackActivationKeyDiff['change'] }) {
  const { t } = useTranslation('market');
  const text = t(`packs.change.${change}`);
  const semantic = change === 'ADDED' ? 'success' : change === 'REMOVED' ? 'error' : 'warning';
  return <StatusPill semantic={semantic} text={text} size="sm" announceChanges={false} />;
}

/** The dry-run result of a request: from → to, the window, the hashes issued and the key differences. */
export function PreviewPanel({ preview }: { preview: PackActivationPreview }) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const count = (change: PackActivationKeyDiff['change']) =>
    preview.keyDiff.filter((d) => d.change === change).length;
  return (
    <section className={styles.dialogBody} aria-label={t('packs.preview.title')}>
      <h3 className="ds-heading-3">{t('packs.preview.title')}</h3>
      <dl className={styles.facts}>
        <div>
          <dt>{t('packs.preview.fromTo')}</dt>
          <dd>
            <span className="ds-mono">{preview.fromVersion ?? t('packs.dialog.none')}</span>
            <span className={styles.arrow}> → </span>
            <span className="ds-mono">{preview.toVersion}</span>
          </dd>
        </div>
        <div>
          <dt>{t('packs.preview.window')}</dt>
          <dd>
            {fmt.dateTime(preview.window.from)} →{' '}
            {preview.window.to ? fmt.dateTime(preview.window.to) : t('packs.preview.windowOpen')}
          </dd>
        </div>
        <div>
          <dt>{t('packs.preview.hashes', { count: preview.hashesIssued.length })}</dt>
          <dd>
            {preview.hashesIssued.length === 0 ? (
              <span>{t('packs.preview.noHashes')}</span>
            ) : (
              <ul className={styles.diff}>
                {preview.hashesIssued.map((h) => (
                  <li key={h}>
                    <HashText value={h} label={t('packs.preview.hashLabel')} />
                  </li>
                ))}
              </ul>
            )}
          </dd>
        </div>
      </dl>
      <h4 className="ds-caption">
        {t('packs.preview.keyDiff')}
        {' · '}
        {t('packs.preview.diffCounts', {
          added: count('ADDED'),
          removed: count('REMOVED'),
          changed: count('CHANGED'),
        })}
      </h4>
      {preview.keyDiff.length === 0 ? (
        <p className="ds-caption">{t('packs.preview.noDiff')}</p>
      ) : (
        <ul className={styles.diff}>
          {preview.keyDiff.map((d) => (
            <li key={`${d.change}:${d.key}`}>
              <ChangePill change={d.change} />
              <span className={`ds-mono ${styles.diffKey}`}>{d.key}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
