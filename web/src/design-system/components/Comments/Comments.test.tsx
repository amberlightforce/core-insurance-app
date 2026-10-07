import { screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import {
  CommentThread,
  MentionComposer,
  ResolvedCommentsToggle,
  filterPeople,
  insertMention,
  rebaseMentions,
  type CommentItem,
  type MentionPerson,
  type MentionValue,
} from './index';

const people: MentionPerson[] = [
  { id: 'p1', name: 'Μαρία Παπαδοπούλου', meta: 'Χειρίστρια ζημιών' },
  { id: 'p2', name: 'Κώστας Νικολάου' },
  { id: 'p3', name: 'Μάριος Αθανασίου' },
];

const now = new Date(2026, 9, 7, 14, 30);

const comments: CommentItem[] = [
  {
    id: 'c1',
    author: { id: 'p2', name: 'Κώστας Νικολάου' },
    at: new Date(2026, 9, 7, 14, 0),
    body: {
      text: '@Μαρία Παπαδοπούλου ελέγξτε το ποσοστό.',
      mentions: [{ id: 'p1', name: 'Μαρία Παπαδοπούλου', start: 0, end: 19 }],
    },
  },
  {
    id: 'c2',
    author: { id: 'p1', name: 'Μαρία Παπαδοπούλου' },
    at: new Date(2026, 9, 7, 14, 25),
    body: { text: 'Συμφωνώ.', mentions: [] },
  },
];

describe('mention model', () => {
  it('filters people accent- and case-insensitively', () => {
    expect(filterPeople(people, 'μαρ').map((p) => p.id)).toEqual(['p1', 'p3']);
    expect(filterPeople(people, 'ΝΙΚΟΛ').map((p) => p.id)).toEqual(['p2']);
    expect(filterPeople(people, 'αθανασιου').map((p) => p.id)).toEqual(['p3']);
  });

  it('inserts a mention token and rebases tokens after edits', () => {
    const start: MentionValue = { text: 'Γεια @μα', mentions: [] };
    const person = people[0] ?? { id: '', name: '' };
    const { value, caret } = insertMention(start, person, 5, 8);
    expect(value.text).toBe('Γεια @Μαρία Παπαδοπούλου ');
    expect(value.mentions).toEqual([{ id: 'p1', name: 'Μαρία Παπαδοπούλου', start: 5, end: 24 }]);
    expect(caret).toBe(25);
    // Typing before the mention shifts it; editing inside it drops it.
    expect(rebaseMentions(value, `Α ${value.text}`)[0]).toMatchObject({ start: 7, end: 26 });
    expect(rebaseMentions(value, value.text.replace('Μαρία', 'Μαρ'))).toEqual([]);
  });
});

function ControlledComposer({ onSubmit }: { onSubmit: (v: MentionValue) => void }) {
  const [value, setValue] = useState<MentionValue>({ text: '', mentions: [] });
  return (
    <>
      <MentionComposer
        label="Νέο σχόλιο"
        people={people}
        value={value}
        onChange={setValue}
        onSubmit={onSubmit}
      />
      <output data-testid="model">{JSON.stringify(value.mentions)}</output>
    </>
  );
}

describe('MentionComposer', () => {
  it('opens a listbox on @ with list-autocomplete semantics and inserts with Enter', async () => {
    const onSubmit = vi.fn();
    const { user } = renderWithDs(<ControlledComposer onSubmit={onSubmit} />);
    const box = screen.getByRole('textbox', { name: 'Νέο σχόλιο' });
    expect(box).toHaveAttribute('aria-autocomplete', 'list');
    expect(box).not.toHaveAttribute('aria-controls');
    await user.click(box);
    await user.type(box, 'Δείτε @μαρ');
    const listbox = screen.getByRole('listbox', { name: 'Πρόσωπα' });
    const options = within(listbox).getAllByRole('option');
    expect(options).toHaveLength(2);
    // D-FE-20: no aria-activedescendant; keyboard stays on the textarea and the highlight is announced.
    expect(box).not.toHaveAttribute('aria-activedescendant');
    expect(box).toHaveAccessibleDescription(/↑↓/);
    expect(options[0]).toHaveAttribute('aria-selected', 'true');
    await user.keyboard('{ArrowDown}');
    expect(options[1]).toHaveAttribute('aria-selected', 'true');
    expect(box).toHaveFocus();
    await waitFor(() => {
      expect(document.getElementById('ds-live-polite')).toHaveTextContent(/2 από 2/);
    });
    await user.keyboard('{ArrowUp}{Enter}');
    expect(box).toHaveValue('Δείτε @Μαρία Παπαδοπούλου ');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.getByTestId('model')).toHaveTextContent('"id":"p1"');
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onSubmit).toHaveBeenCalledWith({
      text: 'Δείτε @Μαρία Παπαδοπούλου ',
      mentions: [{ id: 'p1', name: 'Μαρία Παπαδοπούλου', start: 6, end: 25 }],
    });
  });

  it('closes the list with Esc and inserts with Tab', async () => {
    const { user } = renderWithDs(<ControlledComposer onSubmit={vi.fn()} />);
    const box = screen.getByRole('textbox');
    await user.click(box);
    await user.type(box, '@κω');
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).toBeNull();
    await user.type(box, 'σ');
    await user.keyboard('{Tab}');
    expect(box).toHaveValue('@Κώστας Νικολάου ');
  });
});

