import { screen, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Stepper } from './Stepper';
import { adjacentNavigableStep, defaultCanNavigateTo, type StepItem } from './stepperLogic';

const steps: StepItem[] = [
  { id: 'policyholder', label: 'Λήπτης', state: 'complete' },
  { id: 'vehicles', label: 'Οχήματα', state: 'complete', subLabel: '3 οχήματα · 2 οδηγοί' },
  { id: 'coverages', label: 'Καλύψεις', state: 'error', errorCount: 2, subLabel: '2 σφάλματα' },
  { id: 'underwriting', label: 'Ανάληψη', state: 'warning', subLabel: '1 ζήτημα ανάληψης' },
  { id: 'payment', label: 'Πληρωμή', state: 'upcoming' },
  {
    id: 'documents',
    label: 'Έγγραφα',
    state: 'locked',
    lockedReason: 'Απαιτείται έγκριση ανάληψης',
  },
  { id: 'review', label: 'Έλεγχος', state: 'upcoming' },
];

describe('stepper logic', () => {
  it('allows complete, warning and error steps; upcoming only after valid steps', () => {
    const can = (i: number) => {
      const step = steps[i];
      return step ? defaultCanNavigateTo(step, i, steps) : false;
    };
    expect(can(0)).toBe(true);
    expect(can(2)).toBe(true);
    expect(can(3)).toBe(true);
    expect(can(4)).toBe(false); // the error step before it is not valid
    expect(can(5)).toBe(false);
    const clean = steps.map((s, i) => (i < 4 ? { ...s, state: 'complete' as const } : s));
    const payment = clean[4];
    expect(payment && defaultCanNavigateTo(payment, 4, clean)).toBe(true);
  });

  it('finds the adjacent navigable step', () => {
    expect(adjacentNavigableStep(steps, 'coverages', 1)?.id).toBe('underwriting');
    expect(adjacentNavigableStep(steps, 'underwriting', 1)).toBeUndefined();
    expect(adjacentNavigableStep(steps, 'coverages', -1)?.id).toBe('vehicles');
  });
});

describe('Stepper', () => {
  it('is a nav with an ordered list and stateful step names', () => {
    renderWithDs(<Stepper steps={steps} currentId="coverages" onNavigate={vi.fn()} />);
    const nav = screen.getByRole('navigation', { name: 'Βήματα' });
    expect(within(nav).getByRole('list').tagName).toBe('OL');
    expect(within(nav).getAllByRole('listitem')).toHaveLength(7);
    expect(
      screen.getByRole('button', { name: 'Βήμα 2, Οχήματα, ολοκληρωμένο' }),
    ).toHaveAccessibleDescription('3 οχήματα · 2 οδηγοί');
    const current = screen.getByRole('button', { name: 'Βήμα 3, Καλύψεις, τρέχον' });
    expect(current).toHaveAttribute('aria-current', 'step');
  });

  it('navigates to allowed steps and blocks the others with a reason', async () => {
    const onNavigate = vi.fn();
    const { user } = renderWithDs(
      <Stepper steps={steps} currentId="coverages" onNavigate={onNavigate} />,
    );
    await user.click(screen.getByRole('button', { name: /Βήμα 1, Λήπτης/ }));
    expect(onNavigate).toHaveBeenLastCalledWith('policyholder');

    const locked = screen.getByRole('button', { name: 'Βήμα 6, Έγγραφα, κλειδωμένο' });
    expect(locked).toHaveAttribute('aria-disabled', 'true');
    expect(locked).toHaveAccessibleDescription('Απαιτείται έγκριση ανάληψης');
    await user.click(locked);
    const upcoming = screen.getByRole('button', { name: 'Βήμα 5, Πληρωμή, επόμενο' });
    expect(upcoming).toHaveAccessibleDescription('Ολοκληρώστε πρώτα τα προηγούμενα βήματα');
    await user.click(upcoming);
    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('activates with the keyboard and supports Alt+→ / Alt+←', async () => {
    function Wizard() {
      const [current, setCurrent] = useState('vehicles');
      return <Stepper steps={steps} currentId={current} onNavigate={setCurrent} />;
    }
    const { user } = renderWithDs(<Wizard />);
    await user.tab();
    expect(screen.getByRole('button', { name: /Βήμα 1/ })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(screen.getByRole('button', { name: /Βήμα 1, Λήπτης, τρέχον/ })).toHaveAttribute(
      'aria-current',
      'step',
    );
    await user.keyboard('{Alt>}{ArrowRight}{/Alt}');
    expect(screen.getByRole('button', { name: /Βήμα 2, Οχήματα, τρέχον/ })).toBeInTheDocument();
    await user.keyboard('{Alt>}{ArrowRight}{ArrowRight}{/Alt}');
    expect(screen.getByRole('button', { name: /Βήμα 4, Ανάληψη, τρέχον/ })).toBeInTheDocument();
    await user.keyboard('{Alt>}{ArrowLeft}{/Alt}');
    expect(screen.getByRole('button', { name: /Βήμα 3, Καλύψεις, τρέχον/ })).toBeInTheDocument();
  });

  it('renders links for steps with an href', () => {
    const linked = steps.map((s) => ({ ...s, href: `#${s.id}` }));
    renderWithDs(<Stepper steps={linked} currentId="coverages" onNavigate={vi.fn()} />);
    expect(screen.getByRole('link', { name: /Βήμα 1, Λήπτης/ })).toHaveAttribute(
      'href',
      '#policyholder',
    );
  });

  it('horizontal shows the current step ±1 and a counter', () => {
    renderWithDs(
      <Stepper
        steps={steps}
        currentId="underwriting"
        orientation="horizontal"
        onNavigate={vi.fn()}
      />,
    );
    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(screen.getByText('4 / 7')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByText('Βήμα 4 από 7')).toBeInTheDocument();
  });

  it('shows a spinner node while loading', () => {
    const loading: StepItem[] = [{ id: 'a', label: 'Τιμολόγηση', state: 'loading' }];
    renderWithDs(<Stepper steps={loading} currentId="a" />);
    expect(
      screen.getByRole('button', { name: 'Βήμα 1, Τιμολόγηση, φορτώνει' }),
    ).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Stepper steps={steps} currentId="coverages" onNavigate={vi.fn()} />);
    expect(screen.getByRole('navigation', { name: 'Steps' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Step 2, Οχήματα, complete' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderWithDs(<Stepper steps={steps} currentId="coverages" onNavigate={vi.fn()} />);
    await expectNoA11yViolations();
  });
});
