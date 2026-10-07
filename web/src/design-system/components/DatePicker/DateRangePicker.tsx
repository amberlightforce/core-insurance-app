import { today as todayIn, type CalendarDate, type DateValue } from '@internationalized/date';
import { CalendarDays, CircleAlert } from 'lucide-react';
import { useMemo, type ReactNode } from 'react';
import {
  Button as AriaButton,
  DateInput,
  DateRangePicker as AriaDateRangePicker,
  DateSegment,
  Dialog,
  Group,
  Popover,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { formatDate } from '../../../format/dates';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { useControlledValue } from '../Combobox/useControlledValue';
import { FieldChromeLabel, FieldMessageList, useFieldMessages } from '../FieldChrome';
import fieldStyles from '../FieldChrome/FieldChrome.module.css';
import { CalendarPanel } from './CalendarPanel';
import { ATHENS_ZONE, formatLongDate, holidayMap, type Holiday } from './dateHelpers';
import styles from './DatePicker.module.css';
import { RangeQuickPicks, ReadOnlyDate } from './parts';
import { QuickEntry } from './QuickEntry';
import { useQuickEntry } from './useQuickEntry';

export interface DateRange {
  start: CalendarDate;
  end: CalendarDate;
}

export interface DateRangePickerProps {
  label: string;
  value?: DateRange | null;
  defaultValue?: DateRange | null;
  onChange?: (value: DateRange | null) => void;
  minValue?: DateValue;
  maxValue?: DateValue;
  isDateUnavailable?: (date: DateValue) => boolean;
  unavailableReason?: string | ((date: CalendarDate) => string | undefined);
  holidays?: readonly Holiday[];
  today?: CalendarDate;
  hideQuickPicks?: boolean;
  footerNote?: ReactNode;
  description?: string;
  errorMessage?: string;
  isRequired?: boolean;
  isReadOnly?: boolean;
  isDisabled?: boolean;
  disabledReason?: string;
  startName?: string;
  endName?: string;
  className?: string;
}

/**
 * Date range picker (Part 2 §4.5): two segmented inputs, a range calendar (in-range days in
 * `state.row-selected`) and range quick picks: Τελευταίες 7 ημέρες, Τρέχων μήνας, Προηγούμενο τρίμηνο, Από
 * αρχή έτους. The accelerators work in either input and set that end of the range.
 */
export function DateRangePicker({
  label,
  value: valueProp,
  defaultValue = null,
  onChange,
  minValue,
  maxValue,
  isDateUnavailable,
  unavailableReason,
  holidays,
  hideQuickPicks = false,
  footerNote,
  description,
  errorMessage,
  isRequired = false,
  isReadOnly = false,
  isDisabled = false,
  disabledReason,
  startName,
  endName,
  className,
  ...rest
}: DateRangePickerProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const [value, setValue] = useControlledValue<DateRange | null>(valueProp, defaultValue, onChange);
  const today = rest.today ?? todayIn(ATHENS_ZONE);
  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const quick = useQuickEntry(!isDisabled && !softDisabled && !isReadOnly);
  const decorations = useMemo(
    () => ({
      holidays: holidayMap(holidays),
      unavailableReason: (date: CalendarDate) =>
        typeof unavailableReason === 'function' ? unavailableReason(date) : unavailableReason,
    }),
    [holidays, unavailableReason],
  );

  let ownError: string | undefined;
  if (value) {
    if (value.end.compare(value.start) < 0) ownError = t('dateRangePicker.endBeforeStart');
    else if (minValue && value.start.compare(minValue) < 0)
      ownError = t('datePicker.beforeMin', { date: formatDate(minValue, region) });
    else if (maxValue && value.end.compare(maxValue) > 0)
      ownError = t('datePicker.afterMax', { date: formatDate(maxValue, region) });
  }
  const shownError = errorMessage ?? ownError;
  const helper = description ?? t('dateRangePicker.formatHint');
  const messages = useFieldMessages({
    description: helper,
    errorMessage: shownError,
    disabledReason,
  });
  const isInvalid = messages.showError;

  if (isReadOnly) {
    const text = value
      ? t('dateRangePicker.range', {
          start: formatLongDate(value.start, region),
          end: formatLongDate(value.end, region),
        })
      : '';
    return (
      <ReadOnlyDate
        label={label}
        text={text}
        description={description}
        className={cx(styles.dateField, className)}
      />
    );
  }

  const setEnd = (slot: 'start' | 'end', date: CalendarDate) => {
    const current = value ?? { start: date, end: date };
    setValue({ ...current, [slot]: date });
  };

  return (
    <div
      className={cx(fieldStyles.field, styles.dateField, className)}
      onKeyDownCapture={quick.onKeyDownCapture}
    >
      <AriaDateRangePicker
        className={cx(fieldStyles.field)}
        value={value}
        onChange={(next) => {
          setValue(next ? { start: next.start, end: next.end } : null);
        }}
        isRequired={isRequired}
        isInvalid={isInvalid}
        isDisabled={isDisabled}
        isReadOnly={softDisabled}
        validationBehavior="aria"
        shouldForceLeadingZeros
        {...defined({
          minValue,
          maxValue,
          isDateUnavailable,
          startName,
          endName,
          'aria-describedby': messages.describedBy,
        })}
      >
        <FieldChromeLabel isRequired={isRequired}>{label}</FieldChromeLabel>
        <Group
          className={cx(fieldStyles.frame, styles.frame)}
          isInvalid={isInvalid}
          isDisabled={isDisabled}
          data-soft-disabled={softDisabled || undefined}
        >
          <DateInput slot="start" className={cx(styles.dateInput)} data-quick-slot="start">
            {(segment) => <DateSegment segment={segment} className={cx(styles.segment)} />}
          </DateInput>
          <span className={styles.rangeDash} aria-hidden="true">
            \u2013
          </span>
          <DateInput slot="end" className={cx(styles.dateInput)} data-quick-slot="end">
            {(segment) => <DateSegment segment={segment} className={cx(styles.segment)} />}
          </DateInput>
          {quick.request ? (
            <QuickEntry
              initialText={quick.request.text}
              today={today}
              region={region}
              onCommit={(date) => {
                setEnd(quick.request?.slot === 'end' ? 'end' : 'start', date);
                quick.close(true);
              }}
              onCancel={(reason) => {
                quick.close(reason === 'escape');
              }}
            />
          ) : null}
          {isInvalid ? (
            <span className={fieldStyles.statusIcon}>
              <Icon icon={CircleAlert} size={16} />
            </span>
          ) : null}
          <AriaButton className={cx(fieldStyles.fieldButton, fieldStyles.hideWhenReadOnly)}>
            <Icon icon={CalendarDays} size={16} />
          </AriaButton>
        </Group>
        <Popover className={cx(styles.popover)} data-material="popover" placement="bottom start">
          <Dialog className={cx(styles.dialog)}>
            <CalendarPanel
              range
              decorations={decorations}
              isDateUnavailable={isDateUnavailable}
              footer={
                hideQuickPicks ? null : (
                  <RangeQuickPicks
                    today={today}
                    onPick={(range) => {
                      setValue(range);
                    }}
                    footerNote={footerNote}
                  />
                )
              }
            />
          </Dialog>
        </Popover>
      </AriaDateRangePicker>
      <FieldMessageList
        messages={messages}
        description={helper}
        errorMessage={shownError}
        disabledReason={disabledReason}
      />
    </div>
  );
}
