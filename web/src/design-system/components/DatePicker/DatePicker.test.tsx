import { CalendarDate, CalendarDateTime, getDayOfWeek } from '@internationalized/date';
import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { rangeQuickPickDates, singleQuickPickDate } from './dateHelpers';
import { DateField, DatePicker, DateTimePicker } from './DatePicker';
import { DateRangePicker } from './DateRangePicker';

// Wednesday 7 October 2026.
const today = new CalendarDate(2026, 10, 7);

function segments(group: HTMLElement) {
  return within(group).getAllByRole('spinbutton');
}

/** The item at `index`; fails the test when it is missing (no non-null assertions). */
function at<T>(list: readonly T[], index: number): T {
  const item = list[index];
  if (item === undefined) throw new Error(`No item at ${String(index)}`);
  return item;
}

function fieldGroup(name: RegExp | string) {
  return at(screen.getAllByRole('group', { name }), 0);
}

async function openCalendar(user: ReturnType<typeof renderWithDs>['user']) {
  await user.click(screen.getByRole('button', { name: /Ημερολόγιο|Calendar/ }));
  return screen.getByRole('grid');
}

describe('date helpers', () => {
  it('computes single quick picks', () => {
    expect(singleQuickPickDate('today', today).toString()).toBe('2026-10-07');
    expect(singleQuickPickDate('tomorrow', today).toString()).toBe('2026-10-08');
    expect(singleQuickPickDate('firstOfNextMonth', today).toString()).toBe('2026-11-01');
    expect(singleQuickPickDate('firstOfNextMonth', new CalendarDate(2026, 12, 15)).toString()).toBe(
      '2027-01-01',
    );
  });

  it.each([
    ['last7Days', '2026-10-01', '2026-10-07'],
    ['currentMonth', '2026-10-01', '2026-10-31'],
    ['previousQuarter', '2026-07-01', '2026-09-30'],
    ['yearToDate', '2026-01-01', '2026-10-07'],
  ] as const)('range quick pick %s → %s … %s', (pick, start, end) => {
    const range = rangeQuickPickDates(pick, today);
    expect(range.start.toString()).toBe(start);
    expect(range.end.toString()).toBe(end);
  });

  it('wraps the previous quarter across years', () => {
    const range = rangeQuickPickDates('previousQuarter', new CalendarDate(2026, 2, 10));
    expect(range.start.toString()).toBe('2025-10-01');
    expect(range.end.toString()).toBe('2025-12-31');
  });
});

