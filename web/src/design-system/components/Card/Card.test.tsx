import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { Card, WorkLeftCard } from './index';

describe('Card', () => {
  it('renders a static card as a named group with header, body and footer', () => {
    renderWithDs(
      <Card title="Στοιχεία οχήματος" meta="Ενημέρωση 14:32" footer={<span>Υποσέλιδο</span>}>
        ΙΧ επιβατικό
      </Card>,
    );
    const card = screen.getByRole('group', { name: 'Στοιχεία οχήματος' });
    expect(card).toHaveAttribute('data-variant', 'static');
    expect(
      screen.getByRole('heading', { level: 3, name: 'Στοιχεία οχήματος' }),
    ).toBeInTheDocument();
    expect(card).toHaveTextContent('ΙΧ επιβατικό');
    expect(card).toHaveTextContent('Υποσέλιδο');
  });

  it('marks on-canvas cards', () => {
    renderWithDs(<Card title="Α" onCanvas />);
    expect(screen.getByRole('group', { name: 'Α' })).toHaveAttribute('data-on-canvas', 'true');
  });

  it('makes an interactive card one stretched link and keeps secondary actions separate', async () => {
    const onPress = vi.fn();
    const onAction = vi.fn();
    const { user } = renderWithDs(
      <Card
        variant="interactive"
        title="ΑΣΦ-2026-0412"
        onPress={onPress}
        actions={
          <Button variant="ghost" size="sm" onPress={onAction}>
            Αντιγραφή
          </Button>
        }
      >
        Ανανέωση σε 23 ημέρες
      </Card>,
    );
    const link = screen.getByRole('link', { name: 'ΑΣΦ-2026-0412' });
    await user.tab();
    expect(link).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onPress).toHaveBeenCalledOnce();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Αντιγραφή' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onAction).toHaveBeenCalledOnce();
    expect(onPress).toHaveBeenCalledOnce();
  });

  it('uses an href for navigation links', () => {
    renderWithDs(<Card variant="interactive" title="Ζημία 4471" href="/claims/4471" />);
    expect(screen.getByRole('link', { name: 'Ζημία 4471' })).toHaveAttribute(
      'href',
      '/claims/4471',
    );
  });

  it('shows selection styling for selectable cards', () => {
    renderWithDs(<Card variant="selectable" title="Πακέτο Βασικό" isSelected />);
    expect(screen.getByRole('group', { name: 'Πακέτο Βασικό' })).toHaveAttribute(
      'data-selected',
      'true',
    );
  });

  it('renders loading, error and empty states', async () => {
    const onRetry = vi.fn();
    const { rerender, user } = renderWithDs(<Card title="Πληρωμές" state="loading" />);
    expect(screen.getByText('Φόρτωση…').parentElement).toHaveAttribute('aria-busy', 'true');

    rerender(<Card title="Πληρωμές" state="error" onRetry={onRetry} />);
    expect(screen.getByRole('alert')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalledOnce();

    rerender(
      <Card title="Πληρωμές" state="empty" emptyAction={<Button size="sm">Νέα πληρωμή</Button>} />,
    );
    expect(screen.getByText('Δεν υπάρχουν δεδομένα')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Νέα πληρωμή' })).toBeInTheDocument();
  });

  it('renders the empty text in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Card title="Payments" state="empty" />);
    expect(screen.getByText('No data')).toBeInTheDocument();
  });
});

describe('WorkLeftCard', () => {
  it('shows the count, at most three items and a «Συνέχεια» link named after the card', async () => {
    const onContinue = vi.fn();
    const { user } = renderWithDs(
      <WorkLeftCard
        title="Παραπομπές"
        family="warning"
        count={1204}
        items={[
          { id: '1', label: 'Παπαδόπουλος Γ.' },
          { id: '2', label: 'Νικολάου Κ.' },
          { id: '3', label: 'Γεωργίου Μ.' },
          { id: '4', label: 'Δεν εμφανίζεται' },
        ]}
        onContinue={onContinue}
      />,
    );
    const card = screen.getByRole('group', { name: 'Παραπομπές' });
    expect(card).toHaveAttribute('data-variant', 'work-left');
    expect(card).toHaveTextContent('1.204');
    expect(card).toHaveTextContent('1.204 εκκρεμότητες');
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
    expect(screen.queryByText('Δεν εμφανίζεται')).toBeNull();
    await user.click(screen.getByRole('link', { name: 'Συνέχεια: Παραπομπές' }));
    expect(onContinue).toHaveBeenCalledOnce();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <Card title="Στατική">Κείμενο</Card>
        <Card variant="interactive" title="Διαδραστική" href="/x" />
        <Card title="Σφάλμα" state="error" onRetry={() => undefined} />
        <WorkLeftCard title="Εργασίες" family="danger" count={3} continueHref="/tasks" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
