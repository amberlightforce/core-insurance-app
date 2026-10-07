import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { Button } from '../Button';
import { Drawer, type DrawerProps } from './Drawer';

function DrawerDemo(props: Partial<DrawerProps>) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        onPress={() => {
          setOpen(true);
        }}
      >
        Φίλτρα
      </Button>
      <Drawer
        title="Φίλτρα αναζήτησης"
        isOpen={open}
        onOpenChange={setOpen}
        footer={
          <>
            <Button variant="ghost">Εκκαθάριση</Button>
            <Button variant="primary">Εφαρμογή (124)</Button>
          </>
        }
        {...props}
      >
        <p>Κατάσταση · Παραγωγός · Ημερομηνία έναρξης</p>
      </Drawer>
    </>
  );
}

const meta = {
  title: 'Components/Drawer',
  component: DrawerDemo,
  args: { isModal: true, width: 360, status: 'ready' },
  argTypes: {
    width: { control: 'inline-radio', options: [320, 360, 400] },
    status: { control: 'inline-radio', options: ['ready', 'loading', 'error'] },
  },
} satisfies Meta<typeof DrawerDemo>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Modal: Story = {};
export const NonModal: Story = { args: { isModal: false, width: 400 } };
export const Loading: Story = { args: { status: 'loading' } };
export const ErrorState: Story = { args: { status: 'error', onRetry: () => undefined } };
