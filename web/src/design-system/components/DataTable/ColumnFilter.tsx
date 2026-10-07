import type { Column } from '@tanstack/react-table';
import { Check, ListFilter } from 'lucide-react';
import {
  Button as AriaButton,
  CheckboxButton,
  CheckboxField,
  CheckboxGroup,
  Dialog,
  DialogTrigger,
  Input,
  Label,
  Popover,
  TextField,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import styles from './DataTable.module.css';

export interface ColumnFilterProps<TData> {
  column: Column<TData>;
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
}

/** Header filter (Part 2 §4.35): text «Περιέχει» or an enum checklist, in a popover. */
export function ColumnFilter<TData>({ column, isOpen, onOpenChange }: ColumnFilterProps<TData>) {
  const { t } = useTranslation('ds');
  const meta = column.columnDef.meta;
  const label = meta?.label ?? column.id;
  const value = column.getFilterValue();
  const active = value !== undefined;

  return (
    <DialogTrigger isOpen={isOpen} onOpenChange={onOpenChange}>
      <AriaButton
        className={cx(styles.headerIconButton)}
        aria-label={t('dataTable.filterButton', { column: label })}
        data-active={active || undefined}
      >
        <Icon icon={ListFilter} size={14} />
      </AriaButton>
      <Popover className={cx(styles.popover)} placement="bottom start" data-material="popover">
        <Dialog
          className={cx(styles.dialog)}
          aria-label={t('dataTable.filterButton', { column: label })}
        >
          {meta?.filter === 'enum' ? (
            <CheckboxGroup
              className={cx(styles.filterGroup)}
              value={Array.isArray(value) ? (value as string[]) : []}
              onChange={(values) => {
                column.setFilterValue(values.length > 0 ? values : undefined);
              }}
            >
              <Label className={cx(styles.filterLabel)}>{t('dataTable.filterValues')}</Label>
              {(meta.enumOptions ?? []).map((option) => (
                <CheckboxField
                  key={option.value}
                  value={option.value}
                  className={cx(styles.checkboxField)}
                >
                  <CheckboxButton className={cx(styles.checkbox)}>
                    {({ isSelected }) => (
                      <>
                        <span className={styles.checkboxBox} aria-hidden="true">
                          {isSelected ? <Icon icon={Check} size={12} /> : null}
                        </span>
                        {option.label}
                      </>
                    )}
                  </CheckboxButton>
                </CheckboxField>
              ))}
            </CheckboxGroup>
          ) : (
            <TextField
              className={cx(styles.filterGroup)}
              value={typeof value === 'string' ? value : ''}
              onChange={(text) => {
                column.setFilterValue(text === '' ? undefined : text);
              }}
              autoFocus
            >
              <Label className={cx(styles.filterLabel)}>{t('dataTable.filterContains')}</Label>
              <Input className={cx(styles.input)} />
            </TextField>
          )}
          <Button
            variant="link"
            size="sm"
            onPress={() => {
              column.setFilterValue(undefined);
            }}
          >
            {t('dataTable.filterClear')}
          </Button>
        </Dialog>
      </Popover>
    </DialogTrigger>
  );
}
