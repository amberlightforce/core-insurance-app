import { useTranslation } from 'react-i18next';

import type { ContractLayer } from './api';
import { useLayerSummary } from './layerSummary';
import { fromMinor, toMinor } from './money';
import styles from './Reinsurance.module.css';
import { useFormat } from '../staff/useFormat';

/**
 * The layer ladder: the retention at the bottom, then each layer from its attachment up by its limit, in design
 * tokens (no chart library). Heights follow the limits but never drop below a readable rung, so no label is cut;
 * the exact amounts are text in every rung and the layers table carries the same numbers.
 */
export function LayerDiagram({ layers }: { layers: readonly ContractLayer[] }) {
  const { t } = useTranslation('reinsurance');
  const fmt = useFormat();
  const summaryOf = useLayerSummary();
  const sorted = [...layers].sort((a, b) => a.layerNo - b.layerNo);
  const first = sorted[0];
  if (!first) return null;
  const currency = first.limit.currency;
  const money = (minor: bigint) => fmt.money({ amount: fromMinor(minor), currency });
  const retention = toMinor(first.attachment.amount);
  const total = sorted.reduce((top, layer) => {
    const end = toMinor(layer.attachment.amount) + toMinor(layer.limit.amount);
    return end > top ? end : top;
  }, retention);
  // Layout proportions only: the amounts themselves stay BigInt and are shown as text.
  const share = (part: bigint) => Math.max(1, Number((part * 1000n) / (total > 0n ? total : 1n)));
  const description = sorted
    .map((layer) => t('layers.diagramLayer', { no: layer.layerNo, summary: summaryOf(layer) }))
    .join('; ');

  return (
    <ol className={styles.ladder} role="img" aria-label={`${t('layers.diagram')}: ${description}`}>
      {sorted.map((layer, index) => {
        const attachment = toMinor(layer.attachment.amount);
        const limit = toMinor(layer.limit.amount);
        return (
          <li
            key={layer.layerNo}
            className={styles.rung}
            data-tone={String(index % 4)}
            style={{ flexGrow: share(limit) }}
          >
            <span className={styles.rungBar} aria-hidden="true" />
            <span className={styles.rungText}>
              <span className={styles.rungTitle}>{t('layers.layerNo', { no: layer.layerNo })}</span>
              <span className={styles.rungRange}>
                {t('layers.range', { from: money(attachment), to: money(attachment + limit) })}
              </span>
            </span>
          </li>
        );
      })}
      <li className={styles.rung} data-retention="" style={{ flexGrow: share(retention) }}>
        <span className={styles.rungBar} aria-hidden="true" />
        <span className={styles.rungText}>
          <span className={styles.rungTitle}>{t('layers.retention')}</span>
          <span className={styles.rungRange}>
            {t('layers.range', { from: money(0n), to: money(retention) })}
          </span>
        </span>
      </li>
    </ol>
  );
}
