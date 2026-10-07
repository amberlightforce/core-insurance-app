import type { LucideIcon } from 'lucide-react';
import { CircleAlert, Lock, X } from 'lucide-react';
import { useRef, useState } from 'react';
import { Button as AriaButton, Tag, TagGroup, TagList, type Key } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { AnchoredTooltip } from '../TextField';
import { chipText, isEditable, isRemovable, type FilterChipItem } from './chipText';
import styles from './Tags.module.css';

export interface FilterChipsProps {
  items: readonly FilterChipItem[];
  /** Accessible name of the chip group (default «Ενεργά φίλτρα»). */
  'aria-label'?: string;
  /** Delete/Backspace or ×. Never called for locked or read-only chips. */
  onRemove?: (id: string) => void;
  /** Enter or click: reopen the chip's filter popover. Not offered on locked or read-only chips. */
  onEdit?: (id: string) => void;
}

export interface FilterChipProps {
  item: FilterChipItem;
  /** Shows the × button (the group must have `onRemove`). */
  removable?: boolean;
  onAction?: () => void;
}

/** One chip: brand family, «Πεδίο: τιμή, τιμή», values truncated after 32 characters with a tooltip. */
export function FilterChip({ item, removable = false, onAction }: FilterChipProps) {
  const { t } = useTranslation('ds');
  const labelRef = useRef<HTMLSpanElement>(null);
  const [hovered, setHovered] = useState(false);
  const text = chipText(item);
  const full = item.field ? t('tags.label', { field: item.field, values: text.full }) : text.full;
  const shown = item.field
    ? t('tags.label', { field: item.field, values: text.shown })
    : text.shown;
  const state = item.state ?? 'default';
  const leadingIcon: LucideIcon | undefined =
    state === 'locked' ? Lock : state === 'invalid' ? CircleAlert : item.icon;
  const needsTooltip = text.truncated || state === 'invalid';
  const tooltip = state === 'invalid' ? `${full} · ${t('tags.invalid')}` : full;
  const name =
    state === 'invalid'
      ? `${full}, ${t('tags.invalid')}`
      : state === 'locked'
        ? `${full}, ${t('tags.locked')}`
        : full;

  return (
    <Tag
      id={item.id}
      textValue={name}
      className={cx(styles.chip)}
      data-state={state}
      data-editable={onAction ? true : undefined}
      onHoverStart={() => {
        setHovered(true);
      }}
      onHoverEnd={() => {
        setHovered(false);
      }}
      {...(onAction ? { onAction } : {})}
    >
      {({ isFocusVisible }) => (
        <>
          {leadingIcon ? (
            <span className={styles.icon}>
              <Icon icon={leadingIcon} size={12} />
            </span>
          ) : null}
          <span ref={labelRef} className={styles.label}>
            <span aria-hidden={text.truncated ? true : undefined}>{shown}</span>
            {text.truncated ? <span className="ds-visually-hidden">{full}</span> : null}
            {state === 'invalid' ? (
              <span className="ds-visually-hidden">, {t('tags.invalid')}</span>
            ) : null}
            {state === 'locked' ? (
              <span className="ds-visually-hidden">, {t('tags.locked')}</span>
            ) : null}
          </span>
          {removable ? (
            <AriaButton slot="remove" className={cx(styles.remove)} aria-label={t('tags.remove')}>
              <Icon icon={X} size={12} />
            </AriaButton>
          ) : null}
          {needsTooltip ? (
            <AnchoredTooltip
              triggerRef={labelRef}
              isOpen={hovered || isFocusVisible}
              placement="bottom"
            >
              {tooltip}
            </AnchoredTooltip>
          ) : null}
        </>
      )}
    </Tag>
  );
}

/**
 * Filter chips (Part 2 §4.14) on RAC TagGroup. Enter edits (reopens the filter popover), Delete/Backspace
 * removes; locked ABAC filters show a lock and no ×; an invalid saved filter turns danger with
 * «Το πεδίο δεν υπάρχει πλέον»; read-only chips are neutral with no ×. MI-25 chip in.
 */
export function FilterChips({ items, onRemove, onEdit, ...rest }: FilterChipsProps) {
  const { t } = useTranslation('ds');
  const byId = new Map(items.map((item) => [item.id, item]));

  return (
    <TagGroup
      aria-label={rest['aria-label'] ?? t('tags.groupLabel')}
      className={cx(styles.group)}
      {...(onRemove
        ? {
            onRemove: (keys: Set<Key>) => {
              for (const key of keys) {
                const item = byId.get(String(key));
                if (item && isRemovable(item)) onRemove(item.id);
              }
            },
          }
        : {})}
    >
      <TagList items={items} className={cx(styles.list)}>
        {(item) => (
          <FilterChip
            item={item}
            removable={onRemove !== undefined && isRemovable(item)}
            {...(onEdit && isEditable(item)
              ? {
                  onAction: () => {
                    onEdit(item.id);
                  },
                }
              : {})}
          />
        )}
      </TagList>
    </TagGroup>
  );
}