describe('DateField', () => {
  it('renders dd/mm/yyyy segments labelled ημέρα / μήνας / έτος', () => {
    renderWithDs(
      <DateField label="Ημερομηνία γέννησης" defaultValue={new CalendarDate(1985, 3, 9)} />,
    );
    const group = fieldGroup(/Ημερομηνία γέννησης/);
    const [day, month, year] = segments(group);
    expect(day).toHaveAccessibleName(/ημέρα/);
    expect(month).toHaveAccessibleName(/μήνας/);
    expect(year).toHaveAccessibleName(/έτος/);
    expect(day).toHaveTextContent('09');
    expect(month).toHaveTextContent('03');
    expect(year).toHaveTextContent('1985');
  });

  it('types into segments with auto-advance and steps with ↑/↓', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<DateField label="Ημερομηνία" onChange={onChange} />);
    await user.click(at(segments(fieldGroup(/Ημερομηνία/)), 0));
    await user.keyboard('07102026');
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDate(2026, 10, 7));
    await user.keyboard('{ArrowUp}');
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDate(2027, 10, 7));
  });

  it('shows the format hint as the description', () => {
    renderWithDs(<DateField label="Ημερομηνία" />);
    const [day] = segments(fieldGroup(/Ημερομηνία/));
    expect(day).toHaveAccessibleDescription('ηη/μμ/εεεε');
  });

  describe('accelerators', () => {
    it.each([
      ['σ', '2026-10-07'],
      ['α', '2026-10-08'],
      ['τμ', '2026-10-31'],
      ['+30', '2026-11-06'],
      ['-7', '2026-09-30'],
      ['+6μ', '2027-04-07'],
      ['+1ε', '2027-10-07'],
    ])('typing %s sets %s', async (text, expected) => {
      const onChange = vi.fn();
      const { user } = renderWithDs(
        <DateField label="Ημερομηνία" today={today} onChange={onChange} />,
      );
      await user.click(at(segments(fieldGroup(/Ημερομηνία/)), 0));
      await user.keyboard(text);
      const box = screen.getByRole('textbox', { name: 'Γρήγορη εισαγωγή ημερομηνίας' });
      expect(box).toHaveValue(text);
      await user.keyboard('{Enter}');
      expect(String(onChange.mock.lastCall?.[0])).toBe(expected);
      expect(
        screen.queryByRole('textbox', { name: 'Γρήγορη εισαγωγή ημερομηνίας' }),
      ).not.toBeInTheDocument();
      expect(segments(fieldGroup(/Ημερομηνία/))[0]).toHaveFocus();
    });

    it('shows a live long-date preview (genitive month)', async () => {
      const { user } = renderWithDs(<DateField label="Ημερομηνία" today={today} />);
      await user.click(at(segments(fieldGroup(/Ημερομηνία/)), 0));
      await user.keyboard('+30');
      const box = screen.getByRole('textbox', { name: 'Γρήγορη εισαγωγή ημερομηνίας' });
      expect(box).toHaveAccessibleDescription('→ Παρασκευή, 6 Νοεμβρίου 2026');
    });

    it('opens empty with Ctrl+Space and accepts 7/10', async () => {
      const onChange = vi.fn();
      const { user } = renderWithDs(
        <DateField label="Ημερομηνία" today={today} onChange={onChange} />,
      );
      await user.click(at(segments(fieldGroup(/Ημερομηνία/)), 0));
      await user.keyboard('{Control>} {/Control}');
      const box = screen.getByRole('textbox', { name: 'Γρήγορη εισαγωγή ημερομηνίας' });
      expect(box).toHaveValue('');
      expect(box).toHaveFocus();
      await user.keyboard('7/10{Enter}');
      expect(onChange).toHaveBeenLastCalledWith(
        expect.objectContaining({ year: 2026, month: 10, day: 7 }),
      );
    });

    it('keeps the box open with an error for unknown text, and Esc closes it without a change', async () => {
      const onChange = vi.fn();
      const { user } = renderWithDs(
        <DateField label="Ημερομηνία" today={today} onChange={onChange} />,
      );
      await user.click(at(segments(fieldGroup(/Ημερομηνία/)), 0));
      await user.keyboard('xyz{Enter}');
      const box = screen.getByRole('textbox', { name: 'Γρήγορη εισαγωγή ημερομηνίας' });
      expect(box).toHaveAttribute('aria-invalid', 'true');
      expect(box).toHaveAccessibleDescription(
        expect.stringContaining('Δεν αναγνωρίστηκε ημερομηνία'),
      );
      await user.keyboard('{Escape}');
      expect(box).not.toBeInTheDocument();
      expect(onChange).not.toHaveBeenCalled();
      expect(segments(fieldGroup(/Ημερομηνία/))[0]).toHaveFocus();
    });

    it('parses on blur', async () => {
      const onChange = vi.fn();
      const { user } = renderWithDs(
        <>
          <DateField label="Ημερομηνία" today={today} onChange={onChange} />
          <button type="button">Επόμενο</button>
        </>,
      );
      await user.click(at(segments(fieldGroup(/Ημερομηνία/)), 0));
      await user.keyboard('αύριο');
      await user.click(screen.getByRole('button', { name: 'Επόμενο' }));
      expect(onChange).toHaveBeenLastCalledWith(
        expect.objectContaining({ year: 2026, month: 10, day: 8 }),
      );
    });
  });
});

