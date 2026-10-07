import { CalendarDate, CalendarDateTime } from '@internationalized/date';
import type { Meta, StoryObj } from '@storybook/react-vite';

import { DateField, DatePicker, DateTimePicker } from './DatePicker';
import { DateRangePicker } from './DateRangePicker';

const today = new CalendarDate(2026, 10, 7);
const holidays = [
  { date: '2026-10-28', name: 'Επέτειος του Όχι' },
  { date: '2026-12-25', name: 'Χριστούγεννα' },
  { date: '2026-12-26', name: 'Σύναξη της Θεοτόκου' },
];

const meta = {
  title: 'Components/DatePicker',
  component: DatePicker,
  args: { label: 'Έναρξη ισχύος', today, holidays },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '320px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof DatePicker>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Type «σ», «αύριο», «+30», «+6μ», «τμ» or «7/10» in a segment (or press Ctrl+Space). */
export const Default: Story = {
  args: { defaultValue: today, footerNote: '+10 εργάσιμες: 21/10/2026' },
};

export const UnavailableDates: Story = {
  args: {
    defaultValue: today,
    isDateUnavailable: (date) => date.compare(today.subtract({ days: 30 })) < 0,
    unavailableReason: 'Εκτός επιτρεπόμενου διαστήματος αναδρομικότητας: έως 30 ημέρες',
    extraQuickPicks: [{ label: 'Λήξη τρέχουσας περιόδου', date: new CalendarDate(2027, 3, 31) }],
  },
};

export const Field: Story = {
  render: () => (
    <DateField label="Ημερομηνία γέννησης" defaultValue={new CalendarDate(1985, 3, 9)} />
  ),
};

export const Range: Story = {
  render: () => (
    <DateRangePicker
      label="Περίοδος"
      today={today}
      holidays={holidays}
      defaultValue={{ start: new CalendarDate(2026, 10, 1), end: new CalendarDate(2026, 10, 15) }}
    />
  ),
};

export const DateTime: Story = {
  render: () => (
    <DateTimePicker
      label="Ημερομηνία και ώρα ζημίας"
      today={today}
      defaultValue={new CalendarDateTime(2026, 10, 7, 14, 32)}
    />
  ),
};

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <DatePicker label="Έναρξη ισχύος" today={today} isRequired />
      <DatePicker
        label="Έναρξη ισχύος"
        today={today}
        defaultValue={new CalendarDate(2026, 8, 1)}
        minValue={new CalendarDate(2026, 9, 7)}
      />
      <DatePicker
        label="Έναρξη ισχύος"
        defaultValue={today}
        disabledReason="Ορίζεται από το συμβόλαιο"
      />
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} isDisabled />
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} isReadOnly />
      <DateTimePicker
        label="Αναγγελία"
        defaultValue={new CalendarDateTime(2026, 10, 7, 9, 5)}
        isReadOnly
      />
    </div>
  ),
};
