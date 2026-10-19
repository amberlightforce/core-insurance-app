import { useTranslation } from 'react-i18next';

import { Banner } from '../../../design-system';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import type { AuthorityPreviewItem } from './api';

/**
 * What submitting would do under the handler's authority, from the dry run (D-SL4-14): before the user submits,
 * «Θα χρειαστεί έγκριση από …». The decision is the server's; this only displays it.
 */
export function AuthorityPreview({ items }: { items: readonly AuthorityPreviewItem[] }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const worst = items.some((i) => i.outcome === 'DENY')
    ? 'DENY'
    : items.some((i) => i.outcome === 'REFER')
      ? 'REFER'
      : 'WITHIN';
  return (
    <Banner
      variant={worst === 'WITHIN' ? 'success' : worst === 'REFER' ? 'warning' : 'danger'}
      live="none"
      title={t(`authorityPreview.title.${worst}`)}
    >
      <ul className={styles.stack} aria-label={t('authorityPreview.label')}>
        {items.map((item, index) => (
          <li key={`${item.type}:${item.costType}:${String(index)}`}>
            {t(`authorityPreview.line.${item.outcome}`, {
              type: t(`authorityPreview.types.${item.type.replaceAll('.', '_')}`, {
                defaultValue: item.type,
              }),
              amount: fmt.money(item.amount),
              role: item.role
                ? t(`authorityPreview.roles.${item.role.replaceAll('.', '_')}`, {
                    defaultValue: item.role,
                  })
                : '—',
            })}
          </li>
        ))}
      </ul>
    </Banner>
  );
}
