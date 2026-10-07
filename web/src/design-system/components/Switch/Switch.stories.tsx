import type { Meta, StoryObj } from '@storybook/react-vite';

import { Switch } from './Switch';

const meta = {
  title: 'Components/Switch',
  component: Switch,
  args: { children: 'Εορτασμοί', showStateText: true },
} satisfies Meta<typeof Switch>;

export default meta;
type Story = StoryObj<typeof meta>;

const column = { display: 'grid', gap: 'var(--space-3)', justifyItems: 'start' } as const;

function wait(ms: number) {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });
}

export const Playground: Story = {};

export const States: Story = {
  render: () => (
    <div style={column}>
      <Switch>Ανενεργό</Switch>
      <Switch defaultSelected>Ενεργό</Switch>
      <Switch showStateText defaultSelected description="Κινούμενα εφέ σε ορόσημα">
        Εορτασμοί
      </Switch>
      <Switch disabledReason="Η ρύθμιση ορίζεται από τον διαχειριστή.">Βοηθός ΤΝ</Switch>
      <Switch isDisabled defaultSelected>
        Χωρίς αιτία
      </Switch>
      <Switch isPending defaultSelected>
        Αποθήκευση σε εξέλιξη
      </Switch>
      <Switch isReadOnly defaultSelected>
        Μόνο ανάγνωση (ενεργό)
      </Switch>
      <Switch isReadOnly>Μόνο ανάγνωση (ανενεργό)</Switch>
    </div>
  ),
};

export const ServerPersisted: Story = {
  render: () => (
    <div style={column}>
      <Switch onChangeAsync={() => wait(1200)} showStateText>
        Ειδοποιήσεις email (αποθηκεύεται)
      </Switch>
      <Switch
        onChangeAsync={() => wait(1200).then(() => Promise.reject(new Error('503')))}
        showStateText
      >
        Ειδοποιήσεις SMS (αποτυγχάνει και επανέρχεται)
      </Switch>
    </div>
  ),
};
