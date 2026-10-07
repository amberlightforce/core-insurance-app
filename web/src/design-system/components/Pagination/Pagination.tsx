import { ChevronDown, ChevronLeft, ChevronRight } from 'lucide-react';
import type { KeyboardEvent } from 'react';
import {
  Button as AriaButton,
  Label,
  ListBox,
  ListBoxItem,
  Popover,
  Select,
  SelectValue,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { formatInteger } from '../../../format/numbers';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { pageCountFor, pageRange, pageSlots } from './pages';
import styles from './Pagination.module.css';

export interface PaginationProps {
  /** 1-based current page. */
  page: number;
  pageSize: number;
  /** Total matching records. */
  total: number;
  onPageChange: (page: number) => void;
  /** Shows the page size select when given. */
  onPageSizeChange?: (pageSize: number) => void;
  pageSizeOptions?: readonly number[];
  /** Landmark name (default «Σελιδοποίηση»). */
  'aria-label'?: string;
}

/** Arrow keys move between the buttons of the joined group (they also stay in the Tab order). */
function onGroupKeyDown(event: KeyboardEvent<HTMLDivElement>) {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
  const buttons = Array.from(
    event.currentTarget.querySelectorAll<HTMLButtonElement>('button:not([disabled])'),
  );
  const index = buttons.indexOf(event.target as HTMLButtonElement);
  if (index === -1) return;
  const next = buttons[index + (event.key === 'ArrowRight' ? 1 : -1)];
  if (next) {
    event.preventDefault();
    next.focus();
  }
}

/**
 * Classic pagination (Part 2 §4.35 virtualisation alternative): «1–50 από 4.812» with el-GR grouping, page
 * size 25/50/100, previous/next and page buttons as a joined secondary group, `aria-current="page"`.
 */
export function Pagination({
  page,
  pageSize,
  total,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = [25, 50, 100],
  ...rest
}: PaginationProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const pageCount = pageCountFor(total, pageSize);
  const current = Math.min(Math.max(1, page), pageCount);
  const [start, end] = pageRange(current, pageSize, total);
  const go = (target: number) => {
    const clamped = Math.min(Math.max(1, target), pageCount);
    if (clamped !== current) onPageChange(clamped);
  };

  return (
    <nav aria-label={rest['aria-label'] ?? t('pagination.label')} className={styles.pagination}>
      <span className={styles.range}>
        {t('pagination.range', {
          start: formatInteger(start, region),
          end: formatInteger(end, region),
          total: formatInteger(total, region),
        })}
      </span>
      {onPageSizeChange ? (
        <Select
          className={cx(styles.pageSize)}
          value={pageSize}
          onChange={(key: Key | null) => {
            if (key !== null) onPageSizeChange(Number(key));
          }}
        >
          <Label className={cx(styles.pageSizeLabel)}>{t('pagination.pageSize')}</Label>
          <AriaButton className={cx(styles.selectButton)}>
            <SelectValue />
            <Icon icon={ChevronDown} size={14} />
          </AriaButton>
          <Popover className={cx(styles.popover)} data-material="popover">
            <ListBox className={cx(styles.listBox)}>
              {pageSizeOptions.map((size) => (
                <ListBoxItem
                  key={size}
                  id={size}
                  className={cx(styles.option)}
                  textValue={formatInteger(size, region)}
                >
                  {formatInteger(size, region)}
                </ListBoxItem>
              ))}
            </ListBox>
          </Popover>
        </Select>
      ) : null}
      <div
        className={styles.group}
        role="group"
        aria-label={t('pagination.pages')}
        onKeyDown={onGroupKeyDown}
      >
        <AriaButton
          className={cx(styles.button)}
          aria-label={t('pagination.previous')}
          isDisabled={current <= 1}
          onPress={() => {
            go(current - 1);
          }}
        >
          <Icon icon={ChevronLeft} size={16} />
        </AriaButton>
        {pageSlots(current, pageCount).map((slot) =>
          slot.kind === 'gap' ? (
            <span key={slot.key} className={styles.gap} aria-hidden="true">
              …
            </span>
          ) : (
            <AriaButton
              key={slot.page}
              className={cx(styles.button)}
              aria-label={t('pagination.page', { page: formatInteger(slot.page, region) })}
              {...(slot.page === current ? { 'aria-current': 'page' as const } : {})}
              data-current={slot.page === current || undefined}
              onPress={() => {
                go(slot.page);
              }}
            >
              {formatInteger(slot.page, region)}
            </AriaButton>
          ),
        )}
        <AriaButton
          className={cx(styles.button)}
          aria-label={t('pagination.next')}
          isDisabled={current >= pageCount}
          onPress={() => {
            go(current + 1);
          }}
        >
          <Icon icon={ChevronRight} size={16} />
        </AriaButton>
      </div>
    </nav>
  );
}
