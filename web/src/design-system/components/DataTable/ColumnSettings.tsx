import type { Column, Table } from '@tanstack/react-table';
import { ArrowDown, ArrowUp, Check, Columns3Cog } from 'lucide-react';
import { useState } from 'react';
import {
  Button as AriaButton,
  CheckboxButton,
  CheckboxField,
  Dialog,
  DialogTrigger,
  Input,
  Popover,
  SearchField,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { normalizeForSearch } from '../../../format/search';
import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import styles from './DataTable.module.css';

const fixedColumns = new Set(['__select', '__actions']);

function labelOf<TData>(column: Column<TData>): string {
  return column.columnDef.meta?.label ?? column.id;
}

export interface ColumnSettingsProps<TData> {
  table: Table<TData>;
}

/**
 * Column visibility and order (Part 2 §4.35): 320 px popover with search, checkboxes, «Μετακίνηση
 * πάνω/κάτω» buttons (the WCAG 2.5.7 alternative to dragging) and «Επαναφορά προεπιλογών».
 */
export function ColumnSettings<TData>({ table }: ColumnSettingsProps<TData>) {
  const { t } = useTranslation('ds');
  const [query, setQuery] = useState('');
  const all = table.getAllLeafColumns();
  const movable = all.filter((column) => !fixedColumns.has(column.id));
  const needle = normalizeForSearch(query);
  const shown = needle
    ? movable.filter((column) => normalizeForSearch(labelOf(column)).includes(needle))
    : movable;

  const move = (id: string, direction: -1 | 1) => {
    const ids = movable.map((column) => column.id);
    const index = ids.indexOf(id);
    const target = index + direction;
    if (index < 0 || target < 0 || target >= ids.length) return;
    const swapped = ids[target];
    if (swapped === undefined) return;
    ids[target] = id;
    ids[index] = swapped;
    table.setColumnOrder(['__select', ...ids, '__actions']);
  };

  return (
    <DialogTrigger>
      <Button variant="ghost" size="sm" icon={Columns3Cog}>
        {t('dataTable.columnsButton')}
      </Button>
      <Popover
        className={cx(styles.popover, styles.settingsPopover)}
        placement="bottom end"
        data-material="popover"
      >
        <Dialog className={cx(styles.dialog)} aria-label={t('dataTable.columnsTitle')}>
          <SearchField
            aria-label={t('dataTable.columnsSearch')}
            className={cx(styles.popoverSearch)}
            value={query}
            onChange={setQuery}
          >
            <Input className={cx(styles.input)} placeholder={t('dataTable.columnsSearch')} />
          </SearchField>
          <ul className={styles.settingsList}>
            {shown.map((column) => {
              const label = labelOf(column);
              const index = movable.indexOf(column);
              return (
                <li key={column.id} className={styles.settingsItem}>
                  <CheckboxField
                    className={cx(styles.checkboxField)}
                    isSelected={column.getIsVisible()}
                    isDisabled={!column.getCanHide()}
                    onChange={(visible) => {
                      column.toggleVisibility(visible);
                    }}
                  >
                    <CheckboxButton className={cx(styles.checkbox)}>
                      {({ isSelected }) => (
                        <>
                          <span className={styles.checkboxBox} aria-hidden="true">
                            {isSelected ? <Icon icon={Check} size={12} /> : null}
                          </span>
                          <span className={styles.truncate}>{label}</span>
                        </>
                      )}
                    </CheckboxButton>
                  </CheckboxField>
                  <AriaButton
                    className={cx(styles.iconButton)}
                    aria-label={t('dataTable.moveUp', { column: label })}
                    isDisabled={index <= 0}
                    onPress={() => {
                      move(column.id, -1);
                    }}
                  >
                    <Icon icon={ArrowUp} size={14} />
                  </AriaButton>
                  <AriaButton
                    className={cx(styles.iconButton)}
                    aria-label={t('dataTable.moveDown', { column: label })}
                    isDisabled={index >= movable.length - 1}
                    onPress={() => {
                      move(column.id, 1);
                    }}
                  >
                    <Icon icon={ArrowDown} size={14} />
                  </AriaButton>
                </li>
              );
            })}
          </ul>
          <Button
            variant="link"
            size="sm"
            onPress={() => {
              table.resetColumnVisibility();
              table.resetColumnOrder();
            }}
          >
            {t('dataTable.columnsReset')}
          </Button>
        </Dialog>
      </Popover>
    </DialogTrigger>
  );
}
