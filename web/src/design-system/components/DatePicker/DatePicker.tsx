import {
  today as todayIn,
  toCalendarDateTime,
  type CalendarDate,
  type CalendarDateTime,
  type DateValue,
} from '@internationalized/date';
import { CalendarDays, CircleAlert } from 'lucide-react';
import { useMemo, type ReactNode } from 'react';
import {
  Button as AriaButton,
  DateField as AriaDateField,
  DateInput,
  DatePicker as AriaDatePicker,
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
import {
  ATHENS_ZONE,
  formatLongDate,
  formatTimeOfDay,
  holidayMap,
  type Holiday,
} from './dateHelpers';
import styles from './DatePicker.module.css';
import { ReadOnlyDate, SingleQuickPicks, type ExtraQuickPick } from './parts';
import { QuickEntry } from './QuickEntry';
import { useQuickEntry } from './useQuickEntry';

/** Props shared by DateField, DatePicker and DateTimePicker. */
export interface DateFieldCommonProps {
  label: string;
  minValue?: DateValue;
  maxValue?: DateValue;
  /** Dates that cannot be chosen (struck through in the calendar, invalid in the field). */
  isDateUnavailable?: (date: DateValue) => boolean;
  /** Why a date is unavailable («Εκτός επιτρεπόμενου διαστήματος αναδρομικότητας: έως 30 ημέρες»). */
  unavailableReason?: string | ((date: CalendarDate) => string | undefined);
  /** Holidays (PLT calendar): an info dot and the name in the cell label and tooltip. */
  holidays?: readonly Holiday[];
  /** «Today» for the accelerators and quick picks; defaults to today in Europe/Athens. */
  today?: CalendarDate;
  /** Extra quick picks after Σήμερα / Αύριο / 1η επόμενου μήνα, e.g. «Λήξη τρέχουσας περιόδου». */
  extraQuickPicks?: readonly ExtraQuickPick[];
  /** Hide the quick picks. */
  hideQuickPicks?: boolean;
  /** Business-day note under the calendar, e.g. «+10 εργάσιμες: 21/10/2026». */
  footerNote?: ReactNode;
  /** Helper text; defaults to the format hint «ηη/μμ/εεεε». */
  description?: string;
  errorMessage?: string;
  isRequired?: boolean;
  /** Read-only shows the long format as text («Τρίτη, 7 Οκτωβρίου 2026»). */
  isReadOnly?: boolean;
  isDisabled?: boolean;
  disabledReason?: string;
  name?: string;
  className?: string;
}

export interface DatePickerProps extends DateFieldCommonProps {
  value?: CalendarDate | null;
  defaultValue?: CalendarDate | null;
  onChange?: (value: CalendarDate | null) => void;
}

export interface DateTimePickerProps extends DateFieldCommonProps {
  value?: CalendarDateTime | null;
  defaultValue?: CalendarDateTime | null;
  onChange?: (value: CalendarDateTime | null) => void;
}

export type DateFieldProps = DatePickerProps;

type Mode = 'field' | 'picker' | 'datetime';

interface BaseProps extends DateFieldCommonProps {
  mode: Mode;
  value: DateValue | null | undefined;
  defaultValue: DateValue | null;
  onChange: ((value: DateValue | null) => void) | undefined;
}

function reasonFor(
  unavailableReason: DateFieldCommonProps['unavailableReason'],
  date: CalendarDate,
): string | undefined {
  return typeof unavailableReason === 'function' ? unavailableReason(date) : unavailableReason;
}

/** Shared implementation of DateField, DatePicker and DateTimePicker. */
function DateBase(props: BaseProps) {
  const {
    mode,
    label,
    minValue,
    maxValue,
    isDateUnavailable,
    unavailableReason,
    holidays,
    extraQuickPicks,
    hideQuickPicks = false,
    footerNote,
    description,
    errorMessage,
    isRequired = false,
    isReadOnly = false,
    isDisabled = false,
    disabledReason,
    name,
    className,
  } = props;
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const [value, setValue] = useControlledValue<DateValue | null>(
    props.value,
    props.defaultValue,
    props.onChange,
  );
  const today = props.today ?? todayIn(ATHENS_ZONE);
  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const readOnlyLike = softDisabled;
  const quick = useQuickEntry(!isDisabled && !readOnlyLike && !isReadOnly);
  const decorations = useMemo(
    () => ({
      holidays: holidayMap(holidays),
      unavailableReason: (date: CalendarDate) => reasonFor(unavailableReason, date),
    }),
    [holidays, unavailableReason],
  );

  /** Keeps the time of a date-time value when a date is chosen by accelerator or quick pick. */
  const withTime = (date: CalendarDate): DateValue => {
    if (mode !== 'datetime') return date;
    if (value && 'hour' in value) return toCalendarDateTime(date, value);
    return toCalendarDateTime(date);
  };

  let ownError: string | undefined;
  if (value) {
    if (minValue && value.compare(minValue) < 0) {
      ownError = t('datePicker.beforeMin', { date: formatDate(minValue, region) });
    } else if (maxValue && value.compare(maxValue) > 0) {
      ownError = t('datePicker.afterMax', { date: formatDate(maxValue, region) });
    } else if (isDateUnavailable?.(value)) {
      ownError = reasonFor(unavailableReason, value as CalendarDate) ?? t('datePicker.unavailable');
    }
  }
  const shownError = errorMessage ?? ownError;
  const helper = description ?? t('datePicker.formatHint');
  const messages = useFieldMessages({
    description: helper,
    errorMessage: shownError,
    disabledReason,
  });
  const isInvalid = messages.showError;

  if (isReadOnly) {
    const text = value
      ? mode === 'datetime'
        ? t('datePicker.dateTime', {
            date: formatLongDate(value, region),
            time: formatTimeOfDay(value),
          })
        : formatLongDate(value, region)
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

  const fieldProps = {
    value,
    onChange: (next: DateValue | null) => {
      setValue(next);
    },
    isRequired,
    isInvalid,
    isDisabled,
    isReadOnly: readOnlyLike,
    validationBehavior: 'aria' as const,
    shouldForceLeadingZeros: true,
    ...(mode === 'datetime' ? { granularity: 'minute' as const, hourCycle: 24 as const } : {}),
    ...defined({
      minValue,
      maxValue,
      isDateUnavailable,
      name,
      'aria-describedby': messages.describedBy,
    }),
  };

  const frameContent = (
    <>
      <DateInput className={cx(styles.dateInput)} data-quick-slot="single">
        {(segment) => <DateSegment segment={segment} className={cx(styles.segment)} />}
      </DateInput>
      {quick.request ? (
        <QuickEntry
          initialText={quick.request.text}
          today={today}
          region={region}
          onCommit={(date) => {
            setValue(withTime(date));
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
    </>
  );

  const frameProps = {
    className: cx(fieldStyles.frame, styles.frame),
    isInvalid,
    isDisabled,
    'data-soft-disabled': softDisabled || undefined,
  };

  return (
    <div
      className={cx(fieldStyles.field, styles.dateField, className)}
      onKeyDownCapture={quick.onKeyDownCapture}
    >
      {mode === 'field' ? (
        <AriaDateField {...fieldProps} className={cx(fieldStyles.field)}>
          <FieldChromeLabel isRequired={isRequired}>{label}</FieldChromeLabel>
          <Group {...frameProps}>{frameContent}</Group>
        </AriaDateField>
      ) : (
        <AriaDatePicker {...fieldProps} className={cx(fieldStyles.field)}>
          <FieldChromeLabel isRequired={isRequired}>{label}</FieldChromeLabel>
          <Group {...frameProps}>
            {frameContent}
            <AriaButton className={cx(fieldStyles.fieldButton, fieldStyles.hideWhenReadOnly)}>
              <Icon icon={CalendarDays} size={16} />
            </AriaButton>
          </Group>
          <Popover className={cx(styles.popover)} data-material="popover" placement="bottom start">
            <Dialog className={cx(styles.dialog)}>
              <CalendarPanel
                decorations={decorations}
                isDateUnavailable={isDateUnavailable}
                footer={
                  hideQuickPicks ? (
                    footerNote ? (
                      <div className={styles.footer}>
                        <div className={styles.footerNote}>{footerNote}</div>
                      </div>
                    ) : null
                  ) : (
                    <SingleQuickPicks
                      today={today}
                      onPick={(date) => {
                        setValue(withTime(date));
                      }}
                      extra={extraQuickPicks}
                      minValue={minValue}
                      maxValue={maxValue}
                      isDateUnavailable={isDateUnavailable}
                      footerNote={footerNote}
                    />
                  )
                }
              />
            </Dialog>
          </Popover>
        </AriaDatePicker>
      )}
      <FieldMessageList
        messages={messages}
        description={helper}
        errorMessage={shownError}
        disabledReason={disabledReason}
      />
    </div>
  );
}

/**
 * Segmented date field without a calendar (birth dates): dd/mm/yyyy segments labelled «ημέρα / μήνας /
 * έτος», plus the accelerators (type `σ`, `+30`, `7/10`… or press Ctrl+Space).
 */
export function DateField({ value, defaultValue = null, onChange, ...rest }: DateFieldProps) {
  return (
    <DateBase
      {...rest}
      mode="field"
      value={value}
      defaultValue={defaultValue}
      onChange={onChange as ((value: DateValue | null) => void) | undefined}
    />
  );
}

/**
 * Date picker (Part 2 §4.5): segmented field + calendar popover with quick picks, holidays, unavailable
 * dates and the accelerators. Alt+↓ opens the calendar; Esc closes it.
 */
export function DatePicker({ value, defaultValue = null, onChange, ...rest }: DatePickerProps) {
  return (
    <DateBase
      {...rest}
      mode="picker"
      value={value}
      defaultValue={defaultValue}
      onChange={onChange as ((value: DateValue | null) => void) | undefined}
    />
  );
}

/** Date and time (minute granularity, 24-hour), Europe/Athens wall time. */
export function DateTimePicker({
  value,
  defaultValue = null,
  onChange,
  ...rest
}: DateTimePickerProps) {
  return (
    <DateBase
      {...rest}
      mode="datetime"
      value={value}
      defaultValue={defaultValue}
      onChange={onChange as ((value: DateValue | null) => void) | undefined}
    />
  );
}
