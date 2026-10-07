import type { CalendarDate, DateValue } from '@internationalized/date';
import { use, useId, type ReactNode } from 'react';
import {
  Button as AriaButton,
  DatePickerStateContext,
  DateRangePickerStateContext,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { cx } from '../../utils/cx';
import { FieldChromeLabel } from '../FieldChrome';
import fieldStyles from '../FieldChrome/FieldChrome.module.css';
import {
  RANGE_QUICK_PICKS,
  rangeQuickPickDates,
  SINGLE_QUICK_PICKS,
  singleQuickPickDate,
  type RangeQuickPick,
  type SingleQuickPick,
} from './dateHelpers';
import styles from './DatePicker.module.css';

export interface ExtraQuickPick {
  label: string;
  date: CalendarDate;
}

function isOutside(
  date: CalendarDate,
  minValue: DateValue | undefined,
  maxValue: DateValue | undefined,
  isDateUnavailable: ((date: DateValue) => boolean) | undefined,
): boolean {
  if (minValue && date.compare(minValue) < 0) return true;
  if (maxValue && date.compare(maxValue) > 0) return true;
  return isDateUnavailable?.(date) ?? false;
}

export interface SingleQuickPicksProps {
  today: CalendarDate;
  onPick: (date: CalendarDate) => void;
  extra?: readonly ExtraQuickPick[] | undefined;
  minValue?: DateValue | undefined;
  maxValue?: DateValue | undefined;
  isDateUnavailable?: ((date: DateValue) => boolean) | undefined;
  footerNote?: ReactNode;
}

type Translate = ReturnType<typeof useTranslation>['t'];

function singleLabel(t: Translate, pick: SingleQuickPick): string {
  switch (pick) {
    case 'today':
      return t('datePicker.quickToday');
    case 'tomorrow':
      return t('datePicker.quickTomorrow');
    case 'firstOfNextMonth':
      return t('datePicker.quickFirstOfNextMonth');
  }
}

function rangeLabel(t: Translate, pick: RangeQuickPick): string {
  switch (pick) {
    case 'last7Days':
      return t('dateRangePicker.quickLast7Days');
    case 'currentMonth':
      return t('dateRangePicker.quickCurrentMonth');
    case 'previousQuarter':
      return t('dateRangePicker.quickPreviousQuarter');
    case 'yearToDate':
      return t('dateRangePicker.quickYearToDate');
  }
}

/** Footer of the single-date calendar: Σήμερα, Αύριο, 1η επόμενου μήνα (+ extra picks), business-day note. */
export function SingleQuickPicks({
  today,
  onPick,
  extra,
  minValue,
  maxValue,
  isDateUnavailable,
  footerNote,
}: SingleQuickPicksProps) {
  const { t } = useTranslation('ds');
  const state = use(DatePickerStateContext);
  const picks = [
    ...SINGLE_QUICK_PICKS.map((pick) => ({
      key: pick,
      label: singleLabel(t, pick),
      date: singleQuickPickDate(pick, today),
    })),
    ...(extra ?? []).map((p, i) => ({ key: `extra-${String(i)}`, label: p.label, date: p.date })),
  ];
  return (
    <div className={styles.footer}>
      <div className={styles.quickPicks} role="group" aria-label={t('datePicker.quickPicks')}>
        {picks.map((pick) => {
          const disabled = isOutside(pick.date, minValue, maxValue, isDateUnavailable);
          return (
            <AriaButton
              key={pick.key}
              className={cx(styles.quickPick)}
              isDisabled={disabled}
              onPress={() => {
                onPick(pick.date);
                state?.close();
              }}
            >
              {pick.label}
            </AriaButton>
          );
        })}
      </div>
      {footerNote ? <div className={styles.footerNote}>{footerNote}</div> : null}
    </div>
  );
}

export interface RangeQuickPicksProps {
  today: CalendarDate;
  onPick: (range: { start: CalendarDate; end: CalendarDate }) => void;
  footerNote?: ReactNode;
}

/** Footer of the range calendar: Τελευταίες 7 ημέρες, Τρέχων μήνας, Προηγούμενο τρίμηνο, Από αρχή έτους. */
export function RangeQuickPicks({ today, onPick, footerNote }: RangeQuickPicksProps) {
  const { t } = useTranslation('ds');
  const state = use(DateRangePickerStateContext);
  return (
    <div className={styles.footer}>
      <div className={styles.quickPicks} role="group" aria-label={t('datePicker.quickPicks')}>
        {RANGE_QUICK_PICKS.map((pick) => (
          <AriaButton
            key={pick}
            className={cx(styles.quickPick)}
            onPress={() => {
              onPick(rangeQuickPickDates(pick, today));
              state?.close();
            }}
          >
            {rangeLabel(t, pick)}
          </AriaButton>
        ))}
      </div>
      {footerNote ? <div className={styles.footerNote}>{footerNote}</div> : null}
    </div>
  );
}

export interface ReadOnlyDateProps {
  label: string;
  text: string;
  description?: string | undefined;
  className?: string | undefined;
}

/** Read-only date: the long format as text, no field chrome (Part 2 §4.5). */
export function ReadOnlyDate({ label, text, description, className }: ReadOnlyDateProps) {
  const { t } = useTranslation('ds');
  const labelId = useId();
  const descriptionId = useId();
  return (
    <div className={cx(fieldStyles.field, className)}>
      <FieldChromeLabel id={labelId}>{label}</FieldChromeLabel>
      <div
        role="textbox"
        aria-readonly="true"
        aria-labelledby={labelId}
        {...(description ? { 'aria-describedby': descriptionId } : {})}
        tabIndex={0}
        className={cx(fieldStyles.frame, styles.readOnly)}
        data-readonly
      >
        {text === '' ? <span className={styles.empty}>{t('datePicker.empty')}</span> : text}
      </div>
      {description ? (
        <div id={descriptionId} className={cx(fieldStyles.message, fieldStyles.helper)}>
          {description}
        </div>
      ) : null}
    </div>
  );
}
