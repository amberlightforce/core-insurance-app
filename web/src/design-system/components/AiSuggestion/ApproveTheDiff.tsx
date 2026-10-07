import { ArrowRight, Check } from 'lucide-react';
import { useState } from 'react';
import { CheckboxButton, CheckboxField, CheckboxGroup, Label } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import styles from './AiSuggestion.module.css';

export interface DiffRow {
  id: string;
  /** Field label: «Απόθεμα · Υλικές ζημιές». */
  label: string;
  /** Formatted old value (struck through). */
  from: string;
  /** Formatted new value. */
  to: string;
  /** Optional change, e.g. «+1.260,00 €». */
  delta?: string;
}

export interface ApproveTheDiffProps {
  rows: DiffRow[];
  /** Selected row ids (controlled). All rows are selected by default. */
  selected?: string[];
  defaultSelected?: string[];
  onSelectionChange?: (ids: string[]) => void;
  /** «Αποδοχή επιλεγμένων (n)»: accepting still goes through normal validation and authority. */
  onAccept: (ids: string[]) => void;
  onEdit?: () => void;
  onWhy?: () => void;
  /** Without AI styling (maker-checker approval diffs). */
  neutral?: boolean;
  /** Group label; «Αλλαγές προς αποδοχή» by default. */
  label?: string;
  /** Replaces the accept label, e.g. «Αποστολή για έγκριση» when the values exceed the user's authority. */
  acceptLabel?: string;
}

/**
 * Approve-the-diff (IB-07): label → old value (struck) → arrow → new value, a checkbox per row
 * (RAC CheckboxGroup), «Αποδοχή επιλεγμένων (n)», Επεξεργασία, Γιατί;. Each row announces «από … σε …».
 */
export function ApproveTheDiff({
  rows,
  selected: controlled,
  defaultSelected,
  onSelectionChange,
  onAccept,
  onEdit,
  onWhy,
  neutral = false,
  label,
  acceptLabel,
}: ApproveTheDiffProps) {
  const { t } = useTranslation('ds');
  const [uncontrolled, setUncontrolled] = useState<string[]>(
    defaultSelected ?? rows.map((r) => r.id),
  );
  const selected = controlled ?? uncontrolled;
  const count = selected.length;

  return (
    <div className={styles.diff} data-neutral={neutral || undefined}>
      <CheckboxGroup
        className={cx(styles.diffGroup)}
        value={selected}
        onChange={(ids) => {
          setUncontrolled(ids);
          onSelectionChange?.(ids);
        }}
      >
        <Label className="ds-visually-hidden">{label ?? t('approveTheDiff.label')}</Label>
        {rows.map((row) => (
          <div key={row.id} className={styles.diffRow}>
            <span className={styles.diffLabel}>{row.label}</span>
            <span className={styles.diffValues} aria-hidden="true">
              <del className={styles.diffFrom}>{row.from}</del>
              <Icon icon={ArrowRight} size={12} />
              <ins className={styles.diffTo}>{row.to}</ins>
              {row.delta ? <span className={styles.diffDelta}>({row.delta})</span> : null}
            </span>
            <span className="ds-visually-hidden">
              {t('approveTheDiff.change', { from: row.from, to: row.to })}
            </span>
            <CheckboxField
              value={row.id}
              className={cx(styles.diffCheckField)}
              aria-label={t('approveTheDiff.rowLabel', {
                label: row.label,
                from: row.from,
                to: row.to,
              })}
            >
              <CheckboxButton className={cx(styles.diffCheck)}>
                {({ isSelected }) => (
                  <span
                    className={styles.box}
                    data-checked={isSelected || undefined}
                    aria-hidden="true"
                  >
                    {isSelected ? <Icon icon={Check} size={12} /> : null}
                  </span>
                )}
              </CheckboxButton>
            </CheckboxField>
          </div>
        ))}
      </CheckboxGroup>
      <div className={styles.footer}>
        <Button
          variant={neutral ? 'primary' : 'ai'}
          size="sm"
          {...(count === 0 ? { disabledReason: t('approveTheDiff.selectOne') } : {})}
          onPress={() => {
            onAccept(selected);
          }}
        >
          {acceptLabel ?? t('approveTheDiff.accept', { count })}
        </Button>
        {onEdit ? (
          <Button variant="secondary" size="sm" onPress={onEdit}>
            {t('aiSuggestion.edit')}
          </Button>
        ) : null}
        {onWhy ? (
          <Button variant="link" size="sm" onPress={onWhy}>
            {t('aiSuggestion.why')}
          </Button>
        ) : null}
      </div>
    </div>
  );
}
