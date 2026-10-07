import { act, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import type { Key } from 'react-aria-components';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { clearAnnouncements } from '../../a11y/announce';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Combobox } from './Combobox';
import type { ComboboxOption } from './options';

const people: ComboboxOption[] = [
  {
    id: 'p1',
    label: 'Γεώργιος Παπαδόπουλος',
    caption: '1978 · ΑΦΜ •••789 · Αθήνα',
    meta: 'ΠΡΣ-004471',
  },
  {
    id: 'p2',
    label: 'Γεώργιος Παπαδάκης',
    caption: '1985 · ΑΦΜ •••112 · Ηράκλειο',
    meta: 'ΠΡΣ-001203',
  },
  { id: 'p3', label: 'Μαρία Αποστόλου', caption: '1990 · ΑΦΜ •••541 · Πάτρα', meta: 'ΠΡΣ-002210' },
  {
    id: 'p4',
    label: 'Ευάγγελος Θεοδωρίδης',
    caption: '1969 · ΑΦΜ •••300 · Θεσσαλονίκη',
    meta: 'ΠΡΣ-003377',
  },
];

function input() {
  return screen.getByRole('combobox', { name: /Πελάτης/ });
}

afterEach(() => {
  clearAnnouncements();
  vi.useRealTimers();
});

