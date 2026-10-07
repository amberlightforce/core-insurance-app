import type { Meta, StoryObj } from '@storybook/react-vite';
import { Download, UserCheck, UserPlus } from 'lucide-react';
import { useState } from 'react';

import { Button } from '../Button';
import { StatusPill } from '../StatusPill';
import type { EntityState } from '../StatusPill';
import {
  booleanColumn,
  dateColumn,
  identifierColumn,
  moneyColumn,
  percentColumn,
  priorityColumn,
  queueColumn,
  statusColumn,
  textColumn,
} from './columns';
import { DataTable } from './DataTable';

/* ---------- UW referral queue (two-line rows) ---------- */

interface Referral {
  id: string;
  client: string;
  product: string;
  producer: string;
  priority: number;
  slaDays: number;
  sla: EntityState<'clockInstance'>;
  premium: number;
  issues: number;
}

const clients = [
  'Παπαδόπουλος Γεώργιος',
  'Κωνσταντίνου Ελένη',
  'Άλφα Μεταφορική Α.Ε.',
  'Νικολάου Δημήτρης',
  'Βασιλείου Μαρία',
  'Οικονόμου Αικατερίνη',
  'Ζαχαρίου Παναγιώτης',
  'Θεοδωρίδης Ιωάννης',
];
const products = ['Αυτοκίνητο ΙΧ', 'Κατοικία', 'Περιουσία επιχειρήσεων', 'Στόλος οχημάτων'];

const referrals: Referral[] = clients.map((client, i) => {
  const slaDays = [0, 1, 2, 3, 6, 9, 12, 20][i] ?? 5;
  return {
    id: `ΠΑΡ-2026-${String(412 + i).padStart(4, '0')}`,
    client,
    product: products[i % products.length] ?? 'Κατοικία',
    producer: `Παραγωγός ${String(10230 + i)}`,
    priority: [92, 81, 77, 64, 58, 41, 33, 12][i] ?? 50,
    slaDays,
    sla: slaDays === 0 ? 'breached' : slaDays <= 2 ? 'warned' : 'running',
    premium: [1840.5, 612.2, 12480, 388.9, 990, 2310.75, 455, 7200][i] ?? 500,
    issues: (i % 3) + 1,
  };
});

const referralColumns = [
  queueColumn<Referral>(
    'client',
    'Πελάτης',
    (r) => ({ primary: r.client, id: r.id, fact: r.product }),
    {
      size: 260,
    },
  ),
  priorityColumn<Referral>('priority', 'Προτεραιότητα', (r) => r.priority, { size: 132 }),
  statusColumn<Referral>(
    'sla',
    'SLA',
    (r) => r.sla,
    (r) =>
      r.sla === 'breached' ? (
        <StatusPill entity="clockInstance" state="breached" size="sm" announceChanges={false} />
      ) : (
        <StatusPill
          entity="clockInstance"
          state={r.sla}
          size="sm"
          countdownDays={r.slaDays}
          variant="dot"
          showLabel
          announceChanges={false}
        />
      ),
    {
      size: 140,
      meta: {
        filter: 'enum',
        enumOptions: [
          { value: 'running', label: 'Σε εξέλιξη' },
          { value: 'warned', label: 'Πλησιάζει η προθεσμία' },
          { value: 'breached', label: 'Παραβίαση προθεσμίας' },
        ],
      },
    },
  ),
  moneyColumn<Referral>('premium', 'Ασφάλιστρο', (r) => r.premium, { size: 120 }),
];

const meta = {
  title: 'Components/DataTable',
  parameters: { layout: 'fullscreen' },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

/** UW workbench queue (v3): two-line rows, priority bar, SLA pill, premium; J/K drives the detail. */
export const ReferralQueue: Story = {
  render: function Render() {
    const [current, setCurrent] = useState<Referral | null>(null);
    const [selected, setSelected] = useState<string[]>([]);
    return (
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'minmax(540px, 620px) 1fr',
          gap: 'var(--space-4)',
          padding: 'var(--space-4)',
        }}
      >
        <DataTable<Referral>
          aria-label="Οι παραπομπές μου"
          columns={referralColumns}
          data={referrals}
          getRowId={(r) => r.id}
          rowVariant="queue"
          selectable
          selectedIds={selected}
          onSelectionChange={setSelected}
          onCursorChange={setCurrent}
          onOpen={setCurrent}
          initialSorting={[{ id: 'priority', desc: true }]}
          hiddenNormalCount={4721}
          onShowAll={() => undefined}
          showDensityToggle={false}
          bulkActions={[
            { id: 'assign', label: 'Ανάθεση', icon: UserPlus, onAction: () => undefined },
            {
              id: 'approve',
              label: 'Αποστολή για έγκριση',
              icon: UserCheck,
              onAction: () => undefined,
            },
          ]}
          footerTotal={{
            label: 'Σύνολο ασφαλίστρων',
            value: referrals.reduce((s, r) => s + r.premium, 0),
          }}
          height="520px"
        />
        <section aria-label="Λεπτομέρειες παραπομπής" style={{ padding: 'var(--space-4)' }}>
          {current ? (
            <>
              <p className="ds-overline">ΠΑΡΑΠΟΜΠΗ</p>
              <h2 className="ds-heading-2">{current.client}</h2>
              <p className="ds-mono">{current.id}</p>
              <StatusPill entity="job" state="referred" />
            </>
          ) : (
            <p className="ds-caption">Επιλέξτε μια παραπομπή (J/K).</p>
          )}
        </section>
      </div>
    );
  },
};