describe('CommentThread', () => {
  it('names the pin with the field and count and opens a non-modal thread', async () => {
    const { user } = renderWithDs(
      <CommentThread
        field="Ποσοστό απαλλαγής"
        comments={comments}
        currentUserId="p1"
        people={people}
        onSend={vi.fn()}
        now={now}
        hasUnread
      />,
    );
    const pin = screen.getByRole('button', { name: 'Σχόλια στο πεδίο Ποσοστό απαλλαγής, 2' });
    expect(pin).toHaveTextContent('2');
    await user.click(pin);
    const dialog = await screen.findByRole('dialog', { name: 'Σχόλια: Ποσοστό απαλλαγής' });
    expect(within(dialog).getAllByRole('listitem')).toHaveLength(2);
    expect(within(dialog).getByText('@Μαρία Παπαδοπούλου')).toBeInTheDocument();
    expect(within(dialog).getByText('πριν από 5 λεπτά')).toBeInTheDocument();
  });

  it('allows edit and delete only on own comments within the window', async () => {
    const onDelete = vi.fn();
    const { user } = renderWithDs(
      <CommentThread
        field="Απαλλαγή"
        comments={comments}
        currentUserId="p1"
        people={people}
        onSend={vi.fn()}
        onEdit={vi.fn(() => Promise.resolve())}
        onDelete={onDelete}
        now={now}
        defaultOpen
      />,
    );
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getAllByRole('button', { name: 'Διαγραφή σχολίου' })).toHaveLength(1);
    await user.click(within(dialog).getByRole('button', { name: 'Διαγραφή σχολίου' }));
    expect(onDelete).toHaveBeenCalledWith('c2');
  });

  it('sends optimistically and offers retry when sending fails', async () => {
    const onSend = vi
      .fn()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(undefined);
    const { user } = renderWithDs(
      <CommentThread
        field="Απαλλαγή"
        comments={[]}
        currentUserId="p1"
        people={people}
        onSend={onSend}
        now={now}
        defaultOpen
      />,
    );
    const dialog = await screen.findByRole('dialog');
    const box = within(dialog).getByRole('textbox', { name: 'Νέο σχόλιο' });
    await user.type(box, 'Εντάξει');
    await user.click(within(dialog).getByRole('button', { name: /Αποστολή/ }));
    expect(onSend).toHaveBeenCalledWith({ text: 'Εντάξει', mentions: [] });
    const failure = await within(dialog).findByText('Δεν στάλθηκε');
    expect(box).toHaveValue('');
    await user.click(within(failure).getByRole('button', { name: 'Επανάληψη' }));
    expect(onSend).toHaveBeenCalledTimes(2);
    await waitFor(() => {
      expect(within(dialog).queryByText('Δεν στάλθηκε')).toBeNull();
    });
  });

  it('hides resolved threads unless shown, and hides the composer when read-only', async () => {
    const { rerender, container } = renderWithDs(
      <CommentThread
        field="Α"
        comments={comments}
        currentUserId="p1"
        people={people}
        onSend={vi.fn()}
        isResolved
        now={now}
      />,
    );
    expect(container).toBeEmptyDOMElement();
    rerender(
      <CommentThread
        field="Α"
        comments={comments}
        currentUserId="p1"
        people={people}
        onSend={vi.fn()}
        isResolved
        showResolved
        isReadOnly
        now={now}
        defaultOpen
      />,
    );
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByRole('textbox')).toBeNull();
    expect(within(dialog).getByText('Η συζήτηση έχει επιλυθεί')).toBeInTheDocument();
  });

  it('toggles resolved threads with a pressed button', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <ResolvedCommentsToggle isSelected={false} onChange={onChange} count={3} />,
    );
    const toggle = screen.getByRole('button', { name: 'Εμφάνιση επιλυμένων (3)' });
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await user.click(toggle);
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <CommentThread
        field="Excess"
        comments={comments}
        currentUserId="p1"
        people={people}
        onSend={vi.fn()}
        now={now}
      />,
    );
    expect(screen.getByRole('button', { name: 'Comments on Excess, 2' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { baseElement } = renderWithDs(
      <CommentThread
        field="Απαλλαγή"
        comments={comments}
        currentUserId="p1"
        people={people}
        onSend={vi.fn()}
        onResolve={vi.fn()}
        now={now}
        defaultOpen
      />,
    );
    await screen.findByRole('dialog');
    await expectNoA11yViolations(baseElement);
  });
});
