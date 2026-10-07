import {
  isSameDay,
  isToday,
  isWeekend,
  type CalendarDate,
  type DateValue,
} from '@internationalized/date';
import { ChevronDown, ChevronLeft, ChevronRight } from 'lucide-react';
import { use, useRef, useState, type ContextType, type KeyboardEvent, type ReactNode } from 'react';
import { useCalendarCell, useDateFormatter, useLocale } from 'react-aria';
import {
  Button as AriaButton,
  Calendar,
  CalendarGrid,
  CalendarGridBody,
  CalendarGridHeader,
  CalendarHeaderCell,
  CalendarMonthPicker,
  CalendarStateContext,
  CalendarYearPicker,
  ListBox,
  ListBoxItem,
  RangeCalendar,
  RangeCalendarStateContext,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { ATHENS_ZONE } from './dateHelpers';
import styles from './DatePicker.module.css';

type AnyCalendarState = NonNullable<ContextType<typeof CalendarStateContext>>;
type AnyRangeState = NonNullable<ContextType<typeof RangeCalendarStateContext>>;
type PanelState = AnyCalendarState | AnyRangeState;

export interface DayDecorations {
  /** Holiday names by ISO date: a 4 px info dot and the name in the cell's label and tooltip. */
  holidays: ReadonlyMap<string, string>;
  /** Why a date is unavailable (struck through), for its label and tooltip. */
  unavailableReason?: ((date: CalendarDate) => string | undefined) | undefined;
}

function firstKey(keys: 'all' | Set<Key>): Key | undefined {
  if (keys === 'all') return undefined;
  return [...keys][0];
}

/** A day cell built on `useCalendarCell` so its label can carry the holiday name and unavailable reason. */
function DayCell({
  date,
  state,
  decorations,
}: {
  date: CalendarDate;
  state: PanelState;
  decorations: DayDecorations;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const { locale } = useLocale();
  const cell = useCalendarCell({ date }, state, ref);
  if (cell.isOutsideVisibleRange) return <td {...cell.cellProps} className={styles.cellEmpty} />;

  const holiday = decorations.holidays.get(date.toString());
  const reason = cell.isUnavailable ? decorations.unavailableReason?.(date) : undefined;
  const baseLabel = cell.buttonProps['aria-label'] ?? cell.formattedDate;
  const label = [baseLabel, holiday, reason].filter(Boolean).join(', ');
  const range = 'highlightedRange' in state ? state.highlightedRange : null;
  const inRange = range !== null && date.compare(range.start) >= 0 && date.compare(range.end) <= 0;
  const rangeStart = range !== null && isSameDay(date, range.start);
  const rangeEnd = range !== null && isSameDay(date, range.end);
  const tooltip = [holiday, reason].filter(Boolean).join(' · ');

  return (
    <td {...cell.cellProps} className={styles.cell}>
      <div
        {...cell.buttonProps}
        ref={ref}
        aria-label={label}
        className={styles.day}
        data-selected={(cell.isSelected && !range) || rangeStart || rangeEnd || undefined}
        data-in-range={(inRange && !rangeStart && !rangeEnd) || undefined}
        data-range-start={rangeStart || undefined}
        data-range-end={rangeEnd || undefined}
        data-today={isToday(date, state.timeZone) || undefined}
        data-weekend={isWeekend(date, locale) || undefined}
        data-unavailable={cell.isUnavailable || undefined}
        data-disabled={(cell.isDisabled && !cell.isUnavailable) || undefined}
        data-focused={cell.isFocused || undefined}
        data-pressed={cell.isPressed || undefined}
        data-invalid={cell.isInvalid || undefined}
        data-holiday={holiday !== undefined || undefined}
        {...(tooltip ? { title: tooltip } : {})}
      >
        <span className={styles.dayNumber}>{cell.formattedDate}</span>
        {holiday !== undefined ? <span className={styles.holidayDot} aria-hidden="true" /> : null}
      </div>
    </td>
  );
}

/** Month and year quick picker (title click), built on RAC `CalendarMonthPicker` / `CalendarYearPicker`. */
function MonthYearPicker({ onDone }: { onDone: () => void }) {
  return (
    <div
      className={styles.monthYear}
      onKeyDown={(e: KeyboardEvent<HTMLDivElement>) => {
        if (e.key === 'Escape') {
          e.preventDefault();
          e.stopPropagation();
          onDone();
        }
      }}
    >
      <CalendarMonthPicker format="long">
        {({ 'aria-label': label, value, onChange, items }) => (
          <ListBox
            aria-label={label}
            layout="grid"
            selectionMode="single"
            disallowEmptySelection
            selectedKeys={[value]}
            onSelectionChange={(keys) => {
              const key = firstKey(keys);
              if (key !== undefined) {
                onChange(key);
                onDone();
              }
            }}
            items={items}
            className={cx(styles.monthGrid)}
            autoFocus
          >
            {(item) => (
              <ListBoxItem id={item.id} className={cx(styles.monthItem)}>
                {item.formatted}
              </ListBoxItem>
            )}
          </ListBox>
        )}
      </CalendarMonthPicker>
      <CalendarYearPicker visibleYears={12}>
        {({ 'aria-label': label, value, onChange, items }) => (
          <ListBox
            aria-label={label}
            layout="grid"
            selectionMode="single"
            disallowEmptySelection
            selectedKeys={[value]}
            onSelectionChange={(keys) => {
              const key = firstKey(keys);
              if (key !== undefined) onChange(key);
            }}
            items={items}
            className={cx(styles.yearGrid)}
          >
            {(item) => (
              <ListBoxItem id={item.id} className={cx(styles.monthItem)}>
                {item.formatted}
              </ListBoxItem>
            )}
          </ListBox>
        )}
      </CalendarYearPicker>
    </div>
  );
}

/** Header: ‹ › and the nominative «Οκτώβριος 2026» title, which toggles the month/year picker. */
function PanelBody({
  state,
  decorations,
}: {
  state: PanelState | null;
  decorations: DayDecorations;
}) {
  const { t } = useTranslation('ds');
  const [picking, setPicking] = useState(false);
  const formatter = useDateFormatter({
    month: 'long',
    year: 'numeric',
    timeZone: state?.timeZone ?? ATHENS_ZONE,
  });
  if (!state) return null;
  const title = formatter.format(state.visibleRange.start.toDate(state.timeZone));

  return (
    <>
      <header className={styles.header}>
        <AriaButton slot="previous" className={cx(styles.navButton)}>
          <Icon icon={ChevronLeft} size={16} />
        </AriaButton>
        <AriaButton
          slot={null}
          className={cx(styles.title)}
          aria-label={t('datePicker.chooseMonthYear', { title })}
          aria-expanded={picking}
          onPress={() => {
            setPicking((p) => !p);
          }}
        >
          <span aria-live="polite">{title}</span>
          <Icon icon={ChevronDown} size={14} />
        </AriaButton>
        <AriaButton slot="next" className={cx(styles.navButton)}>
          <Icon icon={ChevronRight} size={16} />
        </AriaButton>
      </header>
      {picking ? (
        <MonthYearPicker
          onDone={() => {
            setPicking(false);
          }}
        />
      ) : (
        <CalendarGrid className={cx(styles.grid)} weekdayStyle="short">
          <CalendarGridHeader>
            {(day) => <CalendarHeaderCell className={cx(styles.weekday)}>{day}</CalendarHeaderCell>}
          </CalendarGridHeader>
          <CalendarGridBody>
            {(date) => <DayCell date={date} state={state} decorations={decorations} />}
          </CalendarGridBody>
        </CalendarGrid>
      )}
    </>
  );
}

function SinglePanelBody({ decorations }: { decorations: DayDecorations }) {
  return <PanelBody state={use(CalendarStateContext)} decorations={decorations} />;
}

function RangePanelBody({ decorations }: { decorations: DayDecorations }) {
  return <PanelBody state={use(RangeCalendarStateContext)} decorations={decorations} />;
}

export interface CalendarPanelProps {
  range?: boolean;
  decorations: DayDecorations;
  isDateUnavailable?: ((date: DateValue) => boolean) | undefined;
  /** Quick picks and the business-day note, under the grid. */
  footer?: ReactNode;
}

/**
 * The calendar popover content (Part 2 §4.5): weekdays Monday first (locale), today ring, selected accent
 * fill, in-range `state.row-selected`, unavailable dates struck through with a reason, holidays with an
 * info dot, weekends in `text.tertiary`. React Aria handles arrows, PgUp/PgDn (month), Shift+PgUp/PgDn
 * (year), Home/End (week bounds) and Enter.
 */
export function CalendarPanel({
  range = false,
  decorations,
  isDateUnavailable,
  footer,
}: CalendarPanelProps) {
  const body = range ? (
    <RangeCalendar className={cx(styles.calendar)} {...defined({ isDateUnavailable })}>
      <RangePanelBody decorations={decorations} />
    </RangeCalendar>
  ) : (
    <Calendar className={cx(styles.calendar)} {...defined({ isDateUnavailable })}>
      <SinglePanelBody decorations={decorations} />
    </Calendar>
  );
  return (
    <div className={styles.panel}>
      {body}
      {footer}
    </div>
  );
}
