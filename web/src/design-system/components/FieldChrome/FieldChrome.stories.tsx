import type { Meta, StoryObj } from '@storybook/react-vite';
import { Group, Input, TextField } from 'react-aria-components';

import { cx } from '../../utils/cx';
import { FieldChromeLabel, FieldMessageList } from './FieldChrome';
import styles from './FieldChrome.module.css';
import { useFieldMessages, type FieldChromeMessagesInput } from './useFieldMessages';

function Demo(props: FieldChromeMessagesInput & { label: string; isRequired?: boolean }) {
  const messages = useFieldMessages(props);
  return (
    <TextField
      className={cx(styles.field)}
      isRequired={props.isRequired ?? false}
      isInvalid={messages.showError}
      validationBehavior="aria"
      {...(messages.describedBy ? { 'aria-describedby': messages.describedBy } : {})}
    >
      <FieldChromeLabel isRequired={props.isRequired}>{props.label}</FieldChromeLabel>
      <Group
        className={cx(styles.frame)}
        isInvalid={messages.showError}
        data-warning={messages.showWarning || undefined}
      >
        <Input className={cx(styles.input)} />
      </Group>
      <FieldMessageList messages={messages} {...props} />
    </TextField>
  );
}

/** The shared chrome of the advanced fields (label, frame states, messages). */
const meta = {
  title: 'Components/FieldChrome',
  component: Demo,
  args: { label: 'Αριθμός πλαισίου (VIN)', description: '17 χαρακτήρες' },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '320px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof Demo>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Helper: Story = { args: { isRequired: true } };

export const ErrorState: Story = {
  args: { errorMessage: 'Ο αριθμός πλαισίου έχει 16 χαρακτήρες. Συμπληρώστε και τους 17.' },
};

export const Warning: Story = { args: { warning: 'Ο αριθμός δεν βρέθηκε στο μητρώο οχημάτων.' } };

export const Info: Story = { args: { info: 'Από gov.gr Wallet' } };