describe('DatePicker', () => {
  it('opens the calendar with Alt+↓ and shows a nominative month title, Monday first', async () => {
    const { user } = renderWithDs(
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} today={today} />,
    );
    await user.click(at(segments(fieldGroup(/Έναρξη ισχύος/)), 0));
    await user.keyboard('{Alt>}{ArrowDown}{/Alt}');
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('button', { name: /Οκτώβριος 2026/ })).toBeInTheDocument();
    // The weekday row is hidden from screen readers (every cell has a full label); Monday comes first.
    const headers = Array.from(dialog.querySelectorAll('thead th'));
    expect(headers[0]).toHaveTextContent('Δευ');
    expect(headers[6]).toHaveTextContent('Κυρ');
    const selected = within(dialog).getByRole('gridcell', { selected: true });
    expect(selected).toHaveTextContent('7');
  });

  it('navigates the grid with arrows and PgDn, selects with Enter and closes', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} today={today} onChange={onChange} />,
    );
    const grid = await openCalendar(user);
    expect(grid).toBeInTheDocument();
    await user.keyboard('{ArrowRight}{ArrowDown}');
    await user.keyboard('{Enter}');
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDate(2026, 10, 15));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await openCalendar(user);
    await user.keyboard('{PageDown}{Enter}');
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDate(2026, 11, 15));
  });

  it('closes on Esc without a change', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} today={today} onChange={onChange} />,
    );
    await openCalendar(user);
    await user.keyboard('{ArrowRight}{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('labels holidays, strikes unavailable dates with a reason and marks weekends', async () => {
    const reason = 'Εκτός επιτρεπόμενου διαστήματος αναδρομικότητας: έως 30 ημέρες';
    const { user } = renderWithDs(
      <DatePicker
        label="Έναρξη ισχύος"
        defaultValue={today}
        today={today}
        holidays={[{ date: '2026-10-28', name: 'Επέτειος του Όχι' }]}
        isDateUnavailable={(d) => d.compare(new CalendarDate(2026, 10, 5)) < 0}
        unavailableReason={reason}
      />,
    );
    await openCalendar(user);
    const holiday = screen.getByRole('button', { name: /28 Οκτωβρίου 2026, Επέτειος του Όχι/ });
    expect(holiday).toHaveAttribute('data-holiday', 'true');
    const unavailable = screen.getByRole('button', {
      name: new RegExp(`2 Οκτωβρίου 2026.*${reason}`),
    });
    expect(unavailable).toHaveAttribute('data-unavailable', 'true');
    expect(unavailable).toHaveAttribute('aria-disabled', 'true');
    const saturday = screen.getByRole('button', { name: /^Σάββατο 10 Οκτωβρίου 2026$/ });
    expect(saturday).toHaveAttribute('data-weekend', 'true');
    expect(getDayOfWeek(new CalendarDate(2026, 10, 10), 'el-GR')).toBe(5);
    const todayCell = screen.getByRole('button', { name: /Τετάρτη 7 Οκτωβρίου 2026/ });
    expect(todayCell).toHaveAttribute('data-today', 'true');
  });

  it('applies quick picks and the business-day note', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DatePicker
        label="Έναρξη ισχύος"
        today={today}
        onChange={onChange}
        footerNote="+10 εργάσιμες: 21/10/2026"
      />,
    );
    await openCalendar(user);
    expect(screen.getByText('+10 εργάσιμες: 21/10/2026')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: '1η επόμενου μήνα' }));
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDate(2026, 11, 1));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await openCalendar(user);
    await user.click(screen.getByRole('button', { name: 'Αύριο' }));
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDate(2026, 10, 8));
  });

  it('switches month and year from the title', async () => {
    const { user } = renderWithDs(
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} today={today} />,
    );
    await openCalendar(user);
    await user.click(
      screen.getByRole('button', { name: /Οκτώβριος 2026, επιλογή μήνα και έτους/ }),
    );
    const months = at(screen.getAllByRole('listbox'), 0);
    await user.click(within(months).getByRole('option', { name: /Μάρτιος|Μαρτίου/ }));
    expect(screen.getByRole('button', { name: /Μάρτιος 2026/ })).toBeInTheDocument();
    expect(screen.getByRole('grid')).toBeInTheDocument();
  });

  it('shows an error for an unavailable value (error first in the description)', () => {
    renderWithDs(
      <DatePicker
        label="Έναρξη ισχύος"
        defaultValue={new CalendarDate(2026, 9, 1)}
        today={today}
        minValue={new CalendarDate(2026, 9, 7)}
      />,
    );
    const [day] = segments(fieldGroup(/Έναρξη ισχύος/));
    expect(day).toHaveAttribute('aria-invalid', 'true');
    // React Aria describes the current value first («Επιλεγμένη ημερομηνία: …»), then our error.
    expect(day).toHaveAccessibleDescription(
      expect.stringContaining(
        'Η ημερομηνία είναι πριν από τις 07/09/2026. Επιλέξτε 07/09/2026 ή μεταγενέστερη ημερομηνία.',
      ),
    );
    const ids = (day?.getAttribute('aria-describedby') ?? '').split(' ');
    const ours = ids.filter((id) => !id.startsWith('react-aria-description'));
    expect(document.getElementById(ours[0] ?? '')).toHaveTextContent('πριν από τις 07/09/2026');
  });

  it('renders read-only as the long date', () => {
    renderWithDs(<DatePicker label="Έναρξη ισχύος" defaultValue={today} isReadOnly />);
    const box = screen.getByRole('textbox', { name: 'Έναρξη ισχύος' });
    expect(box).toHaveAttribute('aria-readonly', 'true');
    expect(box).toHaveTextContent('Τετάρτη, 7 Οκτωβρίου 2026');
  });

  it('keeps a soft-disabled picker focusable with its reason', async () => {
    const { user } = renderWithDs(
      <DatePicker
        label="Έναρξη ισχύος"
        defaultValue={today}
        disabledReason="Ορίζεται από το συμβόλαιο"
      />,
    );
    await user.tab();
    const [day] = segments(fieldGroup(/Έναρξη ισχύος/));
    expect(day).toHaveFocus();
    expect(day).toHaveAccessibleDescription(expect.stringContaining('Ορίζεται από το συμβόλαιο'));
    await user.keyboard('σ');
    expect(
      screen.queryByRole('textbox', { name: 'Γρήγορη εισαγωγή ημερομηνίας' }),
    ).not.toBeInTheDocument();
  });

  it('has no axe violations (closed and open)', async () => {
    const { container, user } = renderWithDs(
      <DatePicker label="Έναρξη ισχύος" defaultValue={today} today={today} isRequired />,
    );
    await expectNoA11yViolations(container);
    await openCalendar(user);
    await expectNoA11yViolations(document.body);
  });
});

