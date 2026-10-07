import { Lock } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { PermissionLimited } from '../States';
import { EditableValue, type InlineEdit } from './EditableValue';
import styles from './KeyValueList.module.css';

export interface KeyValueItem {
  id: string;
  label: string;
  /** Display value. `null`, `undefined` or `''` render «—» (announced «κενό»). */
  value?: ReactNode;
  /** `mono` for identifiers (policy numbers, IBAN, codes); `money` right-aligns in money-only lists. */
  kind?: 'text' | 'mono' | 'money';
  /** IB-14 inline edit, only for fields the PRD marks editable. */
  edit?: InlineEdit;
  /**
   * The field changes only through a transaction (endorsement, approval, maker-checker): never inline-edited.
   * Shows a lock, «Απαιτείται πρόσθετη πράξη» and an «Έναρξη αλλαγής» link.
   */
  requiresTransaction?: { href?: string; onStart?: () => void };
  /** Explain-why slot (e.g. a «Γιατί;» popover trigger). */
  explain?: ReactNode;
  /** Permission-limited value: masked, never blank. */
  masked?: { display: string; reason?: string };
}

export interface KeyValueListProps {
  items: KeyValueItem[];
  /** All values are money: right-align them (money is left-aligned in mixed lists). */
  moneyOnly?: boolean;
  'aria-label'?: string;
}

function isEmpty(value: ReactNode): boolean {
  return value === null || value === undefined || value === '';
}

/** Key–value list (Part 2 §4.29): a `<dl>` with a label column of minmax(140px, 40%). */
export function KeyValueList({
  items,
  moneyOnly = false,
  'aria-label': ariaLabel,
}: KeyValueListProps) {
  const { t } = useTranslation('ds');

  const renderValue = (item: KeyValueItem) => {
    if (item.masked) {
      return (
        <PermissionLimited
          maskedValue={item.masked.display}
          {...defined({ reason: item.masked.reason })}
        />
      );
    }
    const display = isEmpty(item.value) ? (
      <>
        <span aria-hidden="true">—</span>
        <span className="ds-visually-hidden">{t('keyValueList.empty')}</span>
      </>
    ) : (
      item.value
    );
    if (item.edit && !item.requiresTransaction) {
      return (
        <EditableValue
          label={item.label}
          display={display}
          edit={item.edit}
          mono={item.kind === 'mono'}
        />
      );
    }
    return (
      <span className={cx(styles.valueText, item.kind === 'mono' && styles.mono)}>{display}</span>
    );
  };

  return (
    <dl
      className={styles.list}
      data-money-only={moneyOnly || undefined}
      {...(ariaLabel ? { 'aria-label': ariaLabel } : {})}
    >
      {items.map((item) => (
        <div
          key={item.id}
          className={styles.row}
          data-editable={(item.edit !== undefined && !item.requiresTransaction) || undefined}
          data-kind={item.kind ?? 'text'}
        >
          <dt className={styles.label}>{item.label}</dt>
          <dd className={styles.value}>
            <span className={styles.valueLine}>
              {renderValue(item)}
              {item.explain ? <span className={styles.explain}>{item.explain}</span> : null}
            </span>
            {item.requiresTransaction ? (
              <span className={styles.transaction}>
                <Icon icon={Lock} size={12} />
                {t('keyValueList.requiresTransaction')}
                {item.requiresTransaction.href || item.requiresTransaction.onStart ? (
                  <Link
                    className={cx(styles.link)}
                    {...defined({
                      href: item.requiresTransaction.href,
                      onPress: item.requiresTransaction.onStart,
                    })}
                    aria-label={t('keyValueList.startChangeFor', { label: item.label })}
                  >
                    {t('keyValueList.startChange')}
                  </Link>
                ) : null}
              </span>
            ) : null}
          </dd>
        </div>
      ))}
    </dl>
  );
}
