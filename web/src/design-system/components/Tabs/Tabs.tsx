import { ChevronDown } from 'lucide-react';
import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import {
  Button as AriaButton,
  Menu,
  MenuItem,
  MenuTrigger,
  Popover,
  SelectionIndicator,
  Tab,
  TabList,
  TabPanel,
  Tabs as AriaTabs,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Badge } from '../StatusPill/Badge';
import { AnchoredTooltip } from '../TextField';
import styles from './Tabs.module.css';
import { adjacentTab, fitTabs, isTabSelectable, splitTabs, type TabItem } from './tabsLayout';

export type TabsVariant = 'line' | 'contained';

export interface TabsProps {
  items: readonly TabItem[];
  'aria-label': string;
  /** `line` for record views; `contained` inside cards and panels. */
  variant?: TabsVariant;
  selectedKey?: string;
  defaultSelectedKey?: string;
  onSelectionChange?: (id: string) => void;
  /**
   * Arrow keys move focus only and Enter/Space activate. Use for panels that take more than 300 ms to
   * render (Part 2 §4.11). The default is automatic activation.
   */
  manual?: boolean;
  /**
   * Upper bound for the visible tabs; the rest go into «Περισσότερα ▾». The strip also measures itself
   * (ResizeObserver), so this is mainly for tests and fixed layouts.
   */
  maxVisible?: number;
  /** Panel content for a tab. */
  children: (item: TabItem) => ReactNode;
}

interface TabButtonProps {
  item: TabItem;
}

function TabButton({ item }: TabButtonProps) {
  const { t } = useTranslation('ds');
  const anchorRef = useRef<HTMLSpanElement | null>(null);
  const reasonId = useId();
  const reason = item.disabledReason;
  const errors = item.errorCount ?? 0;
  const name = [
    item.label,
    item.count !== undefined ? t('tabs.countSuffix', { count: item.count }) : '',
    errors > 0 ? t('tabs.errorSuffix', { count: errors }) : '',
  ].join('');

  return (
    <Tab
      id={item.id}
      className={cx(styles.tab)}
      aria-label={name}
      data-tab-id={item.id}
      data-soft-disabled={reason ? true : undefined}
      data-has-error={errors > 0 || undefined}
      {...(reason ? { 'aria-describedby': reasonId } : {})}
      {...(item.isDisabled ? { isDisabled: true } : {})}
    >
      {({ isHovered, isFocusVisible }) => (
        <>
          <span
            ref={(el) => {
              anchorRef.current = el;
              // React Aria only knows hard-disabled tabs; a tab with a reason stays focusable (D-FE-08).
              const tab = el?.closest('[role="tab"]');
              if (reason) tab?.setAttribute('aria-disabled', 'true');
              else tab?.removeAttribute('aria-disabled');
            }}
            className={styles.labelWrap}
          >
            <span className={styles.label}>{item.label}</span>
            <span className={styles.labelReserve} aria-hidden="true">
              {item.label}
            </span>
          </span>
          {item.count !== undefined ? <Badge count={item.count} /> : null}
          {errors > 0 ? <span className={styles.errorDot} aria-hidden="true" /> : null}
          <SelectionIndicator className={cx(styles.indicator)} />
          {reason ? (
            <>
              <span id={reasonId} hidden>
                {reason}
              </span>
              <AnchoredTooltip triggerRef={anchorRef} isOpen={isHovered || isFocusVisible}>
                {reason}
              </AnchoredTooltip>
            </>
          ) : null}
        </>
      )}
    </Tab>
  );
}

function firstSelectable(items: readonly TabItem[]): string | null {
  return (items.find(isTabSelectable) ?? items[0])?.id ?? null;
}

/**
 * Tabs (Part 2 §4.11) on RAC Tabs: sliding 2 px indicator (MI-12), count badge, error dot with the count
 * in the accessible name, selected label width reserved, overflow into «Περισσότερα ▾» (never scroll; the
 * selected tab stays visible), Ctrl+PgUp/PgDn from inside the panel, disabled tabs with a reason.
 * URL sync: `useTabSearchParam('tab')`.
 */