describe('DateRangePicker', () => {
  it('selects a range in the calendar with in-range days', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DateRangePicker label="Περίοδος" today={today} onChange={onChange} />,
    );
    await openCalendar(user);
    await user.click(screen.getByRole('button', { name: /^Δευτέρα 12 Οκτωβρίου 2026/ }));
    await user.click(screen.getByRole('button', { name: /^Πέμπτη 15 Οκτωβρίου 2026/ }));
    expect(onChange).toHaveBeenLastCalledWith({
      start: new CalendarDate(2026, 10, 12),
      end: new CalendarDate(2026, 10, 15),
    });
  });

  it('marks in-range days and applies range quick picks', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DateRangePicker
        label="Περίοδος"
        today={today}
        defaultValue={{
          start: new CalendarDate(2026, 10, 12),
          end: new CalendarDate(2026, 10, 15),
        }}
        onChange={onChange}
      />,
    );
    await openCalendar(user);
    expect(screen.getByRole('button', { name: /13 Οκτωβρίου 2026/ })).toHaveAttribute(
      'data-in-range',
      'true',
    );
    expect(screen.getByRole('button', { name: /12 Οκτωβρίου 2026/ })).toHaveAttribute(
      'data-range-start',
      'true',
    );
    await user.click(screen.getByRole('button', { name: 'Προηγούμενο τρίμηνο' }));
    expect(onChange).toHaveBeenLastCalledWith({
      start: new CalendarDate(2026, 7, 1),
      end: new CalendarDate(2026, 9, 30),
    });
  });

  it('accepts accelerators in the end input', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DateRangePicker
        label="Περίοδος"
        today={today}
        defaultValue={{ start: today, end: today }}
        onChange={onChange}
      />,
    );
    const all = within(fieldGroup(/Περίοδος/)).getAllByRole('spinbutton');
    await user.click(at(all, 3));
    await user.keyboard('+30{Enter}');
    expect(onChange).toHaveBeenLastCalledWith({ start: today, end: new CalendarDate(2026, 11, 6) });
  });

  it('reports an end before the start', () => {
    renderWithDs(
      <DateRangePicker
        label="Περίοδος"
        defaultValue={{
          start: new CalendarDate(2026, 10, 15),
          end: new CalendarDate(2026, 10, 12),
        }}
      />,
    );
    const [day] = within(fieldGroup(/Περίοδος/)).getAllByRole('spinbutton');
    expect(day).toHaveAccessibleDescription(
      expect.stringContaining('Η ημερομηνία λήξης είναι πριν'),
    );
  });

  it('renders read-only as a long range', () => {
    renderWithDs(
      <DateRangePicker
        label="Περίοδος"
        isReadOnly
        defaultValue={{ start: new CalendarDate(2026, 10, 1), end: new CalendarDate(2026, 10, 31) }}
      />,
    );
    expect(screen.getByRole('textbox', { name: 'Περίοδος' })).toHaveTextContent(
      'Πέμπτη, 1 Οκτωβρίου 2026 έως Σάββατο, 31 Οκτωβρίου 2026',
    );
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(<DateRangePicker label="Περίοδος" today={today} />);
    await expectNoA11yViolations(container);
  });
});

