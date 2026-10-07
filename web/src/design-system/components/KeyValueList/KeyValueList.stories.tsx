import type { Meta, StoryObj } from '@storybook/react-vite';

import { Button } from '../Button';
import { KeyValueList, type KeyValueItem } from './index';

const wait = (ms: number) =>
  new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });

const items: KeyValueItem[] = [
  { id: 'product', label: 'Προϊόν', value: 'Αυτοκίνητο ΙΧ' },
  { id: 'package', label: 'Πακέτο', value: 'Πλήρες' },
  { id: 'rule', label: 'Κανόνας ανάληψης κινδύνου', value: 'UW-MOT-014 v7', kind: 'mono' },
  {
    id: 'payment',
    label: 'Τρόπος πληρωμής',
    value: 'Μηνιαία',
    edit: { value: 'Μηνιαία', onCommit: () => wait(600) },
  },
  {
    id: 'email',
    label: 'Email επικοινωνίας',
    value: 'g.papadopoulos@example.gr',
    edit: {
      value: 'g.papadopoulos@example.gr',
      onCommit: async () => {
        await wait(600);
        throw new Error('Η υπηρεσία επικοινωνίας δεν απάντησε. Η προηγούμενη τιμή διατηρήθηκε.');
      },
    },
  },
  {
    id: 'sum',
    label: 'Ασφαλιζόμενο κεφάλαιο',
    value: '20.000,00 €',
    requiresTransaction: { href: '#endorse' },
    explain: (
      <Button variant="link" size="sm">
        Γιατί;
      </Button>
    ),
  },
  { id: 'iban', label: 'IBAN', masked: { display: '•••• 4471' } },
  { id: 'notes', label: 'Σημειώσεις', value: null },
];

const meta = {
  title: 'Components/KeyValueList',
  component: KeyValueList,
  args: { items, 'aria-label': 'Στοιχεία ασφαλιστηρίου' },
} satisfies Meta<typeof KeyValueList>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const MoneyOnly: Story = {
  args: {
    moneyOnly: true,
    items: [
      { id: 'net', label: 'Καθαρό ασφάλιστρο', value: '358,59 €', kind: 'money' },
      { id: 'ipt', label: 'Φόρος ασφαλίστρων 15 %', value: '53,79 €', kind: 'money' },
      { id: 'total', label: 'Συνολικό ασφάλιστρο', value: '412,38 €', kind: 'money' },
    ],
  },
};
