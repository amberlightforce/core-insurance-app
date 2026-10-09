import { useTranslation } from 'react-i18next';

import type { PackActivationView } from '../../../api/types';
import { LinkButton } from '../../staff/LinkButton';
import { useFormat } from '../../staff/useFormat';
import styles from './Packs.module.css';
import { ActivationStatusPill, HashText } from './parts';

/** One activation or rollback in a list: kind, from → to, who asked and decided, when, and the resulting hash. */
export function ActivationRow({
  activation,
  packId,
}: {
  activation: PackActivationView;
  packId: string;
}) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const kind = t(`packs.kind.${activation.kind}`);
  return (
    <li className={styles.historyRow} data-testid="activation-row">
      <div className={styles.versionHead}>
        <span className={styles.versionName}>{kind}</span>
        <span>
          <span className="ds-mono">{activation.from ?? t('packs.dialog.none')}</span>
          <span className={styles.arrow}> → </span>
          <span className="ds-mono">{activation.to}</span>
        </span>
        <ActivationStatusPill status={activation.status} />
        <LinkButton to={`/admin/packs/${packId}/activations/${activation.activationId}`}>
          {t('packs.history.open')}
        </LinkButton>
      </div>
      <dl className={styles.facts}>
        <div>
          <dt>{t('packs.history.entity')}</dt>
          <dd className="ds-mono">{activation.legalEntity}</dd>
        </div>
        <div>
          <dt>{t('packs.history.requestedBy')}</dt>
          <dd>{activation.requestedBy}</dd>
        </div>
        <div>
          <dt>{t('packs.history.decidedBy')}</dt>
          <dd>{activation.decidedBy ?? t('packs.history.notDecided')}</dd>
        </div>
        <div>
          <dt>{t('packs.history.time')}</dt>
          <dd>{activation.activatedAt ? fmt.dateTime(activation.activatedAt) : '—'}</dd>
        </div>
        <div>
          <dt>{t('packs.history.reason')}</dt>
          <dd>{activation.reason}</dd>
        </div>
        <div>
          <dt>{t('packs.history.resultingHash')}</dt>
          <dd>
            {activation.resultingHash ? (
              <HashText value={activation.resultingHash} label={t('packs.history.resultingHash')} />
            ) : (
              '—'
            )}
          </dd>
        </div>
      </dl>
    </li>
  );
}