describe('DateTimePicker', () => {
  it('has hour and minute segments (24-hour) and keeps the time on quick entry', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <DateTimePicker
        label="Ώρα ζημίας"
        today={today}
        defaultValue={new CalendarDateTime(2026, 10, 7, 14, 32)}
        onChange={onChange}
      />,
    );
    const all = segments(fieldGroup(/Ώρα ζημίας/));
    expect(all.map((s) => s.textContent)).toEqual(['07', '10', '2026', '14', '32']);
    await user.click(at(all, 0));
    await user.keyboard('α{Enter}');
    expect(onChange).toHaveBeenLastCalledWith(new CalendarDateTime(2026, 10, 8, 14, 32));
  });

  it('renders read-only with date and time', () => {
    renderWithDs(
      <DateTimePicker
        label="Ώρα ζημίας"
        isReadOnly
        defaultValue={new CalendarDateTime(2026, 10, 7, 9, 5)}
      />,
    );
    expect(screen.getByRole('textbox', { name: 'Ώρα ζημίας' })).toHaveTextContent(
      'Τετάρτη, 7 Οκτωβρίου 2026, 09:05',
    );
  });
});

describe('English', () => {
  it('renders English labels and quick picks', async () => {
    await i18n.changeLanguage('en');
    const { user } = renderWithDs(<DatePicker label="Start date" today={today} />);
    const [day] = segments(fieldGroup(/Start date/));
    expect(day).toHaveAccessibleName(/day/);
    await openCalendar(user);
    expect(screen.getByRole('button', { name: 'Tomorrow' })).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: /October 2026, choose month and year/ }),
    ).toBeInTheDocument();
  });
});