export function Tabs({
  items,
  variant = 'line',
  selectedKey,
  defaultSelectedKey,
  onSelectionChange,
  manual = false,
  maxVisible,
  children,
  ...rest
}: TabsProps) {
  const { t } = useTranslation('ds');
  const [uncontrolled, setUncontrolled] = useState<string | null>(
    defaultSelectedKey ?? firstSelectable(items),
  );
  const selected = selectedKey ?? uncontrolled;
  const [measured, setMeasured] = useState<number>(items.length);
  const focusRequest = useRef<string | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const stripRef = useRef<HTMLDivElement>(null);
  const measureRef = useRef<HTMLDivElement>(null);

  const select = (id: string) => {
    const item = items.find((candidate) => candidate.id === id);
    if (!item || !isTabSelectable(item)) return;
    setUncontrolled(id);
    if (id !== selected) onSelectionChange?.(id);
  };

  // Measure the strip and each tab's natural width; recompute when the strip resizes.
  useLayoutEffect(() => {
    const strip = stripRef.current;
    const measure = measureRef.current;
    if (!strip || !measure || typeof ResizeObserver === 'undefined') return;
    const update = () => {
      const available = strip.getBoundingClientRect().width;
      if (available <= 0) return;
      const children = Array.from(measure.children) as HTMLElement[];
      const more = children.at(-1);
      const widths = children.slice(0, -1).map((el) => el.getBoundingClientRect().width);
      setMeasured(fitTabs(widths, available, more?.getBoundingClientRect().width ?? 0));
    };
    const observer = new ResizeObserver(update);
    observer.observe(strip);
    return () => {
      observer.disconnect();
    };
  }, [items]);

  // Ctrl+PgUp/PgDn moves focus to the newly selected tab once it has rendered.
  useEffect(() => {
    const id = focusRequest.current;
    if (id === null) return;
    focusRequest.current = null;
    rootRef.current
      ?.querySelector<HTMLElement>(`[role="tab"][data-tab-id="${CSS.escape(id)}"]`)
      ?.focus();
  });

  const visibleCount = Math.min(measured, maxVisible ?? items.length);
  const { visible, overflow } = splitTabs(items, visibleCount, selected);

  const onPanelKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!event.ctrlKey || (event.key !== 'PageDown' && event.key !== 'PageUp')) return;
    const next = adjacentTab(items, selected, event.key === 'PageDown' ? 1 : -1);
    if (!next) return;
    event.preventDefault();
    focusRequest.current = next.id;
    select(next.id);
  };

  return (
    <div ref={rootRef} className={styles.root} data-variant={variant}>
      <AriaTabs
        className={cx(styles.tabs)}
        {...(selected === null ? {} : { selectedKey: selected })}
        onSelectionChange={(key: Key) => {
          select(String(key));
        }}
        keyboardActivation={manual ? 'manual' : 'automatic'}
      >
        <div ref={stripRef} className={styles.strip}>
          <TabList aria-label={rest['aria-label']} items={visible} className={cx(styles.tabList)}>
            {(item) => <TabButton item={item} />}
          </TabList>
          {overflow.length > 0 ? (
            <MenuTrigger>
              <AriaButton className={cx(styles.more)} aria-label={t('tabs.moreWithSelection')}>
                {t('tabs.more')}
                <Icon icon={ChevronDown} size={14} />
              </AriaButton>
              <Popover
                className={cx(styles.popover)}
                placement="bottom end"
                data-material="popover"
              >
                <Menu
                  className={cx(styles.menu)}
                  items={overflow}
                  onAction={(key) => {
                    select(String(key));
                  }}
                  disabledKeys={overflow.filter((item) => !isTabSelectable(item)).map((i) => i.id)}
                >
                  {(item) => (
                    <MenuItem
                      id={item.id}
                      className={cx(styles.menuItem)}
                      textValue={item.label}
                      data-has-error={(item.errorCount ?? 0) > 0 || undefined}
                    >
                      <span className={styles.menuLabel}>{item.label}</span>
                      {item.count !== undefined ? <Badge count={item.count} /> : null}
                      {(item.errorCount ?? 0) > 0 ? (
                        <>
                          <span className={styles.errorDot} aria-hidden="true" />
                          <span className="ds-visually-hidden">
                            {t('tabs.errorSuffix', { count: item.errorCount ?? 0 })}
                          </span>
                        </>
                      ) : null}
                    </MenuItem>
                  )}
                </Menu>
              </Popover>
            </MenuTrigger>
          ) : null}
          {/* Measuring copy: natural widths of every tab (selected weight) and of the More button. */}
          <div ref={measureRef} className={styles.measure} aria-hidden="true">
            {items.map((item) => (
              <span key={item.id} className={styles.tab} data-measure>
                <span className={styles.labelWrap}>
                  <span className={styles.labelReserve}>{item.label}</span>
                </span>
                {item.count !== undefined ? <Badge count={item.count} /> : null}
                {(item.errorCount ?? 0) > 0 ? <span className={styles.errorDot} /> : null}
              </span>
            ))}
            <span className={styles.more}>
              {t('tabs.more')}
              <Icon icon={ChevronDown} size={14} />
            </span>
          </div>
        </div>
        <div className={styles.panels} onKeyDown={onPanelKeyDown}>
          {items.map((item) => (
            <TabPanel key={item.id} id={item.id} className={cx(styles.panel)}>
              {children(item)}
            </TabPanel>
          ))}
        </div>
      </AriaTabs>
    </div>
  );
}
