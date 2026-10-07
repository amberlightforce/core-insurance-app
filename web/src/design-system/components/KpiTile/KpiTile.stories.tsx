import type { Meta, StoryObj } from '@storybook/react-vite';

import { HeroMetric, KpiTile } from './index';

const meta = {
  title: 'Components/KpiTile',
  component: KpiTile,
  args: {
    label: 'Ασφάλιστρα νέας παραγωγής',
    value: 1284390,
    format: 'money',
    delta: { value: 4.2, basis: 'vs προηγ. μήνα' },
    trend: [1020000, 1080000, 1050000, 1150000, 1210000, 1284390],
    asOf: '14:32',
    period: 'Οκτώβριος 2026',
    definition: {
      formula: 'Σύνολο εγγεγραμμένων ασφαλίστρων νέων συμβολαίων',
      source: 'mart.pol_new_business',
      asOf: 'Δεδομένα έως 06/10/2026',
    },
  },
  argTypes: {
    state: { control: 'inline-radio', options: ['ready', 'loading', 'error'] },
    format: { control: 'inline-radio', options: ['money', 'count', 'percent', 'number'] },
  },
} satisfies Meta<typeof KpiTile>;

export default meta;
type Story = StoryObj<typeof meta>;

const grid = {
  display: 'grid',
  gap: 'var(--space-4)',
  gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))',
} as const;

export const Playground: Story = {};

export const Tiles: Story = {
  render: () => (
    <div style={grid}>
      <KpiTile
        label="Ασφάλιστρα"
        value={1284390}
        format="money"
        delta={{ value: 4.2, basis: 'vs προηγ. μήνα' }}
        trend={[1.02e6, 1.1e6, 1.08e6, 1.28e6]}
        asOf="14:32"
        href="#report"
      />
      <KpiTile
        label="Δείκτης ζημιών"
        value={68.4}
        format="percent"
        higherIsBetter={false}
        delta={{ value: 2.1, unit: 'points', basis: 'vs Σεπ' }}
        trend={[64, 65, 67, 68.4]}
        asOf="14:32"
      />
      <KpiTile
        label="Ανοιχτές ζημίες"
        value={4812}
        format="count"
        higherIsBetter={false}
        delta={{ value: -3.5, basis: 'vs προηγ. εβδ.' }}
        trend={[5100, 5000, 4900, 4812]}
        asOf="14:32"
      />
      <KpiTile
        label="Μέσος χρόνος διακανονισμού"
        value={12}
        unit="ημ."
        asOf="Δεδομένα έως 06/10/2026"
        isStale
      />
    </div>
  ),
};

export const States: Story = {
  render: () => (
    <div style={grid}>
      <KpiTile label="Φόρτωση" value={null} state="loading" />
      <KpiTile label="Σφάλμα" value={null} state="error" onRetry={() => undefined} />
      <KpiTile label="Μη ενημερωμένο" value={412} format="count" asOf="09:10" isStale />
    </div>
  ),
};

export const Hero: Story = {
  render: () => (
    <HeroMetric
      label="Ασφάλιστρα νέας παραγωγής Οκτ."
      value={1284390}
      fullValue="1.284.390 €"
      comparison="104 % του πλάνου"
      trend={[0.8e6, 0.9e6, 1.0e6, 1.1e6, 1.2e6, 1.284e6]}
      asOf="14:32"
    />
  ),
};
