import type { Meta, StoryObj } from '@storybook/react-vite';

import { Button } from '../Button';
import {
  EmptyState,
  ErrorState,
  IllAiWindow,
  IllCalmHorizon,
  IllCrackedHouse,
  IllEmptyArch,
  IllLockedDoor,
  IllMapFold,
  IllOfflineCloud,
  LoadingState,
  OfflineState,
  PermissionDenied,
  PermissionLimited,
  SkeletonBlock,
} from './index';

const meta = {
  title: 'Patterns/States',
  component: EmptyState,
  args: {
    kind: 'first-use',
    headline: 'Δεν έχετε ακόμη αποθηκευμένες προβολές',
    description: 'Αποθηκεύστε φίλτρα και στήλες για να επιστρέφετε με ένα κλικ.',
    action: <Button variant="primary">Δημιουργία προβολής</Button>,
    helpHref: '#help',
  },
} satisfies Meta<typeof EmptyState>;

export default meta;
type Story = StoryObj<typeof meta>;

export const FirstUseEmpty: Story = {};

export const DoneEmpty: Story = {
  render: () => <EmptyState kind="done" headline="Η ουρά σας είναι άδεια. Καλή δουλειά." />,
};

export const FilteredEmpty: Story = {
  render: () => (
    <EmptyState
      kind="filtered"
      filters={[
        { label: 'Κατάσταση', value: 'Ακυρώθηκε' },
        { label: 'Παραγωγός', value: '10233' },
      ]}
      onClearFilters={() => undefined}
    />
  ),
};

export const Loading: Story = {
  render: () => (
    <LoadingState immediate>
      <SkeletonBlock width="40%" height="16px" />
      <SkeletonBlock width="88%" />
      <SkeletonBlock width="72%" />
    </LoadingState>
  ),
};

export const LoadingLong: Story = {
  render: () => (
    <LoadingState
      kind="long"
      stage={{ current: 2, total: 4, label: 'Δημιουργία αναφοράς…' }}
      estimate="περίπου 1 λεπτό"
      action={
        <Button variant="ghost" size="sm">
          Ειδοποίηση όταν ολοκληρωθεί
        </Button>
      }
    />
  ),
};

export const RecoverableError: Story = {
  render: () => (
    <ErrorState
      message="Δεν ήταν δυνατή η φόρτωση των πληρωμών."
      onRetry={() => undefined}
      correlationId="4f2a7c1e-91c0"
    />
  ),
};

export const PageError: Story = {
  render: () => (
    <ErrorState
      scope="page"
      homeHref="#home"
      onReport={() => undefined}
      correlationId="00-4f2a…91c0-01"
    />
  ),
};

export const Permission: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <PermissionDenied
        restriction="Δεν έχετε πρόσβαση στις αναφορές Solvency II."
        grantor="ο υπεύθυνος ρόλων της Οικονομικής Διεύθυνσης"
        onRequestAccess={() => new Promise((resolve) => setTimeout(resolve, 800))}
      />
      <PermissionLimited maskedValue="•••• 4471" />
    </div>
  ),
};

export const Offline: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <OfflineState lastSynced="14:32" />
      <OfflineState variant="page" lastSynced="14:32" />
    </div>
  ),
};

export const Illustrations: Story = {
  render: () => (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 'var(--space-4)' }}>
      <IllEmptyArch />
      <IllLockedDoor />
      <IllCalmHorizon />
      <IllMapFold />
      <IllCrackedHouse />
      <IllOfflineCloud />
      <IllAiWindow />
    </div>
  ),
};