describe('Combobox', () => {
  it('exposes combobox ARIA and opens on typing', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    const box = input();
    expect(box).toHaveAttribute('aria-autocomplete', 'list');
    expect(box).toHaveAttribute('aria-expanded', 'false');
    await user.type(box, 'παπ');
    expect(box).toHaveAttribute('aria-expanded', 'true');
    const listbox = screen.getByRole('listbox');
    expect(box).toHaveAttribute('aria-controls', listbox.id);
    expect(within(listbox).getAllByRole('option')).toHaveLength(2);
  });

  it('matches Greeklish and highlights the match', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.type(input(), 'papad');
    const options = within(screen.getByRole('listbox')).getAllByRole('option');
    expect(options).toHaveLength(2);
    const mark = options[0]?.querySelector('mark');
    expect(mark).toHaveTextContent('Παπαδ');
  });

  it('matches accent-insensitively, by caption and by numeric id', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.type(input(), 'θεσσαλονικη');
    expect(within(screen.getByRole('listbox')).getAllByRole('option')).toHaveLength(1);
    await user.clear(input());
    await user.type(input(), '4471');
    const options = within(screen.getByRole('listbox')).getAllByRole('option');
    expect(options).toHaveLength(1);
    expect(options[0]).toHaveTextContent('Γεώργιος Παπαδόπουλος');
  });

  it('names options by label and describes them by caption', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.type(input(), 'μαρ');
    const option = screen.getByRole('option', { name: 'Μαρία Αποστόλου' });
    expect(option).toHaveAccessibleDescription('1990 · ΑΦΜ •••541 · Πάτρα');
  });

  it('selects with ↓ and Enter and reports the item', async () => {
    const onSelectionChange = vi.fn();
    const { user } = renderWithDs(
      <Combobox label="Πελάτης" items={people} onSelectionChange={onSelectionChange} />,
    );
    await user.type(input(), 'μαρ');
    await user.keyboard('{ArrowDown}');
    expect(input()).toHaveAttribute('aria-activedescendant');
    await user.keyboard('{Enter}');
    expect(onSelectionChange).toHaveBeenLastCalledWith('p3', people[2]);
    expect(input()).toHaveValue('Μαρία Αποστόλου');
    expect(input()).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens with Alt+↓ and shows every option', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.click(input());
    await user.keyboard('{Alt>}{ArrowDown}{/Alt}');
    expect(input()).toHaveAttribute('aria-expanded', 'true');
    expect(within(screen.getByRole('listbox')).getAllByRole('option')).toHaveLength(4);
  });

  it('closes on Esc, then clears on a second Esc', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.type(input(), 'παπ');
    expect(input()).toHaveAttribute('aria-expanded', 'true');
    await user.keyboard('{Escape}');
    expect(input()).toHaveAttribute('aria-expanded', 'false');
    expect(input()).toHaveValue('παπ');
    await user.keyboard('{Escape}');
    expect(input()).toHaveValue('');
  });

  it('keeps typed text on blur and warns that no value was selected', async () => {
    const { user } = renderWithDs(
      <>
        <Combobox label="Πελάτης" items={people} description="Αναζήτηση με όνομα ή ΑΦΜ" />
        <button type="button">Επόμενο</button>
      </>,
    );
    await user.type(input(), 'Παπαδ');
    expect(input()).toHaveAccessibleDescription('Αναζήτηση με όνομα ή ΑΦΜ');
    await user.tab();
    expect(screen.getByRole('button', { name: 'Επόμενο' })).toHaveFocus();
    expect(input()).toHaveValue('Παπαδ');
    expect(input()).toHaveAccessibleDescription('Δεν επιλέχθηκε τιμή');
  });

  it('shows the empty state with the query', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.type(input(), 'ζζζ');
    expect(screen.getByRole('listbox')).toHaveTextContent('Δεν βρέθηκαν αποτελέσματα για «ζζζ»');
  });

  it('offers a create row (creatable) that receives the typed text', async () => {
    const onCreate = vi.fn();
    const { user } = renderWithDs(
      <Combobox
        label="Πελάτης"
        items={people}
        onCreate={onCreate}
        createLabel="Δημιουργία νέου προσώπου…"
      />,
    );
    await user.type(input(), 'Νίκος Ζαχαρίου');
    const listbox = screen.getByRole('listbox');
    expect(listbox).toHaveTextContent('Δεν βρέθηκαν αποτελέσματα για «Νίκος Ζαχαρίου»');
    await user.click(screen.getByRole('option', { name: 'Δημιουργία νέου προσώπου…' }));
    expect(onCreate).toHaveBeenCalledWith('Νίκος Ζαχαρίου');
    expect(input()).toHaveValue('Νίκος Ζαχαρίου');
  });

  it('groups results under section headers', async () => {
    const grouped: ComboboxOption[] = [
      { id: 'a', label: 'Παπαδόπουλος Γεώργιος', group: 'Πρόσωπα' },
      {
        id: 'b',
        label: 'ΑΣΦ-2026-004471',
        caption: 'Παπαδόπουλος · Αυτοκίνητο',
        group: 'Ασφαλιστήρια',
      },
    ];
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={grouped} />);
    await user.type(input(), 'παπ');
    const groups = within(screen.getByRole('listbox')).getAllByRole('group');
    expect(groups.map((g) => within(g).getAllByRole('option').length)).toEqual([1, 1]);
    expect(screen.getByRole('group', { name: 'Πρόσωπα' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Ασφαλιστήρια' })).toBeInTheDocument();
  });

  it('announces the result count politely', async () => {
    const { user } = renderWithDs(<Combobox label="Πελάτης" items={people} />);
    await user.type(input(), 'γεω');
    await waitFor(
      () => {
        expect(document.getElementById('ds-live-polite')).toHaveTextContent('2 αποτελέσματα');
      },
      { timeout: 2000 },
    );
  });

  it('wires required, error (error id first) and invalid state', () => {
    renderWithDs(
      <Combobox
        label="Πελάτης"
        items={people}
        isRequired
        description="Αναζήτηση με όνομα"
        errorMessage="Επιλέξτε πελάτη από τη λίστα."
      />,
    );
    const box = screen.getByRole('combobox', { name: 'Πελάτης υποχρεωτικό' });
    expect(box).toHaveAttribute('aria-required', 'true');
    expect(box).toHaveAttribute('aria-invalid', 'true');
    const describedBy = box.getAttribute('aria-describedby') ?? '';
    const first = document.getElementById(describedBy.split(' ')[0] ?? '');
    expect(first).toHaveTextContent('Επιλέξτε πελάτη από τη λίστα.');
  });

  it('stays focusable with aria-disabled and a reason when soft-disabled', async () => {
    const { user } = renderWithDs(
      <Combobox
        label="Πελάτης"
        items={people}
        disabledReason="Ο πελάτης κλειδώθηκε στην προσφορά"
      />,
    );
    await user.tab();
    expect(input()).toHaveFocus();
    expect(input()).toHaveAttribute('aria-disabled', 'true');
    expect(input()).toHaveAccessibleDescription('Ο πελάτης κλειδώθηκε στην προσφορά');
    await user.keyboard('παπ');
    expect(input()).toHaveValue('');
  });

  it('is read-only (native readonly, no list)', () => {
    renderWithDs(
      <Combobox
        label="Πελάτης"
        items={people}
        isReadOnly
        defaultSelectedKey="p1"
        defaultInputValue="Γεώργιος Παπαδόπουλος"
      />,
    );
    expect(input()).toHaveAttribute('readonly');
    expect(input()).toHaveValue('Γεώργιος Παπαδόπουλος');
  });

  it('supports controlled selection', async () => {
    function Controlled() {
      const [key, setKey] = useState<Key | null>('p2');
      const [text, setText] = useState('Γεώργιος Παπαδάκης');
      return (
        <>
          <Combobox
            label="Πελάτης"
            items={people}
            selectedKey={key}
            onSelectionChange={setKey}
            inputValue={text}
            onInputChange={setText}
          />
          <output data-testid="key">{String(key)}</output>
        </>
      );
    }
    const { user } = renderWithDs(<Controlled />);
    expect(input()).toHaveValue('Γεώργιος Παπαδάκης');
    await user.clear(input());
    await user.type(input(), 'ευαγ');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(screen.getByTestId('key')).toHaveTextContent('p4');
  });

  describe('async', () => {
    it('debounces, requires 2 characters (1 for numbers) and shows results', async () => {
      const load = vi.fn((query: string) =>
        Promise.resolve(
          people.filter((p) => p.label.includes(query) || (p.meta ?? '').includes(query)),
        ),
      );
      const { user } = renderWithDs(<Combobox label="Πελάτης" loadOptions={load} />);
      await user.type(input(), 'Μ');
      expect(screen.getByRole('listbox')).toHaveTextContent(
        'Πληκτρολογήστε τουλάχιστον 2 χαρακτήρες',
      );
      expect(load).not.toHaveBeenCalled();
      await user.type(input(), 'αρ');
      await waitFor(() => {
        expect(screen.getByRole('option', { name: 'Μαρία Αποστόλου' })).toBeInTheDocument();
      });
      expect(load).toHaveBeenCalledTimes(1);
      expect(load).toHaveBeenCalledWith('Μαρ', expect.any(AbortSignal));

      await user.clear(input());
      await user.type(input(), '3');
      await waitFor(() => {
        expect(load).toHaveBeenLastCalledWith('3', expect.any(AbortSignal));
      });
    });

    it('shows the error state and retries with the same query', async () => {
      let fail = true;
      const load = vi.fn((query: string) =>
        fail
          ? Promise.reject(new Error('503'))
          : Promise.resolve(people.filter((p) => p.label.includes(query))),
      );
      const { user } = renderWithDs(<Combobox label="Πελάτης" loadOptions={load} />);
      await user.type(input(), 'Μαρ');
      const retry = await screen.findByRole('option', { name: 'Επανάληψη' });
      expect(screen.getByRole('listbox')).toHaveTextContent('Η αναζήτηση απέτυχε. Δοκιμάστε ξανά.');
      fail = false;
      await user.click(retry);
      expect(await screen.findByRole('option', { name: 'Μαρία Αποστόλου' })).toBeInTheDocument();
      expect(input()).toHaveValue('Μαρ');
      expect(load).toHaveBeenLastCalledWith('Μαρ', expect.any(AbortSignal));
    });

    it('marks the list busy and dims old results while loading', async () => {
      const resolvers: ((items: readonly ComboboxOption[]) => void)[] = [];
      const load = vi.fn(
        () =>
          new Promise<readonly ComboboxOption[]>((resolve) => {
            resolvers.push(resolve);
          }),
      );
      const { user } = renderWithDs(<Combobox label="Πελάτης" loadOptions={load} />);
      await user.type(input(), 'Γε');
      await waitFor(() => {
        expect(load).toHaveBeenCalledTimes(1);
      });
      act(() => {
        resolvers[0]?.(people.slice(0, 2));
      });
      await screen.findByRole('option', { name: 'Γεώργιος Παπαδόπουλος' });
      await user.type(input(), 'ω');
      const results = screen.getByRole('listbox').parentElement;
      expect(results).toHaveAttribute('aria-busy', 'true');
      expect(results).toHaveAttribute('data-stale', 'true');
      expect(screen.getAllByRole('option')).toHaveLength(2);
    });
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    const { user } = renderWithDs(<Combobox label="Customer" items={people} isRequired />);
    const box = screen.getByRole('combobox', { name: 'Customer required' });
    await user.type(box, 'zzz');
    expect(screen.getByRole('listbox')).toHaveTextContent('No results for “zzz”');
  });

  it('has no axe violations (closed and open)', async () => {
    const { container, user } = renderWithDs(
      <Combobox label="Πελάτης" items={people} description="Αναζήτηση με όνομα ή ΑΦΜ" isRequired />,
    );
    await expectNoA11yViolations(container);
    await user.type(input(), 'γεω');
    await expectNoA11yViolations(document.body);
  });
});
