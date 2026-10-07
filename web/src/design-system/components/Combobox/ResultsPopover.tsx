import { Check, Plus, RotateCcw } from 'lucide-react';
import { Fragment, use, useEffect, type RefObject } from 'react';
import {
  ComboBoxStateContext,
  Header,
  ListBox,
  ListBoxItem,
  ListBoxSection,
  Popover,
} from 'react-aria-components';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import styles from './Combobox.module.css';
import { OptionContent } from './OptionContent';
import { CREATE_KEY, RETRY_KEY, type ComboboxOption, type OptionSection } from './options';

export interface ResultsFooter {
  kind: 'create' | 'retry';
  label: string;
  /** Header text above the action (the empty or error message). */
  header?: string | undefined;
}

export interface ResultsPopoverProps<T extends ComboboxOption> {
  sections: readonly OptionSection<T>[];
  footer?: ResultsFooter | undefined;
  /** Shown when there are no rows at all (loading, too short, no results). */
  emptyText: string;
  /** Older results stay visible, dimmed to 60 %, while a newer query loads. */
  isStale?: boolean;
  isBusy?: boolean;
  /** The 2 px indeterminate bar (only after 150 ms of loading). */
  showBar?: boolean;
  /** Increment to close the popover (after a create action). */
  closeSignal?: number;
  /** Multi-select: a check box-like indicator before each option. */
  showSelection?: boolean;
  /** Anchor the list to this element instead of the combobox input group. */
  triggerRef?: RefObject<Element | null>;
}

/** Closes the combobox popover when `signal` changes (React Aria has no controlled open state for it). */
function CloseOnSignal({ signal }: { signal: number }) {
  const state = use(ComboBoxStateContext);
  useEffect(() => {
    if (signal > 0) state?.close();
  }, [signal, state]);
  return null;
}

/** The combobox results: glass popover, grouped listbox, create / retry footer, empty and loading states. */
export function ResultsPopover<T extends ComboboxOption>({
  sections,
  footer,
  emptyText,
  isStale = false,
  isBusy = false,
  showBar = false,
  closeSignal = 0,
  showSelection = false,
  triggerRef,
}: ResultsPopoverProps<T>) {
  const renderHits = (section: OptionSection<T>) =>
    section.hits.map((hit) => (
      <ListBoxItem
        key={String(hit.item.id)}
        id={hit.item.id}
        textValue={hit.item.label}
        className={cx(styles.option)}
        isDisabled={hit.item.isDisabled ?? false}
      >
        {({ isSelected }) => (
          <>
            {showSelection ? (
              <span className={styles.check} data-checked={isSelected || undefined}>
                {isSelected ? <Icon icon={Check} size={12} /> : null}
              </span>
            ) : null}
            <OptionContent hit={hit} />
          </>
        )}
      </ListBoxItem>
    ));

  return (
    <>
      <CloseOnSignal signal={closeSignal} />
      <Popover
        className={cx(styles.popover)}
        data-material="popover"
        placement="bottom start"
        offset={4}
        {...(triggerRef ? { triggerRef } : {})}
      >
        {showBar ? <div className={styles.loadingBar} aria-hidden="true" /> : null}
        <div
          className={styles.results}
          data-stale={isStale || undefined}
          {...(isBusy ? { 'aria-busy': true } : {})}
        >
          <ListBox
            className={cx(styles.listbox)}
            renderEmptyState={() => <div className={styles.state}>{emptyText}</div>}
          >
            {sections.map((section) =>
              section.title === undefined ? (
                <Fragment key="__ungrouped">{renderHits(section)}</Fragment>
              ) : (
                <ListBoxSection
                  key={section.key}
                  id={`section-${section.key}`}
                  className={cx(styles.section)}
                >
                  <Header className={cx(styles.sectionHeader)}>{section.title}</Header>
                  {renderHits(section)}
                </ListBoxSection>
              ),
            )}
            {footer ? (
              <ListBoxSection id="__ds-footer" className={cx(styles.footer)}>
                {footer.header ? (
                  <Header className={cx(styles.footerHeader)}>{footer.header}</Header>
                ) : null}
                <ListBoxItem
                  id={footer.kind === 'create' ? CREATE_KEY : RETRY_KEY}
                  textValue={footer.label}
                  className={cx(styles.option, styles.action)}
                >
                  <Icon icon={footer.kind === 'create' ? Plus : RotateCcw} size={16} />
                  <span className={styles.optionLabel}>{footer.label}</span>
                </ListBoxItem>
              </ListBoxSection>
            ) : null}
          </ListBox>
        </div>
      </Popover>
    </>
  );
}
