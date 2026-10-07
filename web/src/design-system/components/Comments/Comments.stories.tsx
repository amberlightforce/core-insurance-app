import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import {
  CommentThread,
  MentionComposer,
  ResolvedCommentsToggle,
  type CommentItem,
  type MentionPerson,
  type MentionValue,
} from './index';

const people: MentionPerson[] = [
  { id: 'p1', name: 'Μαρία Παπαδοπούλου', meta: 'Χειρίστρια ζημιών' },
  { id: 'p2', name: 'Κώστας Νικολάου', meta: 'Ανάληψη κινδύνου' },
  { id: 'p3', name: 'Μάριος Αθανασίου', meta: 'Οικονομική Διεύθυνση' },
];

const now = new Date(2026, 9, 7, 14, 30);

const comments: CommentItem[] = [
  {
    id: 'c1',
    author: { id: 'p2', name: 'Κώστας Νικολάου' },
    at: new Date(2026, 9, 7, 13, 50),
    body: {
      text: '@Μαρία Παπαδοπούλου το ποσοστό απαλλαγής δεν ταιριάζει με το πακέτο.',
      mentions: [{ id: 'p1', name: 'Μαρία Παπαδοπούλου', start: 0, end: 19 }],
    },
  },
  {
    id: 'c2',
    author: { id: 'p1', name: 'Μαρία Παπαδοπούλου' },
    at: new Date(2026, 9, 7, 14, 25),
    body: { text: 'Το διορθώνω με πρόσθετη πράξη.', mentions: [] },
  },
];

const meta = {
  title: 'Components/Comments',
  component: CommentThread,
  args: {
    field: 'Ποσοστό απαλλαγής',
    comments,
    currentUserId: 'p1',
    people,
    now,
    onSend: () => Promise.resolve(),
    onEdit: () => Promise.resolve(),
    onDelete: () => undefined,
    onResolve: () => undefined,
    hasUnread: true,
    defaultOpen: true,
  },
} satisfies Meta<typeof CommentThread>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Thread: Story = {};

export const SendFails: Story = {
  args: { onSend: () => Promise.reject(new Error('offline')) },
};

export const ReadOnly: Story = { args: { isReadOnly: true } };

export const Resolved: Story = {
  render: function Render(args) {
    const [show, setShow] = useState(false);
    return (
      <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
        <ResolvedCommentsToggle isSelected={show} onChange={setShow} count={1} />
        <CommentThread
          {...args}
          isResolved
          showResolved={show}
          defaultOpen={false}
          onReopen={() => undefined}
        />
      </div>
    );
  },
};

export const Composer: Story = {
  render: function Render() {
    const [value, setValue] = useState<MentionValue>({ text: '', mentions: [] });
    return (
      <div style={{ maxInlineSize: 360 }}>
        <MentionComposer
          label="Νέο σχόλιο"
          people={people}
          value={value}
          onChange={setValue}
          placeholder="Πληκτρολογήστε @"
        />
        <pre>{JSON.stringify(value, null, 2)}</pre>
      </div>
    );
  },
};