/* ---------- 10,000-row virtualised policy list ---------- */

interface Policy {
  id: string;
  holder: string;
  branch: string;
  status: EntityState<'policyTerm'>;
  premium: number;
  commission: number;
  start: string;
  paperless: boolean;
}

const surnames = [
  'Παπαδόπουλος',
  'Γεωργίου',
  'Δημητρίου',
  'Ιωάννου',
  'Κωνσταντίνου',
  'Νικολάου',
  'Αλεξίου',
];
const names = ['Γεώργιος', 'Μαρία', 'Ελένη', 'Νίκος', 'Κατερίνα', 'Δημήτρης', 'Σοφία'];
const branches = ['Αυτοκίνητο', 'Κατοικία', 'Περιουσία', 'Υγεία', 'Αστική ευθύνη'];
const states: EntityState<'policyTerm'>[] = [
  'inForce',
  'inForce',
  'inForce',
  'pendingCancellation',
  'cancelled',
  'lapsed',
  'scheduled',
  'expired',
];

const manyPolicies: Policy[] = Array.from({ length: 10_000 }, (_, i) => ({
  id: `ΑΣΦ-2026-${String(i + 1).padStart(6, '0')}`,
  holder: `${surnames[i % surnames.length] ?? ''} ${names[(i * 3) % names.length] ?? ''}`,
  branch: branches[i % branches.length] ?? 'Αυτοκίνητο',
  status: states[(i * 7) % states.length] ?? 'inForce',
  premium: Math.round(((i * 7919) % 250000) / 1.7) / 100,
  commission: ((i * 13) % 2000) / 100,
  start: `2026-${String((i % 12) + 1).padStart(2, '0')}-${String((i % 28) + 1).padStart(2, '0')}`,
  paperless: i % 3 !== 0,
}));

const policyColumns = [
  identifierColumn<Policy>('id', 'Ασφαλιστήριο', (r) => r.id, { size: 160 }),
  textColumn<Policy>('holder', 'Λήπτης', (r) => r.holder, { size: 220, meta: { filter: 'text' } }),
  textColumn<Policy>('branch', 'Κλάδος', (r) => r.branch, {
    size: 140,
    meta: { filter: 'enum', enumOptions: branches.map((b) => ({ value: b, label: b })) },
  }),
  statusColumn<Policy>(
    'status',
    'Κατάσταση',
    (r) => r.status,
    (r) => <StatusPill entity="policyTerm" state={r.status} size="sm" announceChanges={false} />,
    { size: 180 },
  ),
  moneyColumn<Policy>('premium', 'Ασφάλιστρο', (r) => r.premium, { size: 130 }),
  percentColumn<Policy>('commission', 'Προμήθεια', (r) => r.commission, { size: 110 }),
  dateColumn<Policy>('start', 'Έναρξη', (r) => r.start, { size: 120 }),
  booleanColumn<Policy>('paperless', 'Ηλεκτρονικά', (r) => r.paperless, { size: 110 }),
];

export const VirtualisedPolicies: Story = {
  render: () => (
    <div style={{ padding: 'var(--space-4)' }}>
      <DataTable<Policy>
        aria-label="Ασφαλιστήρια"
        columns={policyColumns}
        data={manyPolicies}
        getRowId={(r) => r.id}
        selectable
        pinnedColumns={{ left: ['id'] }}
        height="640px"
        totalCount={10_000}
        onSelectAllMatching={() => undefined}
        exportSlot={
          <Button variant="ghost" size="sm" icon={Download}>
            Εξαγωγή
          </Button>
        }
        bulkActions={[
          { id: 'export', label: 'Εξαγωγή', icon: Download, onAction: () => undefined },
        ]}
      />
    </div>
  ),
};

export const Grouped: Story = {
  render: () => (
    <DataTable<Policy>
      aria-label="Ασφαλιστήρια ανά κλάδο"
      columns={policyColumns}
      data={manyPolicies.slice(0, 60)}
      getRowId={(r) => r.id}
      grouping={['branch']}
    />
  ),
};

export const Paged: Story = {
  render: () => (
    <DataTable<Policy>
      aria-label="Ασφαλιστήρια"
      columns={policyColumns}
      data={manyPolicies.slice(0, 480)}
      getRowId={(r) => r.id}
      pagination={{ pageSize: 25 }}
    />
  ),
};

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)', padding: 'var(--space-4)' }}>
      <DataTable<Policy>
        aria-label="Φόρτωση"
        columns={policyColumns}
        data={[]}
        getRowId={(r) => r.id}
        isLoading
      />
      <DataTable<Policy>
        aria-label="Κενός πίνακας"
        columns={policyColumns}
        data={[]}
        getRowId={(r) => r.id}
        emptyState={<p style={{ margin: 0 }}>Δεν υπάρχουν παραπομπές</p>}
      />
      <DataTable<Policy>
        aria-label="Σφάλμα και αλλαγές"
        columns={policyColumns}
        data={manyPolicies.slice(0, 6)}
        getRowId={(r) => r.id}
        isRefetching
        error={{ onRetry: () => undefined }}
        staleCount={12}
        onRefresh={() => undefined}
        permissionLimited
        selectable
        getRowState={(r) =>
          r.id.endsWith('2')
            ? { lockedReason: 'Κλειδωμένο από Μ. Παπαδοπούλου' }
            : r.id.endsWith('3')
              ? { error: { message: 'Η ενημέρωση απέτυχε.', onRetry: () => undefined } }
              : r.id.endsWith('4')
                ? { isNew: true }
                : undefined
        }
      />
    </div>
  ),
};
