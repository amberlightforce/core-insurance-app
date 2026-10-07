import type { Meta, StoryObj } from '@storybook/react-vite';

import { Checkbox, CheckboxGroup } from './Checkbox';

const meta = {
  title: 'Components/Checkbox',
  component: Checkbox,
  args: { children: 'Οδική βοήθεια' },
} satisfies Meta<typeof Checkbox>;

export default meta;
type Story = StoryObj<typeof meta>;

const column = { display: 'grid', gap: 'var(--space-2)', maxInlineSize: '420px' } as const;

export const Playground: Story = {};

export const States: Story = {
  render: () => (
    <div style={column}>
      <Checkbox>Χωρίς επιλογή</Checkbox>
      <Checkbox defaultSelected>Επιλεγμένο</Checkbox>
      <Checkbox isIndeterminate>Επιλογή όλων (μερική)</Checkbox>
      <Checkbox description="Έως 3 συμβάντα ανά έτος">Με περιγραφή</Checkbox>
      <Checkbox isInvalid>Με σφάλμα</Checkbox>
      <Checkbox isDisabled>Ανενεργό</Checkbox>
      <Checkbox isDisabled defaultSelected>
        Ανενεργό επιλεγμένο
      </Checkbox>
      <Checkbox isReadOnly defaultSelected>
        Μόνο ανάγνωση (επιλεγμένο)
      </Checkbox>
      <Checkbox isReadOnly>Μόνο ανάγνωση (χωρίς επιλογή)</Checkbox>
    </div>
  ),
};

export const Group: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-stack-section)' }}>
      <CheckboxGroup
        label="Πρόσθετες καλύψεις"
        helperText="Επιλέξτε όσες χρειάζεστε."
        defaultValue={['road']}
      >
        <Checkbox value="road">Οδική βοήθεια</Checkbox>
        <Checkbox value="glass">Θραύση κρυστάλλων</Checkbox>
        <Checkbox value="fire">Πυρκαγιά</Checkbox>
      </CheckboxGroup>
      <CheckboxGroup
        label="Συναινέσεις"
        isRequired
        errorMessage="Επιλέξτε τουλάχιστον έναν τρόπο επικοινωνίας."
      >
        <Checkbox value="email">Email</Checkbox>
        <Checkbox value="sms">SMS</Checkbox>
      </CheckboxGroup>
      <CheckboxGroup label="Οριζόντια" orientation="horizontal">
        <Checkbox value="a">Α</Checkbox>
        <Checkbox value="b">Β</Checkbox>
      </CheckboxGroup>
      <CheckboxGroup label="Μόνο ανάγνωση" isReadOnly defaultValue={['road']}>
        <Checkbox value="road">Οδική βοήθεια</Checkbox>
        <Checkbox value="glass">Θραύση κρυστάλλων</Checkbox>
      </CheckboxGroup>
    </div>
  ),
};
