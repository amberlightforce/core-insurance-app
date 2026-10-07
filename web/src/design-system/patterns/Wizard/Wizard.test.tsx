import { fireEvent, screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import type { StepItem } from '../../components/Stepper';
import { Wizard, type WizardCommit } from './Wizard';

const steps: StepItem[] = [
  { id: 'policyholder', label: 'Λήπτης ασφάλισης', state: 'complete' },
  { id: 'vehicles', label: 'Οχήματα', state: 'complete', subLabel: '1 όχημα' },
  { id: 'cover', label: 'Καλύψεις', state: 'upcoming' },
];

function Harness({ commit, onSaveDraft }: { commit: WizardCommit; onSaveDraft?: () => void }) {
  const [current, setCurrent] = useState('vehicles');
  return (
    <Wizard
      title="Νέα προσφορά · Αυτοκίνητο"
      steps={steps}
      currentId={current}
      onNavigate={setCurrent}
      commit={commit}
      lastSavedAt={new Date('2026-10-07T11:30:00Z')}
      {...(onSaveDraft ? { onSaveDraft } : {})}
      summary={<p>Συνολικό ασφάλιστρο 412,38 €</p>}
    >
      <label>
        Πινακίδα
        <input />
      </label>
    </Wizard>
  );
}

describe('Wizard', () => {
  it('shows the current step heading, back/next and the last saved time', () => {
    renderWithDs(<Harness commit={{ label: 'Δέσμευση', onCommit: vi.fn() }} />);
    expect(screen.getByRole('heading', { level: 2, name: 'Οχήματα' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Πίσω/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Επόμενο/ })).toBeInTheDocument();
    expect(screen.getByText('Αποθηκεύτηκε 14:30')).toBeInTheDocument();
    expect(screen.getByRole('complementary', { name: 'Σύνοψη' })).toBeInTheDocument();
  });

  it('moves with Next and Ctrl+Enter, then commits on the last step', async () => {
    const onCommit = vi.fn();
    const { user } = renderWithDs(<Harness commit={{ label: 'Δέσμευση', onCommit }} />);
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', ctrlKey: true });
    expect(screen.getByRole('heading', { level: 2, name: 'Καλύψεις' })).toBeInTheDocument();
    const commit = screen.getByRole('button', { name: /Δέσμευση/ });
    expect(commit).toHaveAttribute('data-variant', 'commit');
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', ctrlKey: true });
    expect(onCommit).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole('button', { name: /Πίσω/ }));
    expect(screen.getByRole('heading', { level: 2, name: 'Οχήματα' })).toBeInTheDocument();
  });

  it('keeps a blocked commit visible with its reason and does not run it', () => {
    const onCommit = vi.fn();
    renderWithDs(
      <Harness
        commit={{
          label: 'Δέσμευση',
          onCommit,
          disabledReason: 'Δεν μπορεί να γίνει δέσμευση: 2 ανοιχτά ζητήματα ανάληψης',
        }}
      />,
    );
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', ctrlKey: true });
    const commit = screen.getByRole('button', { name: /Δέσμευση/ });
    expect(commit).toHaveAttribute('aria-disabled', 'true');
    expect(commit).toHaveAccessibleDescription(/2 ανοιχτά ζητήματα/);
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', ctrlKey: true });
    expect(onCommit).not.toHaveBeenCalled();
  });

  it('uses «Αποστολή για έγκριση» when approval is needed and saves drafts', async () => {
    const onSaveDraft = vi.fn();
    const { user } = renderWithDs(
      <Harness
        commit={{ label: 'Δέσμευση', onCommit: vi.fn(), needsApproval: true }}
        onSaveDraft={onSaveDraft}
      />,
    );
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση πρόχειρου' }));
    expect(onSaveDraft).toHaveBeenCalled();
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', ctrlKey: true });
    expect(screen.getByRole('button', { name: /Αποστολή για έγκριση/ })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <Harness commit={{ label: 'Δέσμευση', onCommit: vi.fn() }} />,
    );
    await expectNoA11yViolations(container);
  });
});
