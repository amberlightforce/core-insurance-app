import { ChevronDown } from 'lucide-react';
import type { Key } from 'react-aria-components';
import {
  Button as AriaButton,
  Label,
  ListBox,
  ListBoxItem,
  Popover as AriaPopover,
  Select,
  SelectValue,
} from 'react-aria-components';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import styles from './Dialog.module.css';

export interface ReasonOption {
  id: string;
  label: string;
}

export interface ReasonSelectProps {
  label: string;
  placeholder: string;
  options: ReasonOption[];
  selectedKey: string | null;
  onSelectionChange: (key: string | null) => void;
}

/**
 * Minimal required reason select for confirmations (RAC `Select`), kept private to the dialog so the
 * confirmation does not depend on the form components. Its list uses the solid popover material.
 */
export function ReasonSelect({
  label,
  placeholder,
  options,
  selectedKey,
  onSelectionChange,
}: ReasonSelectProps) {
  return (
    <Select
      className={cx(styles.field)}
      isRequired
      placeholder={placeholder}
      value={selectedKey}
      onChange={(key: Key | null) => {
        onSelectionChange(key === null ? null : String(key));
      }}
    >
      <Label className={cx(styles.fieldLabel)}>{label}</Label>
      <AriaButton className={cx(styles.selectButton)}>
        <SelectValue className={cx(styles.selectValue)} />
        <Icon icon={ChevronDown} size={16} />
      </AriaButton>
      <AriaPopover
        className={cx(styles.listPopover)}
        data-material="popover"
        data-solid="true"
        offset={4}
      >
        <ListBox className={cx(styles.listBox)} items={options}>
          {(option) => (
            <ListBoxItem id={option.id} className={cx(styles.option)} textValue={option.label}>
              {option.label}
            </ListBoxItem>
          )}
        </ListBox>
      </AriaPopover>
    </Select>
  );
}
