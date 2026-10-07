import type { Meta, StoryObj } from '@storybook/react-vite';

import { ChoiceCard, Radio, RadioGroup } from './RadioGroup';

const meta = {
  title: 'Components/RadioGroup',
  component: RadioGroup,
  args: {
    label: 'Πρόγραμμα πληρωμής',
    children: (
      <>
        <Radio value="annual">Εφάπαξ</Radio>
        <Radio value="semi" description="2 δόσεις">
          Εξαμηνιαία
        </Radio>
        <Radio value="quarterly" description="4 δόσεις">
          Τριμηνιαία
        </Radio>
        <Radio value="monthly" isDisabled>
          Μηνιαία
        </Radio>
      </>
    ),
  },
} satisfies Meta<typeof RadioGroup>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-stack-section)' }}>
      <RadioGroup label="Οριζόντια (≤ 3 επιλογές)" orientation="horizontal" defaultValue="yes">
        <Radio value="yes">Ναι</Radio>
        <Radio value="no">Όχι</Radio>
      </RadioGroup>
      <RadioGroup label="Σφάλμα" isRequired errorMessage="Επιλέξτε τη χρήση του οχήματος.">
        <Radio value="private">Ιδιωτική</Radio>
        <Radio value="professional">Επαγγελματική</Radio>
      </RadioGroup>
      <RadioGroup label="Ανενεργή ομάδα" isDisabled defaultValue="a">
        <Radio value="a">Α</Radio>
        <Radio value="b">Β</Radio>
      </RadioGroup>
      <RadioGroup label="Μόνο ανάγνωση" isReadOnly defaultValue="b">
        <Radio value="a">Εφάπαξ</Radio>
        <Radio value="b">Εξαμηνιαία</Radio>
      </RadioGroup>
    </div>
  ),
};

export const ChoiceCards: Story = {
  render: () => (
    <RadioGroup label="Πακέτο κάλυψης" variant="cards" defaultValue="comfort">
      <ChoiceCard
        value="basic"
        title="Βασικό"
        price="286,40 €"
        priceCaption="ετησίως, με φόρους"
        bullets={['Αστική ευθύνη', 'Νομική προστασία', 'Οδική βοήθεια 24/7']}
      />
      <ChoiceCard
        value="comfort"
        title="Άνετο"
        price="412,38 €"
        priceCaption="ετησίως, με φόρους"
        tag="Συνιστάται"
        bullets={['Όλα του Βασικού', 'Θραύση κρυστάλλων', 'Πυρκαγιά', 'Κλοπή']}
      />
      <ChoiceCard
        value="full"
        title="Πλήρες"
        price="689,10 €"
        priceCaption="ετησίως, με φόρους"
        bullets={[
          'Όλα του Άνετου',
          'Ίδιες ζημίες',
          'Φυσικά φαινόμενα',
          'Αυτοκίνητο αντικατάστασης',
        ]}
      />
    </RadioGroup>
  ),
};
